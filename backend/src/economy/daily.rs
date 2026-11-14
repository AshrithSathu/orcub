use super::*;
use chrono::NaiveDate;

pub(crate) fn date(s: &str) -> Result<NaiveDate> {
    let d = NaiveDate::parse_from_str(s, "%Y-%m-%d").map_err(|_| ApiError::bad("utc_day"))?;
    if s.len() != 10 || d.format("%Y-%m-%d").to_string() != s {
        return Err(ApiError::bad("utc_day"));
    }
    Ok(d)
}

#[derive(Default)]
struct Streak {
    last: Option<NaiveDate>,
    count: i32,
    freeze: bool,
    earned: bool,
}
impl Streak {
    fn advance(&mut self, day: NaiveDate) -> Result<()> {
        let gap = self.last.map(|last| (day - last).num_days());
        if gap.is_some_and(|g| g <= 0) {
            return Err(ApiError(StatusCode::CONFLICT, "daily history out of order"));
        }
        self.count = match gap {
            Some(1) => self.count.checked_add(1),
            Some(2) if self.freeze => {
                self.freeze = false;
                self.count.checked_add(1)
            }
            _ => Some(1),
        }
        .ok_or(ApiError::bad("streak overflow"))?;
        if self.count >= 7 && !self.earned {
            self.freeze = true;
            self.earned = true;
        }
        self.last = Some(day);
        Ok(())
    }
    async fn load(tx: &mut Tx<'_>, id: Uuid) -> Result<Self> {
        sqlx::query("INSERT INTO daily_streaks(player_id) VALUES($1) ON CONFLICT DO NOTHING")
            .bind(id)
            .execute(&mut **tx)
            .await?;
        let r = sqlx::query("SELECT * FROM daily_streaks WHERE player_id=$1 FOR UPDATE")
            .bind(id)
            .fetch_one(&mut **tx)
            .await?;
        Ok(Self {
            last: r.get("last_day"),
            count: r.get("streak"),
            freeze: r.get("freeze"),
            earned: r.get("freeze_earned"),
        })
    }
    async fn store(&self, tx: &mut Tx<'_>, id: Uuid) -> Result<()> {
        sqlx::query("INSERT INTO daily_streaks(player_id,last_day,streak,\"freeze\",freeze_earned) VALUES($1,$2,$3,$4,$5) ON CONFLICT(player_id) DO UPDATE SET last_day=$2,streak=$3,\"freeze\"=$4,freeze_earned=$5").bind(id).bind(self.last).bind(self.count).bind(self.freeze).bind(self.earned).execute(&mut **tx).await?;
        Ok(())
    }
}
fn reward(reason: &str, day: NaiveDate, streak: i32) -> Value {
    let mut gold = 0;
    let mut inventory = json!({});
    let mut minutes = 0;
    if reason == "daily_gift" {
        match (day - date("1970-01-01").unwrap()).num_days().rem_euclid(3) {
            0 => gold = 20,
            1 => inventory["hint"] = json!(1),
            _ => minutes = 30,
        }
    } else {
        match streak {
            3 => gold = 30,
            7 => inventory["hint"] = json!(1),
            14 => inventory["undo"] = json!(2),
            30 => gold = 100,
            _ => {}
        }
    }
    json!({"gold":gold,"inventory":inventory,"unlimited_minutes":minutes})
}
async fn receipt(tx: &mut Tx<'_>, id: Uuid, reason: &str, day: NaiveDate, v: &Value) -> Result<()> {
    sqlx::query("INSERT INTO daily_claims(player_id,reason,utc_day,receipt) VALUES($1,$2,$3,$4)")
        .bind(id)
        .bind(reason)
        .bind(day)
        .bind(v)
        .execute(&mut **tx)
        .await?;
    Ok(())
}
pub(super) async fn legacy_receipt(tx: &mut Tx<'_>, id: Uuid) -> Result<()> {
    let today: NaiveDate = sqlx::query_scalar("SELECT (now() AT TIME ZONE 'UTC')::date")
        .fetch_one(&mut **tx)
        .await?;
    receipt(tx,id,"daily_gift",today,&json!({"utc_day":today,"reason":"daily_gift","streak":0,"freeze":false,"reward":{"gold":20,"inventory":{},"unlimited_minutes":0}})).await
}
pub(super) async fn claim(tx: &mut Tx<'_>, id: Uuid, b: &Action) -> Result<Value> {
    let today: NaiveDate = sqlx::query_scalar("SELECT (now() AT TIME ZONE 'UTC')::date")
        .fetch_one(&mut **tx)
        .await?;
    let retention = std::env::var("RETENTION_DAYS")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(395);
    claim_on(tx, id, b, today, retention).await
}
async fn claim_on(
    tx: &mut Tx<'_>,
    id: Uuid,
    b: &Action,
    today: NaiveDate,
    retention: i64,
) -> Result<Value> {
    if !matches!(b.reason.as_str(), "daily_gift" | "daily_puzzle") || b.booster.is_some() {
        return Err(ApiError::bad("utc_day only allowed for daily grants"));
    }
    let day = date(b.utc_day.as_deref().ok_or(ApiError::bad("utc_day"))?)?;
    if day > today {
        return Err(ApiError(StatusCode::CONFLICT, "daily date future"));
    }
    replay(tx, id, b).await?;
    let old: Option<Value> = sqlx::query_scalar(
        "SELECT receipt FROM daily_claims WHERE player_id=$1 AND reason=$2 AND utc_day=$3",
    )
    .bind(id)
    .bind(&b.reason)
    .bind(day)
    .fetch_optional(&mut **tx)
    .await?;
    if let Some(v) = old {
        return Ok(v);
    }
    let mut streak = Streak::default();
    if b.reason == "daily_gift" {
        if day != today {
            return Err(ApiError(StatusCode::CONFLICT, "daily date expired"));
        }
    } else {
        let ordinal = (day - date("1970-01-01")?).num_days();
        // Future-kind metrics are stored in raw_events by the existing ingestion contract.
        let evidence: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM events e LEFT JOIN raw_events r USING(event_id) WHERE e.player_id=$1 AND e.kind='daily_puzzle_end' AND COALESCE(r.payload,e.payload)->'metric_success'='true'::jsonb AND COALESCE(r.payload,e.payload)->'metric_day'=to_jsonb($2::bigint) AND (e.received_at AT TIME ZONE 'UTC')::date BETWEEN $3 AND $3+1)").bind(id).bind(ordinal).bind(day).fetch_one(&mut **tx).await?;
        if !evidence {
            return Err(if (today - day).num_days() > retention {
                ApiError(StatusCode::GONE, "daily completion expired")
            } else {
                ApiError(StatusCode::CONFLICT, "daily completion pending")
            });
        }
        streak = Streak::load(tx, id).await?;
        streak.advance(day)?;
    }
    let reward = reward(&b.reason, day, streak.count);
    let gold = reward["gold"].as_i64().unwrap();
    let balance: i64 = sqlx::query_scalar("SELECT gold FROM wallets WHERE player_id=$1")
        .bind(id)
        .fetch_one(&mut **tx)
        .await?;
    balance
        .checked_add(gold)
        .ok_or(ApiError::bad("gold overflow"))?;
    for (kind, n) in reward["inventory"].as_object().unwrap() {
        let changed = sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(COALESCE((inventory->>$2)::int,0)+$3)) WHERE player_id=$1 AND COALESCE((inventory->>$2)::bigint,0)<=2147483647-$3::bigint").bind(id).bind(kind).bind(n.as_i64().unwrap() as i32).execute(&mut **tx).await?.rows_affected();
        if changed != 1 {
            return Err(ApiError::bad("inventory overflow"));
        }
    }
    if reward["unlimited_minutes"] == 30 {
        sqlx::query("UPDATE wallets SET unlimited_until=greatest(coalesce(unlimited_until,now()),now())+interval '30 minutes' WHERE player_id=$1").bind(id).execute(&mut **tx).await?;
    }
    entry(
        tx,
        id,
        gold,
        &b.reason,
        &b.ref_id.to_string(),
        &b.idempotency_key.to_string(),
        &request(b),
    )
    .await?;
    let v = json!({"utc_day":day,"reason":b.reason,"streak":streak.count,"freeze":streak.freeze,"reward":reward});
    receipt(tx, id, &b.reason, day, &v).await?;
    if b.reason == "daily_puzzle" {
        streak.store(tx, id).await?;
    }
    Ok(v)
}
pub(super) async fn merge(tx: &mut Tx<'_>, canonical: Uuid, guest: Uuid) -> Result<()> {
    let earned: bool = sqlx::query_scalar("SELECT COALESCE(bool_or(freeze_earned),false) FROM daily_streaks WHERE player_id IN ($1,$2)").bind(canonical).bind(guest).fetch_one(&mut **tx).await?;
    sqlx::query("INSERT INTO daily_claims(player_id,reason,utc_day,receipt,created_at) SELECT $1,reason,utc_day,receipt,created_at FROM daily_claims WHERE player_id=$2 ON CONFLICT DO NOTHING").bind(canonical).bind(guest).execute(&mut **tx).await?;
    let days: Vec<NaiveDate> = sqlx::query_scalar("SELECT utc_day FROM daily_claims WHERE player_id=$1 AND reason='daily_puzzle' ORDER BY utc_day").bind(canonical).fetch_all(&mut **tx).await?;
    let mut state = Streak::default();
    for day in days {
        state.advance(day)?;
    }
    state.earned |= earned;
    state.store(tx, canonical).await
}
pub(super) async fn save_fields(tx: &mut Tx<'_>, id: Uuid, data: &mut SaveData) -> Result<()> {
    if let Some(d) = data.daily.as_mut() {
        let state = Streak::load(tx, id).await?;
        d["PuzzleStreak"] = json!(state.count);
        d["Freeze"] = json!(state.freeze);
        d["FreezeEarned"] = json!(state.earned);
        d["LastPuzzleDay"] = json!(state.last.map(|d| d.to_string()).unwrap_or_default());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn action(id: Uuid, reason: &str, day: NaiveDate) -> Action {
        Action {
            player_id: id,
            reason: reason.into(),
            ref_id: Uuid::new_v4(),
            idempotency_key: Uuid::new_v4(),
            booster: None,
            utc_day: Some(day.to_string()),
        }
    }
    async fn player(pool: &PgPool) -> Uuid {
        let id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(pool)
            .await
            .unwrap();
        id
    }
    async fn run(pool: &PgPool, b: &Action, today: NaiveDate) -> Result<Value> {
        let mut tx = pool.begin().await?;
        lock(&mut tx, b.player_id).await?;
        let v = claim_on(&mut tx, b.player_id, b, today, 395).await?;
        tx.commit().await?;
        Ok(v)
    }
    async fn evidence(pool: &PgPool, id: Uuid, day: NaiveDate, received: NaiveDate) {
        let event = Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,received_at,payload) VALUES($1,$2,'daily_puzzle_end',$3,$3,'{}')").bind(event).bind(id).bind(received.and_hms_opt(12,0,0).unwrap().and_utc()).execute(pool).await.unwrap();
        sqlx::query("INSERT INTO raw_events(event_id,payload) VALUES($1,$2)").bind(event).bind(json!({"metric_day":(day-date("1970-01-01").unwrap()).num_days(),"metric_success":true})).execute(pool).await.unwrap();
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn parallel_replay_dates_fingerprints_and_interop(pool: PgPool) {
        let id = player(&pool).await;
        let day = date("2026-11-15").unwrap(); // gold rotation
        let a = action(id, "daily_gift", day);
        let b = action(id, "daily_gift", day);
        let (x, y) = tokio::join!(run(&pool, &a, day), run(&pool, &b, day));
        assert_eq!(x.unwrap(), y.unwrap());
        assert!(
            run(&pool, &a, day + chrono::Duration::days(1))
                .await
                .is_ok()
        );
        let mut wrong = action(id, "daily_gift", day + chrono::Duration::days(1));
        wrong.idempotency_key = a.idempotency_key;
        assert_eq!(
            run(&pool, &wrong, day + chrono::Duration::days(1))
                .await
                .unwrap_err()
                .0,
            StatusCode::CONFLICT
        );
        assert!(
            run(
                &pool,
                &action(id, "daily_gift", day + chrono::Duration::days(1)),
                day
            )
            .await
            .is_err()
        );
        assert!(
            run(
                &pool,
                &action(id, "daily_gift", day - chrono::Duration::days(1)),
                day
            )
            .await
            .is_err()
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM wallet_ledger WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap(),
            1
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT gold FROM wallets WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap(),
            20
        );
        sqlx::query("ALTER TABLE daily_claims ADD CONSTRAINT reject_new CHECK (utc_day <> DATE '2026-11-18') NOT VALID").execute(&pool).await.unwrap();
        let fail_day = date("2026-11-18").unwrap();
        assert!(
            run(&pool, &action(id, "daily_gift", fail_day), fail_day)
                .await
                .is_err()
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT gold FROM wallets WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap(),
            20
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM wallet_ledger WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap(),
            1
        );
        let next = day + chrono::Duration::days(1);
        assert_eq!(
            run(&pool, &action(id, "daily_gift", next), next)
                .await
                .unwrap()["reward"]["inventory"],
            json!({"hint":1})
        );

        // The real HTTP path also rejects unauthenticated claims and preserves legacy caps.
        let today = Utc::now().date_naive();
        let id = player(&pool).await;
        let token = id.to_string();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest(&token))).execute(&pool).await.unwrap();
        let state = new_state(pool.clone(), "admin", false);
        let mut legacy = action(id, "daily_gift", today);
        legacy.utc_day = None;
        assert!(
            grant(State(state.clone()), HeaderMap::new(), Json(legacy))
                .await
                .is_err()
        );
        let mut h = HeaderMap::new();
        h.insert("x-install-token", token.parse().unwrap());
        let mut legacy = action(id, "daily_gift", today);
        legacy.utc_day = None;
        let _ = grant(State(state.clone()), h.clone(), Json(legacy))
            .await
            .unwrap();
        let response = grant(
            State(state.clone()),
            h.clone(),
            Json(action(id, "daily_gift", today)),
        )
        .await
        .unwrap()
        .0;
        assert_eq!(response["daily"]["utc_day"], today.to_string());
        assert_eq!(response["daily"]["reward"]["gold"], 20);
        assert_eq!(response["wallet"]["gold"], 20);
        let mut legacy = action(id, "daily_gift", today);
        legacy.utc_day = None;
        assert!(grant(State(state), h, Json(legacy)).await.is_err());
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn puzzle_evidence_streak_freeze_milestones_and_expiry(pool: PgPool) {
        let id = player(&pool).await;
        let first = date("2026-09-01").unwrap();
        assert_eq!(
            run(&pool, &action(id, "daily_puzzle", first), first)
                .await
                .unwrap_err()
                .1,
            "daily completion pending"
        );
        for n in 0..30 {
            let day = first + chrono::Duration::days(n);
            evidence(&pool, id, day, day + chrono::Duration::days(1)).await;
            let v = run(
                &pool,
                &action(id, "daily_puzzle", day),
                day + chrono::Duration::days(1),
            )
            .await
            .unwrap();
            assert_eq!(v["streak"], n + 1);
            assert_eq!(v["reward"], reward("daily_puzzle", day, (n + 1) as i32));
        }
        let day = first + chrono::Duration::days(31);
        evidence(&pool, id, day, day).await;
        let v = run(&pool, &action(id, "daily_puzzle", day), day)
            .await
            .unwrap();
        assert_eq!(v["streak"], 31);
        assert_eq!(v["freeze"], false);
        let day = day + chrono::Duration::days(2);
        evidence(&pool, id, day, day).await;
        assert_eq!(
            run(&pool, &action(id, "daily_puzzle", day), day)
                .await
                .unwrap()["streak"],
            1
        );
        let old = first - chrono::Duration::days(1);
        evidence(&pool, id, old, old).await;
        assert_eq!(
            run(&pool, &action(id, "daily_puzzle", old), day)
                .await
                .unwrap_err()
                .1,
            "daily history out of order"
        );
        let expired = first - chrono::Duration::days(500);
        assert_eq!(
            run(&pool, &action(id, "daily_puzzle", expired), day)
                .await
                .unwrap_err()
                .0,
            StatusCode::GONE
        );
        let late = day + chrono::Duration::days(1);
        evidence(&pool, id, late, late + chrono::Duration::days(2)).await;
        assert!(
            run(
                &pool,
                &action(id, "daily_puzzle", late),
                late + chrono::Duration::days(2)
            )
            .await
            .is_err()
        );
        // A longer gap resets even an unused freeze; it cannot be earned again.
        let mut s = Streak {
            last: Some(first),
            count: 7,
            freeze: true,
            earned: true,
        };
        s.advance(first + chrono::Duration::days(3)).unwrap();
        assert_eq!(s.count, 1);
        s.freeze = false;
        for n in 4..11 {
            s.advance(first + chrono::Duration::days(n)).unwrap();
        }
        assert!(!s.freeze);
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn unlimited_merge_history_overflow_and_atomic_rollback(pool: PgPool) {
        let day = date("2026-11-17").unwrap();
        let canonical = player(&pool).await;
        let guest = player(&pool).await;
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, guest).await.unwrap();
        sqlx::query(
            "UPDATE wallets SET unlimited_until=now()+interval '1 hour' WHERE player_id=$1",
        )
        .bind(guest)
        .execute(&mut *tx)
        .await
        .unwrap();
        tx.commit().await.unwrap();
        run(&pool, &action(guest, "daily_gift", day), day)
            .await
            .unwrap();
        let mins:f64=sqlx::query_scalar("SELECT extract(epoch FROM (unlimited_until-now()))::float8/60 FROM wallets WHERE player_id=$1").bind(guest).fetch_one(&pool).await.unwrap();
        assert!((89.0..=90.0).contains(&mins));
        let mut tx = pool.begin().await.unwrap();
        super::super::merge(&mut tx, canonical, guest)
            .await
            .unwrap();
        let w = snapshot(&mut tx, canonical).await.unwrap();
        assert_eq!(w["gold"], 0);
        assert_eq!(w["inventory"], json!({}));
        assert!(w["unlimited_until"].is_null());
        tx.commit().await.unwrap();
        assert!(
            run(&pool, &action(canonical, "daily_gift", day), day)
                .await
                .is_ok()
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>(
                "SELECT count(*) FROM wallet_ledger WHERE player_id=$1 AND reason='daily_gift'"
            )
            .bind(canonical)
            .fetch_one(&pool)
            .await
            .unwrap(),
            1
        );
        let hint = day + chrono::Duration::days(2);
        sqlx::query("UPDATE wallets SET inventory='{\"hint\":2147483647}' WHERE player_id=$1")
            .bind(canonical)
            .execute(&pool)
            .await
            .unwrap();
        assert!(
            run(&pool, &action(canonical, "daily_gift", hint), hint)
                .await
                .is_err()
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM daily_claims WHERE player_id=$1")
                .bind(canonical)
                .fetch_one(&pool)
                .await
                .unwrap(),
            1
        );
        sqlx::query("DELETE FROM players WHERE id IN ($1,$2)")
            .bind(guest)
            .bind(canonical)
            .execute(&pool)
            .await
            .unwrap();
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM daily_claims")
                .fetch_one(&pool)
                .await
                .unwrap(),
            0
        );
    }
    #[test]
    fn cloud_validation_and_dates() {
        for day in ["2026-1-01", "2026-11-15T00:00:00Z", "2026-02-30", ""] {
            assert!(date(day).is_err());
        }
        let mut s = SaveData {
            daily: Some(
                json!({"Version":1,"Completed":["2026-11-15"],"PendingPuzzleGrants":["2026-11-15"],"WinStreak":3}),
            ),
            ..Default::default()
        };
        assert!(s.validate().is_ok());
        let v = serde_json::to_value(&s).unwrap();
        assert_eq!(
            serde_json::from_value::<SaveData>(v).unwrap().daily,
            s.daily
        );
        for invalid in [
            json!({"Version":2}),
            json!({"Version":1,"WinStreak":4}),
            json!({"Version":1,"Sessions":-1}),
            json!({"Version":1,"Completed":["2026-11-15","2026-11-15"]}),
            json!({"Version":1,"PendingPuzzleGrants":["2026-11-15"]}),
            json!({"Version":1,"Unknown":true}),
            json!({"Version":1,"ScheduledAt":-1}),
            json!({"Version":1,"ActiveAttempt":"x".repeat(129)}),
        ] {
            s.daily = Some(invalid);
            assert!(s.validate().is_err());
        }
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn upgrade_backfill_timezone_export_and_reconciliation(pool: PgPool) {
        use http_body_util::BodyExt;
        let id = player(&pool).await;
        let token = id.to_string();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest(&token))).execute(&pool).await.unwrap();
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, id).await.unwrap();
        for _ in 0..2 {
            entry(
                &mut tx,
                id,
                20,
                "daily_gift",
                "old",
                &Uuid::new_v4().to_string(),
                &json!({}),
            )
            .await
            .unwrap();
        }
        tx.commit().await.unwrap();
        // Recreate only the new migration over a populated v1 database.
        sqlx::raw_sql(
            "DROP TABLE daily_claims; DROP TABLE daily_streaks; DROP INDEX events_daily_evidence",
        )
        .execute(&pool)
        .await
        .unwrap();
        sqlx::raw_sql(include_str!("../../migrations/0005_daily.sql"))
            .execute(&pool)
            .await
            .unwrap();
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM daily_claims")
                .fetch_one(&pool)
                .await
                .unwrap(),
            1
        );
        let today = Utc::now().date_naive();
        let mut tx = pool.begin().await.unwrap();
        sqlx::query("SET LOCAL TIME ZONE 'America/Los_Angeles'")
            .execute(&mut *tx)
            .await
            .unwrap();
        lock(&mut tx, id).await.unwrap();
        let v = claim(&mut tx, id, &action(id, "daily_gift", today))
            .await
            .unwrap();
        assert_eq!(v["reward"]["gold"], 20);
        tx.commit().await.unwrap();

        let guest = player(&pool).await;
        let first = today - chrono::Duration::days(12);
        for n in 0..10 {
            let day = first + chrono::Duration::days(n);
            for who in if n < 7 { vec![id, guest] } else { vec![guest] } {
                evidence(&pool, who, day, day).await;
                run(&pool, &action(who, "daily_puzzle", day), today)
                    .await
                    .unwrap();
            }
        }
        let mut tx = pool.begin().await.unwrap();
        super::super::merge(&mut tx, id, guest).await.unwrap();
        let state = Streak::load(&mut tx, id).await.unwrap();
        assert_eq!(state.count, 10);
        assert!(state.earned);
        assert!(state.freeze);
        let count: i64 = sqlx::query_scalar(
            "SELECT count(*) FROM daily_claims WHERE player_id=$1 AND reason='daily_puzzle'",
        )
        .bind(id)
        .fetch_one(&mut *tx)
        .await
        .unwrap();
        assert_eq!(count, 10);
        let mut save = SaveData {
            daily: Some(json!({"Version":1,"PuzzleStreak":999,"Freeze":false,"WinStreak":2})),
            ..Default::default()
        };
        save_fields(&mut tx, id, &mut save).await.unwrap();
        assert_eq!(save.daily.as_ref().unwrap()["PuzzleStreak"], 10);
        assert_eq!(save.daily.as_ref().unwrap()["WinStreak"], 2);
        tx.commit().await.unwrap();
        // Receipt tombstones survive event retention and remain included in account export.
        sqlx::query("UPDATE events SET received_at=now()-interval '500 days'")
            .execute(&pool)
            .await
            .unwrap();
        retain(&pool, 395).await.unwrap();
        let app = new_state(pool.clone(), "admin", false);
        let mut h = HeaderMap::new();
        h.insert("x-install-token", token.parse().unwrap());
        let response = account_export(
            State(app.clone()),
            h.clone(),
            Query(AccountQuery {
                player_id: Some(id),
            }),
        )
        .await
        .unwrap();
        let body = response.into_body().collect().await.unwrap().to_bytes();
        let body = String::from_utf8(body.to_vec()).unwrap();
        assert!(body.contains("\"table\":\"daily_claims\""));
        assert!(body.contains("\"table\":\"daily_streaks\""));
        let _ = account_delete(
            State(app),
            h,
            Query(AccountQuery {
                player_id: Some(id),
            }),
        )
        .await
        .unwrap();
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM daily_claims WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap(),
            0
        );
    }
}
