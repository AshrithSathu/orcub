use super::*;

pub(super) async fn claim(tx: &mut Tx<'_>, id: Uuid, b: &Action) -> Result<Value> {
    if b.utc_day.is_some() {
        return Err(ApiError::bad("booster unlock has no utc_day"));
    }
    let kind = b.booster.as_deref().ok_or(ApiError::bad("kind required"))?;
    let level = match kind {
        "hint" => 5,
        "undo" => 8,
        "unclamp" => 12,
        _ => return Err(ApiError::bad("invalid booster kind")),
    };
    replay(tx, id, b).await?;
    let old: Option<Value> = sqlx::query_scalar(
        "SELECT receipt FROM booster_unlock_claims WHERE player_id=$1 AND kind=$2",
    )
    .bind(id)
    .bind(kind)
    .fetch_optional(&mut **tx)
    .await?;
    if let Some(receipt) = old {
        return Ok(receipt);
    }
    // Saves and LevelIndex use zero-based indices; the tutorial uses displayed levels.
    let progress: Option<i32> = sqlx::query_scalar("SELECT max(level) FROM (SELECT (data->>'highest_level')::int+1 AS level FROM saves WHERE player_id=$1 UNION ALL SELECT (payload->>'LevelIndex')::int+1 FROM events WHERE player_id=$1 AND kind IN ('level_start','level_end')) p").bind(id).fetch_one(&mut **tx).await?;
    if progress.is_some_and(|progress| progress < level) {
        return Err(ApiError(StatusCode::CONFLICT, "booster_unlock_locked"));
    }
    let reward = json!({"gold":0,"inventory":{kind:3},"unlimited_minutes":0});
    let receipt = json!({"source":"booster_unlock","kind":kind,"reward":reward});
    sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(COALESCE((inventory->>$2)::int,0)+3)) WHERE player_id=$1").bind(id).bind(kind).execute(&mut **tx).await?;
    entry(
        tx,
        id,
        0,
        "booster_unlock",
        &b.ref_id.to_string(),
        &b.idempotency_key.to_string(),
        &request(b),
    )
    .await?;
    sqlx::query("INSERT INTO booster_unlock_claims(player_id,kind,receipt) VALUES($1,$2,$3)")
        .bind(id)
        .bind(kind)
        .bind(&receipt)
        .execute(&mut **tx)
        .await?;
    Ok(receipt)
}

pub(super) async fn merge(tx: &mut Tx<'_>, canonical: Uuid, guest: Uuid) -> Result<Vec<Value>> {
    // RETURNING selects only newly inserted kinds; canonical receipts always win.
    Ok(sqlx::query_scalar("INSERT INTO booster_unlock_claims(player_id,kind,receipt,created_at) SELECT $1,kind,receipt,created_at FROM booster_unlock_claims WHERE player_id=$2 ON CONFLICT DO NOTHING RETURNING receipt->'reward'").bind(canonical).bind(guest).fetch_all(&mut **tx).await?)
}

#[cfg(test)]
mod tests {
    use super::*;
    async fn player(pool: &PgPool) -> Uuid {
        let id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(pool)
            .await
            .unwrap();
        id
    }
    async fn grant_kind(pool: &PgPool, id: Uuid, kind: &str) -> Result<Value> {
        let b: Action = serde_json::from_value(json!({"player_id":id,"source":"booster_unlock","kind":kind,"ref_id":Uuid::new_v4(),"idempotency_key":Uuid::new_v4()})).unwrap();
        let mut tx = pool.begin().await?;
        lock(&mut tx, id).await?;
        let receipt = claim(&mut tx, id, &b).await?;
        tx.commit().await?;
        Ok(receipt)
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn once_per_kind_replay_and_progress_gate(pool: PgPool) {
        let id = player(&pool).await;
        for kind in ["hint", "undo", "unclamp"] {
            let (first, second) =
                tokio::join!(grant_kind(&pool, id, kind), grant_kind(&pool, id, kind));
            assert_eq!(first.unwrap(), second.unwrap());
            let inventory: Value =
                sqlx::query_scalar("SELECT inventory FROM wallets WHERE player_id=$1")
                    .bind(id)
                    .fetch_one(&pool)
                    .await
                    .unwrap();
            assert_eq!(inventory[kind], 3);
        }
        let count:i64 = sqlx::query_scalar("SELECT count(*) FROM wallet_ledger WHERE player_id=$1 AND reason='booster_unlock' AND delta=0").bind(id).fetch_one(&pool).await.unwrap();
        assert_eq!(count, 3);
        let id = player(&pool).await;
        sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,1,$2)")
            .bind(id)
            .bind(json!({"highest_level":3}))
            .execute(&pool)
            .await
            .unwrap();
        assert_eq!(
            grant_kind(&pool, id, "hint").await.unwrap_err().1,
            "booster_unlock_locked"
        );
        sqlx::query("UPDATE saves SET data='{\"highest_level\":4}' WHERE player_id=$1")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        grant_kind(&pool, id, "hint").await.unwrap();
        assert!(grant_kind(&pool, id, "undo").await.is_err());
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_start',now(),'{\"LevelIndex\":11}')").bind(Uuid::new_v4()).bind(id).execute(&pool).await.unwrap();
        grant_kind(&pool, id, "unclamp").await.unwrap();
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn merge_carries_new_kinds_once_preserving_purchases(pool: PgPool) {
        let canonical = player(&pool).await;
        let receipt = grant_kind(&pool, canonical, "hint").await.unwrap();
        for _ in 0..2 {
            let guest = player(&pool).await;
            grant_kind(&pool, guest, "hint").await.unwrap();
            grant_kind(&pool, guest, "undo").await.unwrap();
            sqlx::query(
                "UPDATE wallets SET inventory=jsonb_set(inventory,'{hint}','6') WHERE player_id=$1",
            )
            .bind(guest)
            .execute(&pool)
            .await
            .unwrap();
            let mut tx = pool.begin().await.unwrap();
            super::super::merge(&mut tx, canonical, guest)
                .await
                .unwrap();
            tx.commit().await.unwrap();
        }
        let inventory: Value =
            sqlx::query_scalar("SELECT inventory FROM wallets WHERE player_id=$1")
                .bind(canonical)
                .fetch_one(&pool)
                .await
                .unwrap();
        assert_eq!(inventory["hint"], 9); // 3 original plus 3 purchased from each guest.
        assert_eq!(inventory["undo"], 3); // First guest carries, second guest is excluded.
        assert_eq!(grant_kind(&pool, canonical, "hint").await.unwrap(), receipt);
        let count: i64 =
            sqlx::query_scalar("SELECT count(*) FROM booster_unlock_claims WHERE player_id=$1")
                .bind(canonical)
                .fetch_one(&pool)
                .await
                .unwrap();
        assert_eq!(count, 2);
    }
}
