mod daily;
pub(crate) fn daily_date(s: &str) -> bool {
    daily::date(s).is_ok()
}
use crate::*;
use sqlx::{Postgres, Transaction};
type Tx<'a> = Transaction<'a, Postgres>;
pub fn catalog() -> Value {
    serde_json::from_str(include_str!("../config/economy.json")).expect("economy")
}
fn product(id: &str) -> Result<Value> {
    catalog()["products"]
        .as_array()
        .unwrap()
        .iter()
        .find(|p| p["id"] == id)
        .cloned()
        .ok_or(ApiError::bad("unknown product"))
}
async fn owner(s: &Arc<App>, h: &HeaderMap, id: Uuid) -> Result<Uuid> {
    install_player(s, h, id).await?;
    if h.contains_key("authorization") {
        let Json(v) = super::link(
            State(s.clone()),
            h.clone(),
            Json(LinkRequest { player_id: id }),
        )
        .await?;
        return serde_json::from_value(v["player_id"].clone()).map_err(|_| ApiError::unavailable());
    }
    Ok(id)
}
async fn lock(tx: &mut Tx<'_>, id: Uuid) -> Result<()> {
    sqlx::query("SELECT id FROM players WHERE id=$1 FOR UPDATE")
        .bind(id)
        .fetch_optional(&mut **tx)
        .await?
        .ok_or(ApiError::unauthorized())?;
    sqlx::query("INSERT INTO wallets(player_id) VALUES($1) ON CONFLICT DO NOTHING")
        .bind(id)
        .execute(&mut **tx)
        .await?;
    Ok(())
}
async fn regenerate(tx: &mut Tx<'_>, id: Uuid) -> Result<()> {
    sqlx::query("UPDATE wallets SET lives=LEAST(5,lives+floor(extract(epoch FROM (now()-lives_updated_at))/1800)::int), lives_updated_at=CASE WHEN lives+floor(extract(epoch FROM (now()-lives_updated_at))/1800)>=5 THEN now() ELSE lives_updated_at+floor(extract(epoch FROM (now()-lives_updated_at))/1800)*interval '30 minutes' END WHERE player_id=$1").bind(id).execute(&mut **tx).await?;
    Ok(())
}
async fn snapshot(tx: &mut Tx<'_>, id: Uuid) -> Result<Value> {
    regenerate(tx, id).await?;
    Ok(sqlx::query_scalar("SELECT to_jsonb(w) || jsonb_build_object('remove_ads',EXISTS(SELECT 1 FROM purchases WHERE player_id=$1 AND product_id='com.orcub.puzzle.remove_ads' AND NOT revoked)) FROM wallets w WHERE player_id=$1").bind(id).fetch_one(&mut **tx).await?)
}
pub(crate) async fn save_fields(tx: &mut Tx<'_>, id: Uuid, data: &mut SaveData) -> Result<()> {
    sqlx::query("INSERT INTO wallets(player_id) VALUES($1) ON CONFLICT DO NOTHING")
        .bind(id)
        .execute(&mut **tx)
        .await?;
    regenerate(tx, id).await?;
    daily::save_fields(tx, id, data).await?;
    data.inventory.clear();
    data.workshop.clear();
    data.lives = 0;
    Ok(())
}
async fn entry(
    tx: &mut Tx<'_>,
    id: Uuid,
    delta: i64,
    reason: &str,
    reference: &str,
    key: &str,
    request: &Value,
) -> Result<()> {
    let balance: i64 =
        sqlx::query_scalar("UPDATE wallets SET gold=gold+$2 WHERE player_id=$1 RETURNING gold")
            .bind(id)
            .bind(delta)
            .fetch_one(&mut **tx)
            .await?;
    sqlx::query("INSERT INTO wallet_ledger(id,player_id,delta,reason,ref_id,idempotency_key,request,balance_after) VALUES($1,$2,$3,$4,$5,$6,$7,$8)").bind(Uuid::new_v4()).bind(id).bind(delta).bind(reason).bind(reference).bind(key).bind(request).bind(balance).execute(&mut **tx).await?;
    sqlx::query("UPDATE wallet_ledger SET environment=(SELECT environment FROM purchases WHERE transaction_id=$2) WHERE player_id=$1 AND idempotency_key=$3 AND reason IN ('iap','refund','refund_reversed')").bind(id).bind(reference).bind(key).execute(&mut **tx).await?;
    sqlx::query("DELETE FROM player_value_cache WHERE player_id=$1")
        .bind(id)
        .execute(&mut **tx)
        .await?;
    Ok(())
}
pub(crate) async fn wallet(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(q): Query<PlayerQuery>,
) -> Result<Response> {
    let id = owner(&s, &h, q.player_id).await?;
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    let v = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(([("cache-control", "no-store")], Json(v)).into_response())
}
#[derive(Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
pub struct Consume {
    player_id: Uuid,
    kind: String,
    count: i32,
    attempt_ref: Uuid,
    idempotency_key: Uuid,
}
pub async fn consume(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<Consume>,
) -> Result<Json<Value>> {
    if !(1..=100).contains(&b.count)
        || !matches!(
            b.kind.as_str(),
            "booster_hint" | "booster_undo" | "booster_unclamp" | "life"
        )
    {
        return Err(ApiError::bad("consume kind/count"));
    }
    let id = owner(&s, &h, b.player_id).await?;
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    regenerate(&mut tx, id).await?;
    let mut request = serde_json::to_value(&b).unwrap();
    request.as_object_mut().unwrap().remove("player_id");
    let old:Option<Value>=sqlx::query_scalar("SELECT request FROM wallet_ledger WHERE player_id=$1 AND (idempotency_key=$2 OR idempotency_key LIKE '%:' || $2)").bind(id).bind(b.idempotency_key.to_string()).fetch_optional(&mut *tx).await?;
    if let Some(old) = old {
        if old != request {
            return Err(ApiError(StatusCode::CONFLICT, "idempotency key reused"));
        }
    } else {
        let n = if b.kind == "life" {
            sqlx::query("UPDATE wallets SET lives=CASE WHEN unlimited_until>now() THEN lives ELSE lives-$2 END,lives_updated_at=CASE WHEN lives=5 THEN now() ELSE lives_updated_at END WHERE player_id=$1 AND (lives >= $2 OR unlimited_until>now())").bind(id).bind(b.count).execute(&mut *tx).await?.rows_affected()
        } else {
            sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb((inventory->>$2)::int-$3)) WHERE player_id=$1 AND COALESCE((inventory->>$2)::int,0)>=$3").bind(id).bind(b.kind.trim_start_matches("booster_")).bind(b.count).execute(&mut *tx).await?.rows_affected()
        };
        if n == 0 {
            return Err(ApiError(StatusCode::CONFLICT, "insufficient inventory"));
        }
        entry(
            &mut tx,
            id,
            0,
            "consume",
            &b.attempt_ref.to_string(),
            &b.idempotency_key.to_string(),
            &request,
        )
        .await?;
    }
    let w = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(json!({"wallet":w})))
}
#[derive(Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
pub struct Action {
    player_id: Uuid,
    idempotency_key: Uuid,
    reason: String,
    ref_id: Uuid,
    #[serde(default)]
    booster: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    utc_day: Option<String>,
}
async fn replay(tx: &mut Tx<'_>, id: Uuid, b: &Action) -> Result<bool> {
    let old: Option<Value> = sqlx::query_scalar(
        "SELECT request FROM wallet_ledger WHERE player_id=$1 AND (idempotency_key=$2 OR idempotency_key LIKE '%:' || $2) ORDER BY created_at LIMIT 1",
    )
    .bind(id)
    .bind(b.idempotency_key.to_string())
    .fetch_optional(&mut **tx)
    .await?;
    let mut request = serde_json::to_value(b).unwrap();
    request.as_object_mut().unwrap().remove("player_id");
    if let Some(old) = old {
        if old != request {
            return Err(ApiError(StatusCode::CONFLICT, "idempotency key reused"));
        }
        return Ok(true);
    }
    Ok(false)
}
fn request(b: &Action) -> Value {
    let mut v = serde_json::to_value(b).unwrap();
    v.as_object_mut().unwrap().remove("player_id");
    v
}
fn continue_price(n: i64) -> Result<i64> {
    if !(0..20).contains(&n) {
        return Err(ApiError::bad("continue limit"));
    }
    let c = catalog();
    Ok((c["sinks"]["continue"]["gold"].as_f64().unwrap()
        * c["sinks"]["continue"]["multiplier"]
            .as_f64()
            .unwrap()
            .powi(n as i32))
    .ceil() as i64)
}
pub async fn spend(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<Action>,
) -> Result<Json<Value>> {
    if b.utc_day.is_some() {
        return Err(ApiError::bad("utc_day only allowed for daily grants"));
    }
    if b.reason != "booster" && b.booster.is_some() {
        return Err(ApiError::bad("booster only allowed for booster sink"));
    }
    let id = owner(&s, &h, b.player_id).await?;
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    if !replay(&mut tx, id, &b).await? {
        let c = catalog();
        let cost = match b.reason.as_str() {
            "refill_lives" => c["sinks"]["refill_lives"]["gold"].as_i64().unwrap(),
            "booster" => {
                if !c["boosters"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|v| v.as_str() == b.booster.as_deref())
                {
                    return Err(ApiError::bad("booster"));
                }
                c["sinks"]["booster"]["gold"].as_i64().unwrap()
            }
            "continue" => {
                let exists:bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM events WHERE player_id=$1 AND event_id=$2 AND kind='level_start' AND received_at>now()-interval '1 day')").bind(id).bind(b.ref_id).fetch_one(&mut *tx).await?;
                if !exists {
                    return Err(ApiError::bad("attempt missing"));
                }
                let n:i64=sqlx::query_scalar("SELECT count(*) FROM wallet_ledger WHERE player_id=$1 AND reason='continue' AND ref_id=$2").bind(id).bind(b.ref_id.to_string()).fetch_one(&mut *tx).await?;
                continue_price(n)?
            }
            _ => return Err(ApiError::bad("unknown sink")),
        };
        let gold: i64 = sqlx::query_scalar("SELECT gold FROM wallets WHERE player_id=$1")
            .bind(id)
            .fetch_one(&mut *tx)
            .await?;
        if gold < cost {
            return Err(ApiError(StatusCode::CONFLICT, "insufficient gold"));
        }
        entry(
            &mut tx,
            id,
            -cost,
            &b.reason,
            &b.ref_id.to_string(),
            &b.idempotency_key.to_string(),
            &request(&b),
        )
        .await?;
        if b.reason == "booster" {
            sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(COALESCE((inventory->>$2)::int,0)+1)) WHERE player_id=$1").bind(id).bind(b.booster.as_deref().unwrap()).execute(&mut *tx).await?;
        }
        if b.reason == "refill_lives" {
            sqlx::query("UPDATE wallets SET lives=5,lives_updated_at=now() WHERE player_id=$1")
                .bind(id)
                .execute(&mut *tx)
                .await?;
        }
    }
    let v = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(
        json!({"wallet":v,"effect":if b.reason=="continue"{json!({"extra_moves":5,"attempt_id":b.ref_id})}else{Value::Null}}),
    ))
}
pub async fn grant(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<Action>,
) -> Result<Json<Value>> {
    if b.booster.is_some() {
        return Err(ApiError::bad("booster only allowed for booster sink"));
    }
    let id = owner(&s, &h, b.player_id).await?;
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    if b.utc_day.is_some() {
        let receipt = daily::claim(&mut tx, id, &b).await?;
        let wallet = snapshot(&mut tx, id).await?;
        tx.commit().await?;
        return Ok(Json(json!({"wallet":wallet,"daily":receipt})));
    }
    if !replay(&mut tx, id, &b).await? {
        let amount = match b.reason.as_str() {
            "daily_gift" => {
                let n:i64=sqlx::query_scalar("SELECT (SELECT count(*) FROM daily_claims WHERE player_id=$1 AND reason='daily_gift' AND utc_day=(now() AT TIME ZONE 'UTC')::date) + (SELECT count(*) FROM wallet_ledger WHERE player_id=$1 AND reason='daily_gift' AND created_at >= date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')").bind(id).fetch_one(&mut *tx).await?;
                if n > 0 {
                    return Err(ApiError(StatusCode::CONFLICT, "daily gift claimed"));
                }
                catalog()["grants"]["daily_gift"].as_i64().unwrap()
            }
            "level_reward" => {
                let valid:bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM events WHERE player_id=$1 AND event_id=$2 AND kind='level_end' AND (payload->'Result'->>'Outcome')::int=0 AND received_at>now()-interval '1 day') AND NOT EXISTS(SELECT 1 FROM wallet_ledger WHERE player_id=$1 AND reason='level_reward' AND ref_id=$3)").bind(id).bind(b.ref_id).bind(b.ref_id.to_string()).fetch_one(&mut *tx).await?;
                let n:i64=sqlx::query_scalar("SELECT count(*) FROM wallet_ledger WHERE player_id=$1 AND reason='level_reward' AND created_at>=date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'").bind(id).fetch_one(&mut *tx).await?;
                if !valid
                    || n >= catalog()["grants"]["level_reward_daily_cap"]
                        .as_i64()
                        .unwrap()
                {
                    return Err(ApiError::bad("reward ineligible"));
                }
                catalog()["grants"]["level_reward"].as_i64().unwrap()
            }
            _ => {
                return Err(ApiError::bad(
                    "grant requires server reason; purchases use IAP",
                ));
            }
        };
        entry(
            &mut tx,
            id,
            amount,
            &b.reason,
            &b.ref_id.to_string(),
            &b.idempotency_key.to_string(),
            &request(&b),
        )
        .await?;
        if b.reason == "daily_gift" {
            daily::legacy_receipt(&mut tx, id).await?;
        }
    }
    let v = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(json!({"wallet":v})))
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Purchase {
    player_id: Uuid,
    signed_transaction: String,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Restore {
    player_id: Uuid,
    signed_transactions: Vec<String>,
}
fn field<'a>(v: &'a Value, k: &str) -> Result<&'a str> {
    v[k].as_str()
        .filter(|s| !s.is_empty() && s.len() <= 200)
        .ok_or(ApiError::bad("transaction field"))
}
async fn apply(tx: &mut Tx<'_>, id: Uuid, v: &Value) -> Result<()> {
    let tid = field(v, "transactionId")?;
    let pid = field(v, "productId")?;
    let p = product(pid)?;
    if v.get("quantity").is_some_and(|q| q.as_i64() != Some(1)) {
        return Err(ApiError::bad("quantity must be 1"));
    }
    let original = field(v, "originalTransactionId")?;
    let environment = field(v, "environment")?;
    let non_consumable = p["kind"] == "non_consumable";
    if non_consumable {
        sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,2))")
            .bind(format!("{environment}:{original}"))
            .execute(&mut **tx)
            .await?;
        let existing=sqlx::query("SELECT transaction_id,player_id,revoked FROM purchases WHERE original_transaction_id=$1 AND environment=$2 AND product_id=$3 ORDER BY created_at LIMIT 1").bind(original).bind(environment).bind(pid).fetch_optional(&mut **tx).await?;
        if let Some(r) = existing {
            if r.get::<bool, _>("revoked") || v.get("revocationDate").is_some_and(|v| !v.is_null())
            {
                return Err(ApiError::bad("revoked purchase"));
            }
            let previous: Uuid = r.get("player_id");
            if previous != id {
                let limited:bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM entitlement_transfers WHERE original_transaction_id=$1 AND environment=$2 AND created_at>now()-interval '24 hours')").bind(original).bind(environment).fetch_one(&mut **tx).await?;
                if limited {
                    return Err(ApiError(
                        StatusCode::TOO_MANY_REQUESTS,
                        "entitlement transfer cooldown",
                    ));
                }
                sqlx::query("UPDATE purchases SET player_id=$1 WHERE original_transaction_id=$2 AND environment=$3 AND product_id=$4").bind(id).bind(original).bind(environment).bind(pid).execute(&mut **tx).await?;
                sqlx::query("INSERT INTO entitlement_transfers(original_transaction_id,environment,from_player,player_id) VALUES($1,$2,$3,$4)").bind(original).bind(environment).bind(previous).bind(id).execute(&mut **tx).await?;
                sqlx::query("DELETE FROM player_value_cache WHERE player_id IN ($1,$2)")
                    .bind(id)
                    .bind(previous)
                    .execute(&mut **tx)
                    .await?;
            }
            return Ok(());
        }
    }
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,1))")
        .bind(tid)
        .execute(&mut **tx)
        .await?;
    if let Some(existing) =
        sqlx::query_scalar::<_, Uuid>("SELECT player_id FROM purchases WHERE transaction_id=$1")
            .bind(tid)
            .fetch_optional(&mut **tx)
            .await?
    {
        if existing != id {
            return Err(ApiError(
                StatusCode::CONFLICT,
                "purchase belongs to another wallet",
            ));
        }
        return Ok(());
    }
    if !non_consumable {
        // Bind purchases to the canonical wallet token supplied to StoreKit's appAccountToken option.
        let token = field(v, "appAccountToken")?
            .parse::<Uuid>()
            .map_err(|_| ApiError::bad("appAccountToken"))?;
        let alias: bool = sqlx::query_scalar(
            "SELECT EXISTS(SELECT 1 FROM wallet_aliases WHERE token=$1 AND player_id=$2)",
        )
        .bind(token)
        .bind(id)
        .fetch_one(&mut **tx)
        .await?;
        if token != id && !alias {
            return Err(ApiError::bad("appAccountToken must match linked player_id"));
        }
    }
    let revoked: bool = sqlx::query_scalar(
        "SELECT EXISTS(SELECT 1 FROM purchase_revocations WHERE transaction_id=$1)",
    )
    .bind(tid)
    .fetch_one(&mut **tx)
    .await?;
    if revoked || v.get("revocationDate").is_some_and(|v| !v.is_null()) {
        return Err(ApiError::bad("revoked purchase"));
    }
    if pid.ends_with("starter_pack") {
        let bought: bool = sqlx::query_scalar(
            "SELECT EXISTS(SELECT 1 FROM purchases WHERE player_id=$1 AND product_id=$2)",
        )
        .bind(id)
        .bind(pid)
        .fetch_one(&mut **tx)
        .await?;
        if bought {
            return Err(ApiError(
                StatusCode::CONFLICT,
                "starter pack already purchased",
            ));
        }
    }
    let gold = p["gold"].as_i64().unwrap();
    let purchased = DateTime::from_timestamp_millis(
        v["purchaseDate"]
            .as_i64()
            .ok_or(ApiError::bad("purchaseDate"))?,
    )
    .ok_or(ApiError::bad("purchaseDate"))?;
    let price = v["price"].as_i64();
    if price.is_some_and(|p| !(0..=1_000_000_000).contains(&p)) {
        return Err(ApiError::bad("price"));
    }
    sqlx::query("INSERT INTO purchases(transaction_id,original_transaction_id,player_id,product_id,environment,price_milliunits,currency,storefront,gold,purchased_at,benefits) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)").bind(tid).bind(field(v,"originalTransactionId")?).bind(id).bind(pid).bind(field(v,"environment")?).bind(price).bind(v["currency"].as_str()).bind(v["storefront"].as_str()).bind(gold).bind(purchased).bind(&p).execute(&mut **tx).await?;
    entry(
        tx,
        id,
        gold,
        "iap",
        tid,
        &format!("iap:{tid}"),
        &json!({"product_id":pid}),
    )
    .await?;
    if pid.ends_with("starter_pack") {
        for (booster, count) in p["boosters"].as_object().unwrap() {
            sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(COALESCE((inventory->>$2)::int,0)+$3)) WHERE player_id=$1").bind(id).bind(booster).bind(count.as_i64().unwrap() as i32).execute(&mut **tx).await?;
        }
        sqlx::query("UPDATE wallets SET unlimited_until=GREATEST(COALESCE(unlimited_until,now()),now())+make_interval(secs=>$2) WHERE player_id=$1").bind(id).bind(p["unlimited_lives_seconds"].as_f64().unwrap()).execute(&mut **tx).await?;
    }
    Ok(())
}
pub async fn purchase(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<Purchase>,
) -> Result<Json<Value>> {
    let id = owner(&s, &h, b.player_id).await?;
    let v = apple::transaction(&b.signed_transaction)?;
    apple::server_check(&v).await?;
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    apply(&mut tx, id, &v).await?;
    let w = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(
        json!({"wallet":w,"transaction_id":v["transactionId"]}),
    ))
}
pub async fn restore(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<Restore>,
) -> Result<Json<Value>> {
    if b.signed_transactions.len() > 100 {
        return Err(ApiError::bad("max 100 transactions"));
    }
    let id = owner(&s, &h, b.player_id).await?;
    use futures_util::{StreamExt, stream};
    let deadline = tokio::time::Instant::now() + std::time::Duration::from_secs(18);
    let verified = stream::iter(b.signed_transactions.into_iter().enumerate().map(
        |(index, jws)| {
            let pool = s.pool.clone();
            async move {
                let result = async {
                    let v = apple::transaction(&jws)?;
                    let known: bool = sqlx::query_scalar(
                        "SELECT EXISTS(SELECT 1 FROM purchases WHERE transaction_id=$1)",
                    )
                    .bind(field(&v, "transactionId")?)
                    .fetch_one(&pool)
                    .await?;
                    if !known {
                        tokio::time::timeout_at(deadline, apple::server_check(&v))
                            .await
                            .map_err(|_| ApiError::unavailable())??;
                    }
                    Ok::<_, ApiError>(v)
                }
                .await;
                (index, result)
            }
        },
    ))
    .buffer_unordered(4)
    .collect::<Vec<_>>()
    .await;
    restore_items(&s, id, verified).await
}
async fn restore_items(
    s: &Arc<App>,
    id: Uuid,
    verified: Vec<(usize, Result<Value>)>,
) -> Result<Json<Value>> {
    let mut results = Vec::new();
    for (index, result) in verified {
        let result = async {
            let v = result?;
            let mut tx = s.pool.begin().await?;
            lock(&mut tx, id).await?;
            apply(&mut tx, id, &v).await?;
            tx.commit().await?;
            Ok::<_, ApiError>(v)
        }
        .await;
        results.push(match result {
            Ok(v) => json!({"index":index,"ok":true,"transaction_id":v["transactionId"]}),
            Err(e) => json!({"index":index,"ok":false,"status":e.0.as_u16(),"error":e.1}),
        });
    }
    results.sort_by_key(|v| v["index"].as_u64());
    let mut tx = s.pool.begin().await?;
    lock(&mut tx, id).await?;
    let w = snapshot(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(
        json!({"wallet":w,"processed":results.iter().filter(|v| v["ok"]==true).count(),"results":results}),
    ))
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Notification {
    signed_payload: String,
}
pub async fn notification(
    State(s): State<Arc<App>>,
    Json(b): Json<Notification>,
) -> Result<Json<Value>> {
    let v = apple::verify(&b.signed_payload)?;
    let data = &v["data"];
    if data["bundleId"] != "com.orcub.puzzle" {
        return Err(ApiError::bad("bundle mismatch"));
    }
    apple::environment(data)?;
    if data["environment"] == "Production" {
        let app_id = std::env::var("APPLE_APP_ID")
            .ok()
            .and_then(|s| s.parse::<i64>().ok())
            .ok_or(ApiError::unavailable())?;
        if data["appAppleId"] != app_id {
            return Err(ApiError::bad("app id mismatch"));
        }
    }
    if matches!(
        v["notificationType"].as_str(),
        Some("REFUND" | "REVOKE" | "REFUND_REVERSED")
    ) {
        let t = apple::transaction(
            data["signedTransactionInfo"]
                .as_str()
                .ok_or(ApiError::bad("signedTransactionInfo"))?,
        )?;
        if t["environment"] != data["environment"] {
            return Err(ApiError::bad("notification environment mismatch"));
        }
        if v["notificationType"] == "REFUND_REVERSED" {
            reverse_refund(&s.pool, &t).await?;
        } else {
            revoke(&s.pool, &t).await?;
        }
    }
    Ok(Json(json!({"accepted":true})))
}
async fn reverse_refund(pool: &PgPool, v: &Value) -> Result<()> {
    let tid = field(v, "transactionId")?;
    let mut tx = pool.begin().await?;
    let id: Option<Uuid> =
        sqlx::query_scalar("SELECT player_id FROM purchases WHERE transaction_id=$1")
            .bind(tid)
            .fetch_optional(&mut *tx)
            .await?;
    if let Some(id) = id {
        lock(&mut tx, id).await?;
        sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,1))")
            .bind(tid)
            .execute(&mut *tx)
            .await?;
        let row=sqlx::query("UPDATE purchases SET revoked=false WHERE transaction_id=$1 AND revoked RETURNING gold,benefits").bind(tid).fetch_optional(&mut *tx).await?;
        if let Some(r) = row {
            let sequence: i64 = sqlx::query_scalar(
                "SELECT count(*) FROM wallet_ledger WHERE ref_id=$1 AND reason='refund_reversed'",
            )
            .bind(tid)
            .fetch_one(&mut *tx)
            .await?;
            entry(
                &mut tx,
                id,
                r.get("gold"),
                "refund_reversed",
                tid,
                &format!("refund_reversed:{tid}:{sequence}"),
                &json!({}),
            )
            .await?;
            let benefits: Value = r.get("benefits");
            if let Some(boosters) = benefits["boosters"].as_object() {
                for (kind, count) in boosters {
                    sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(COALESCE((inventory->>$2)::int,0)+$3)) WHERE player_id=$1").bind(id).bind(kind).bind(count.as_i64().unwrap() as i32).execute(&mut *tx).await?;
                }
                sqlx::query("UPDATE wallets SET unlimited_until=GREATEST(COALESCE(unlimited_until,now()),now())+make_interval(secs=>$2) WHERE player_id=$1").bind(id).bind(benefits["unlimited_lives_seconds"].as_f64().unwrap()).execute(&mut *tx).await?;
            }
        }
    }
    sqlx::query("DELETE FROM purchase_revocations WHERE transaction_id=$1")
        .bind(tid)
        .execute(&mut *tx)
        .await?;
    tx.commit().await?;
    Ok(())
}
async fn revoke(pool: &PgPool, v: &Value) -> Result<()> {
    let tid = field(v, "transactionId")?;
    let mut tx = pool.begin().await?;
    // Same player-before-transaction lock ordering as purchase and restore.
    let id: Option<Uuid> =
        sqlx::query_scalar("SELECT player_id FROM purchases WHERE transaction_id=$1")
            .bind(tid)
            .fetch_optional(&mut *tx)
            .await?;
    if let Some(id) = id {
        lock(&mut tx, id).await?;
    }
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,1))")
        .bind(tid)
        .execute(&mut *tx)
        .await?;
    let row = sqlx::query(
        "SELECT player_id,gold,product_id,revoked,benefits FROM purchases WHERE transaction_id=$1",
    )
    .bind(tid)
    .fetch_optional(&mut *tx)
    .await?;
    // An unknown transaction is tombstoned, so a later client submission cannot grant it.
    sqlx::query("INSERT INTO purchase_revocations(transaction_id,player_id) VALUES($1,$2) ON CONFLICT DO NOTHING").bind(tid).bind(id).execute(&mut *tx).await?;
    if let Some(r) = row
        && !r.get::<bool, _>("revoked")
    {
        let id: Uuid = r.get("player_id");
        let sequence: i64 = sqlx::query_scalar(
            "SELECT count(*) FROM wallet_ledger WHERE ref_id=$1 AND reason='refund'",
        )
        .bind(tid)
        .fetch_one(&mut *tx)
        .await?;
        entry(
            &mut tx,
            id,
            -r.get::<i64, _>("gold"),
            "refund",
            tid,
            &format!("refund:{tid}:{sequence}"),
            &json!({}),
        )
        .await?;
        sqlx::query("UPDATE purchases SET revoked=true WHERE transaction_id=$1")
            .bind(tid)
            .execute(&mut *tx)
            .await?;
        if r.get::<String, _>("product_id").ends_with("starter_pack") {
            let benefits: Value = r.get("benefits");
            for (booster, count) in benefits["boosters"].as_object().unwrap() {
                sqlx::query("UPDATE wallets SET inventory=jsonb_set(inventory,ARRAY[$2],to_jsonb(GREATEST(COALESCE((inventory->>$2)::int,0)-$3,0))),unlimited_until=NULL WHERE player_id=$1").bind(id).bind(booster).bind(count.as_i64().unwrap() as i32).execute(&mut *tx).await?;
            }
        }
    }
    tx.commit().await?;
    Ok(())
}
pub async fn merge(tx: &mut Tx<'_>, canonical: Uuid, guest: Uuid) -> Result<()> {
    lock(tx, canonical).await?;
    lock(tx, guest).await?;
    daily::merge(tx, canonical, guest).await?;
    let mut g = snapshot(tx, guest).await?;
    // Client-proven rewards are spendable on the original wallet, never portable between installs.
    let grants:i64=sqlx::query_scalar("SELECT COALESCE(sum(delta),0)::bigint FROM wallet_ledger WHERE player_id=$1 AND reason IN ('daily_gift','daily_puzzle','level_reward')").bind(guest).fetch_one(&mut **tx).await?;
    let gold = g["gold"].as_i64().unwrap();
    let portable = if gold < 0 {
        gold
    } else {
        (gold - grants).max(0)
    };
    let has_grants: bool = sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM wallet_ledger WHERE player_id=$1 AND reason IN ('daily_gift','daily_puzzle','level_reward'))").bind(guest).fetch_one(&mut **tx).await?;
    if has_grants {
        g["inventory"] = json!({});
        g["unlimited_until"] = Value::Null;
    }
    entry(
        tx,
        guest,
        portable - gold,
        "merge_excluded_grants",
        &canonical.to_string(),
        &format!("merge:{guest}"),
        &json!({}),
    )
    .await?;
    sqlx::query("UPDATE wallets SET gold=gold+$2 WHERE player_id=$1")
        .bind(canonical)
        .bind(portable)
        .execute(&mut **tx)
        .await?;
    sqlx::query("UPDATE wallets SET inventory=(SELECT COALESCE(jsonb_object_agg(k,n),'{}') FROM (SELECT k,sum(v::int) n FROM (SELECT key k,value v FROM jsonb_each_text(inventory) UNION ALL SELECT key,value FROM jsonb_each_text($2::jsonb)) x GROUP BY k) a), unlimited_until=GREATEST(unlimited_until,$3) WHERE player_id=$1").bind(canonical).bind(&g["inventory"]).bind(serde_json::from_value::<Option<DateTime<Utc>>>(g["unlimited_until"].clone()).map_err(|_|ApiError::unavailable())?).execute(&mut **tx).await?;
    sqlx::query("UPDATE wallet_ledger SET player_id=$1,idempotency_key=$2::uuid::text || ':' || idempotency_key WHERE player_id=$2")
        .bind(canonical)
        .bind(guest)
        .execute(&mut **tx)
        .await?;
    sqlx::query("UPDATE wallet_aliases SET player_id=$1 WHERE player_id=$2")
        .bind(canonical)
        .bind(guest)
        .execute(&mut **tx)
        .await?;
    sqlx::query("INSERT INTO wallet_aliases(token,player_id) VALUES($1,$2)")
        .bind(guest)
        .bind(canonical)
        .execute(&mut **tx)
        .await?;
    for table in ["purchases", "purchase_revocations"] {
        sqlx::query(&format!(
            "UPDATE {table} SET player_id=$1 WHERE player_id=$2"
        ))
        .bind(canonical)
        .bind(guest)
        .execute(&mut **tx)
        .await?;
    }
    sqlx::query("INSERT INTO economy_offers(player_id,first_shown_at,last_shown_at) SELECT $1,first_shown_at,last_shown_at FROM economy_offers WHERE player_id=$2 ON CONFLICT(player_id) DO UPDATE SET first_shown_at=LEAST(economy_offers.first_shown_at,excluded.first_shown_at),last_shown_at=GREATEST(economy_offers.last_shown_at,excluded.last_shown_at)").bind(canonical).bind(guest).execute(&mut **tx).await?;
    Ok(())
}
fn ad_policy(payer: bool, remove_ads: bool, acceptance: f64, churn: f64) -> Value {
    let mut cap = 6;
    let mut interval = 150;
    if acceptance >= 0.6 {
        cap = 3;
        interval = 240;
    }
    if churn >= 0.25 {
        cap = cap.min(2);
        interval = interval.max(300);
    }
    if payer || remove_ads {
        cap = 0;
    }
    json!({"interstitial_enabled":cap>0,"interstitial_min_level":8,"interstitial_wins_only":true,"interstitial_min_interval_seconds":interval,"interstitial_min_levels_apart":3,"interstitial_session_cap":cap,"interstitial_daily_cap":cap,"rewarded_daily_cap":20})
}
pub async fn personalize(s: &App, id: Uuid, value: &mut Value) -> Result<()> {
    let cached:Option<Value>=sqlx::query_scalar("SELECT value FROM player_value_cache WHERE player_id=$1 AND updated_at>now()-interval '1 hour'").bind(id).fetch_optional(&s.pool).await?;
    let stats = if let Some(v) = cached {
        v
    } else {
        let v: Value = sqlx::query_scalar(include_str!("../config/player-value.sql"))
            .bind(id)
            .fetch_one(&s.pool)
            .await?;
        sqlx::query("INSERT INTO player_value_cache(player_id,value) VALUES($1,$2) ON CONFLICT(player_id) DO UPDATE SET value=$2,updated_at=now()").bind(id).bind(&v).execute(&s.pool).await?;
        v
    };
    value["economy"] = catalog();
    value["ad_policy"] = ad_policy(
        stats["payer"].as_bool().unwrap_or(false),
        stats["remove_ads"].as_bool().unwrap_or(false),
        stats["rewarded_acceptance"].as_f64().unwrap_or(0.0),
        stats["interstitial_churn"].as_f64().unwrap_or(0.0),
    );
    value["player_value"] = stats.clone();
    value["offers"] = json!([]);
    let offers = catalog()["offers"].clone();
    if !stats["payer"].as_bool().unwrap_or(false)
        && stats["highest_level"].as_i64().unwrap_or(0)
            >= offers["starter_min_level"].as_i64().unwrap()
    {
        let row = sqlx::query(
            "SELECT first_shown_at,last_shown_at FROM economy_offers WHERE player_id=$1",
        )
        .bind(id)
        .fetch_optional(&s.pool)
        .await?;
        let now = Utc::now();
        let first = row
            .as_ref()
            .map(|r| r.get::<DateTime<Utc>, _>("first_shown_at"));
        let last = row
            .as_ref()
            .map(|r| r.get::<DateTime<Utc>, _>("last_shown_at"));
        let expires =
            first.map(|t| t + chrono::Duration::hours(offers["duration_hours"].as_i64().unwrap()));
        let repeat = last.map(|t| {
            t + chrono::Duration::hours(offers["repeat_cooldown_hours"].as_i64().unwrap())
        });
        if expires.is_none_or(|t| now < t) && repeat.is_none_or(|t| now >= t) {
            value["offers"] = json!([{"id":"starter_pack","product_id":"com.orcub.puzzle.starter_pack","expires_at":expires,"repeat_after":repeat}]);
        }
    }
    Ok(())
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct OfferShown {
    player_id: Uuid,
}
pub async fn offer_shown(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<OfferShown>,
) -> Result<Json<Value>> {
    let id = owner(&s, &h, b.player_id).await?;
    let mut config = json!({});
    personalize(&s, id, &mut config).await?;
    if config["offers"].as_array().is_none_or(|v| v.is_empty()) {
        return Err(ApiError::bad("offer ineligible"));
    }
    let c = catalog()["offers"].clone();
    let row=sqlx::query("INSERT INTO economy_offers(player_id) VALUES($1) ON CONFLICT(player_id) DO UPDATE SET last_shown_at=now() WHERE economy_offers.last_shown_at<now()-make_interval(hours=>$2) RETURNING first_shown_at,last_shown_at").bind(id).bind(c["repeat_cooldown_hours"].as_i64().unwrap() as i32).fetch_optional(&s.pool).await?.ok_or(ApiError(StatusCode::CONFLICT,"offer cooldown"))?;
    let first: DateTime<Utc> = row.get("first_shown_at");
    let last: DateTime<Utc> = row.get("last_shown_at");
    Ok(Json(
        json!({"accepted":true,"expires_at":first+chrono::Duration::hours(c["duration_hours"].as_i64().unwrap()),"repeat_after":last+chrono::Duration::hours(c["repeat_cooldown_hours"].as_i64().unwrap())}),
    ))
}
pub(crate) async fn revenue(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(w): Query<Window>,
) -> Result<Response> {
    analytics(&s, &h, w, include_str!("../config/revenue.sql")).await
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn policy_rules() {
        let p = ad_policy(false, false, 0.0, 0.0);
        assert_eq!(p["interstitial_session_cap"], 6);
        assert_eq!(p["interstitial_daily_cap"], 6);
        assert_eq!(
            ad_policy(true, false, 0.0, 0.0)["interstitial_enabled"],
            false
        );
        assert_eq!(
            ad_policy(false, true, 0.0, 0.0)["interstitial_session_cap"],
            0
        );
        assert_eq!(
            ad_policy(false, false, 0.6, 0.0)["interstitial_session_cap"],
            3
        );
        assert_eq!(
            ad_policy(false, false, 0.9, 0.25)["interstitial_session_cap"],
            2
        );
    }
    #[test]
    fn prices() {
        assert_eq!(continue_price(0).unwrap(), 60);
        assert_eq!(continue_price(1).unwrap(), 90);
        assert_eq!(continue_price(2).unwrap(), 135);
        assert_eq!(continue_price(3).unwrap(), 203);
        assert!(continue_price(20).is_err());
    }
}

#[cfg(test)]
mod database_tests {
    use super::*;
    fn action(id: Uuid, reason: &str) -> Action {
        Action {
            player_id: id,
            idempotency_key: Uuid::new_v4(),
            reason: reason.into(),
            ref_id: Uuid::new_v4(),
            booster: None,
            utc_day: None,
        }
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn wallet_purchase_refund_merge_and_analytics(pool: PgPool) {
        let s = new_state(pool.clone(), "admin", false);
        let id = Uuid::new_v4();
        let guest = Uuid::new_v4();
        for player in [id, guest] {
            sqlx::query("INSERT INTO players(id) VALUES($1)")
                .bind(player)
                .execute(&pool)
                .await
                .unwrap();
        }
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest("secret"))).execute(&pool).await.unwrap();
        let mut h = HeaderMap::new();
        h.insert("x-install-token", "secret".parse().unwrap());
        let b = action(id, "daily_gift");
        let key = b.idempotency_key;
        let reference = b.ref_id;
        assert_eq!(
            grant(State(s.clone()), h.clone(), Json(b)).await.unwrap().0["wallet"]["gold"],
            catalog()["grants"]["daily_gift"].as_i64().unwrap()
        );
        let mut replay = action(id, "daily_gift");
        replay.idempotency_key = key;
        replay.ref_id = reference;
        assert_eq!(
            grant(State(s.clone()), h.clone(), Json(replay))
                .await
                .unwrap()
                .0["wallet"]["gold"],
            catalog()["grants"]["daily_gift"].as_i64().unwrap()
        );
        assert!(
            grant(State(s.clone()), h.clone(), Json(action(id, "daily_gift")))
                .await
                .is_err()
        );
        assert!(
            spend(
                State(s.clone()),
                h.clone(),
                Json(action(id, "refill_lives"))
            )
            .await
            .is_err()
        );
        let v = json!({"transactionId":"1234","originalTransactionId":"1234","productId":"com.orcub.puzzle.starter_pack","environment":"Sandbox","appAccountToken":id,"purchaseDate":Utc::now().timestamp_millis(),"price":1990,"currency":"USD","storefront":"USA"});
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, id).await.unwrap();
        apply(&mut tx, id, &v).await.unwrap();
        apply(&mut tx, id, &v).await.unwrap();
        tx.commit().await.unwrap();
        let b = action(id, "refill_lives");
        let key = b.idempotency_key;
        let reference = b.ref_id;
        assert_eq!(
            spend(State(s.clone()), h.clone(), Json(b)).await.unwrap().0["wallet"]["gold"],
            430
        );
        let mut b = action(id, "refill_lives");
        b.idempotency_key = key;
        b.ref_id = reference;
        assert_eq!(
            spend(State(s.clone()), h.clone(), Json(b)).await.unwrap().0["wallet"]["gold"],
            430
        );
        let mut value = json!({});
        personalize(&s, id, &mut value).await.unwrap();
        assert_eq!(value["ad_policy"]["interstitial_enabled"], true);
        assert_eq!(value["player_value"]["payer"], false);
        assert_eq!(value["player_value"]["ltv_iap"], json!({}));
        let mut ah = HeaderMap::new();
        ah.insert("admin_token", "admin".parse().unwrap());
        assert!(
            revenue(
                State(s.clone()),
                ah,
                Query(Window {
                    start: None,
                    end: None,
                    after: None
                })
            )
            .await
            .is_ok()
        );
        revoke(&pool, &v).await.unwrap();
        revoke(&pool, &v).await.unwrap();
        let mut tx = pool.begin().await.unwrap();
        assert_eq!(snapshot(&mut tx, id).await.unwrap()["gold"], -70);
        entry(&mut tx, id, 0, "test", "ref", "guest-key", &json!({}))
            .await
            .unwrap();
        lock(&mut tx, guest).await.unwrap();
        entry(
            &mut tx,
            guest,
            30,
            "daily_gift",
            "ref",
            "guest-key",
            &json!({}),
        )
        .await
        .unwrap();
        merge(&mut tx, id, guest).await.unwrap();
        assert_eq!(snapshot(&mut tx, id).await.unwrap()["gold"], -70);
        tx.commit().await.unwrap();
        let total: i64 =
            sqlx::query_scalar("SELECT sum(delta)::bigint FROM wallet_ledger WHERE player_id=$1")
                .bind(id)
                .fetch_one(&pool)
                .await
                .unwrap();
        assert_eq!(total, -70);
        sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,1,$2)")
            .bind(guest)
            .bind(json!({"highest_level":10}))
            .execute(&pool)
            .await
            .unwrap();
        let mut offers = json!({});
        personalize(&s, guest, &mut offers).await.unwrap();
        assert_eq!(offers["offers"].as_array().unwrap().len(), 1);
        sqlx::query(
            "UPDATE economy_offers SET first_shown_at=now()-interval '49 hours' WHERE player_id=$1",
        )
        .bind(guest)
        .execute(&pool)
        .await
        .unwrap();
        sqlx::query("INSERT INTO economy_offers(player_id,first_shown_at) VALUES($1,now()-interval '49 hours') ON CONFLICT(player_id) DO UPDATE SET first_shown_at=excluded.first_shown_at").bind(guest).execute(&pool).await.unwrap();
        personalize(&s, guest, &mut offers).await.unwrap();
        assert_eq!(offers["offers"], json!([]));
        let n: i64 = sqlx::query_scalar("SELECT count(*) FROM wallet_ledger WHERE reason='refund'")
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(n, 1);
    }
}

#[cfg(test)]
mod concurrency_tests {
    use super::*;
    #[sqlx::test(migrations = "./migrations")]
    async fn concurrent_spends_cannot_overdraw(pool: PgPool) {
        let id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest("concurrent"))).execute(&pool).await.unwrap();
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, id).await.unwrap();
        entry(&mut tx, id, 100, "test", "test", "seed", &json!({}))
            .await
            .unwrap();
        tx.commit().await.unwrap();
        let state = new_state(pool.clone(), "admin", false);
        let mut h = HeaderMap::new();
        h.insert("x-install-token", "concurrent".parse().unwrap());
        let action = || Action {
            player_id: id,
            idempotency_key: Uuid::new_v4(),
            reason: "refill_lives".into(),
            ref_id: Uuid::new_v4(),
            booster: None,
            utc_day: None,
        };
        let (a, b) = tokio::join!(
            spend(State(state.clone()), h.clone(), Json(action())),
            spend(State(state), h, Json(action()))
        );
        assert_ne!(a.is_ok(), b.is_ok());
        let gold: i64 = sqlx::query_scalar("SELECT gold FROM wallets WHERE player_id=$1")
            .bind(id)
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(gold, 10);
    }
}

#[cfg(test)]
mod authority_tests {
    use super::*;
    async fn player(pool: &PgPool) -> (Uuid, HeaderMap) {
        let id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest(&id.to_string()))).execute(pool).await.unwrap();
        let mut h = HeaderMap::new();
        h.insert("x-install-token", id.to_string().parse().unwrap());
        (id, h)
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn restore_transfer_partial_failure_and_sandbox_revenue(pool: PgPool) {
        let (old, _) = player(&pool).await;
        let (new, _) = player(&pool).await;
        let s = new_state(pool.clone(), "admin", false);
        let v = json!({"transactionId":"remove1","originalTransactionId":"original1","productId":"com.orcub.puzzle.remove_ads","environment":"Sandbox","appAccountToken":old,"purchaseDate":Utc::now().timestamp_millis(),"price":3990,"currency":"USD"});
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, old).await.unwrap();
        apply(&mut tx, old, &v).await.unwrap();
        tx.commit().await.unwrap();
        let r = restore_items(
            &s,
            new,
            vec![(0, Err(ApiError::bad("invalid JWS"))), (1, Ok(v.clone()))],
        )
        .await
        .unwrap()
        .0;
        assert_eq!(r["processed"], 1);
        assert_eq!(r["results"][0]["ok"], false);
        assert_eq!(r["wallet"]["remove_ads"], true);
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, old).await.unwrap();
        assert_eq!(snapshot(&mut tx, old).await.unwrap()["remove_ads"], false);
        assert!(apply(&mut tx, old, &v).await.is_err());
        tx.rollback().await.unwrap();
        let n: i64 = sqlx::query_scalar("SELECT count(*) FROM entitlement_transfers")
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(n, 1);
        let stats: Value = sqlx::query_scalar(include_str!("../config/player-value.sql"))
            .bind(new)
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(stats["payer"], false);
        assert_eq!(stats["ltv_iap"], json!({}));
        let revenue: Value = sqlx::query_scalar(include_str!("../config/revenue.sql"))
            .bind(Utc::now() - chrono::Duration::days(1))
            .bind(Utc::now() + chrono::Duration::days(1))
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(revenue["by_product"], json!([]));
        reverse_refund(&pool, &v).await.unwrap();
        revoke(&pool, &v).await.unwrap();
        reverse_refund(&pool, &v).await.unwrap();
        reverse_refund(&pool, &v).await.unwrap();
        let n: i64 =
            sqlx::query_scalar("SELECT count(*) FROM wallet_ledger WHERE reason='refund_reversed'")
                .fetch_one(&pool)
                .await
                .unwrap();
        assert_eq!(n, 1);
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn repeated_guest_grants_cannot_farm_merge(pool: PgPool) {
        let (canonical, _) = player(&pool).await;
        let s = new_state(pool.clone(), "admin", false);
        for _ in 0..3 {
            let (guest, h) = player(&pool).await;
            let b = Action {
                player_id: guest,
                reason: "daily_gift".into(),
                ref_id: Uuid::new_v4(),
                idempotency_key: Uuid::new_v4(),
                booster: None,
                utc_day: None,
            };
            let _ = grant(State(s.clone()), h.clone(), Json(b)).await.unwrap();
            let b = Action {
                player_id: guest,
                reason: "daily_gift".into(),
                ref_id: Uuid::new_v4(),
                idempotency_key: Uuid::new_v4(),
                booster: None,
                utc_day: None,
            };
            assert!(grant(State(s.clone()), h, Json(b)).await.is_err());
            let mut tx = pool.begin().await.unwrap();
            merge(&mut tx, canonical, guest).await.unwrap();
            assert_eq!(snapshot(&mut tx, canonical).await.unwrap()["gold"], 0);
            tx.commit().await.unwrap();
        }
        let (guest, _) = player(&pool).await;
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, guest).await.unwrap();
        let v = json!({"transactionId":"bought","originalTransactionId":"bought","productId":"com.orcub.puzzle.gold_small","environment":"Sandbox","appAccountToken":guest,"purchaseDate":Utc::now().timestamp_millis()});
        apply(&mut tx, guest, &v).await.unwrap();
        merge(&mut tx, canonical, guest).await.unwrap();
        assert_eq!(snapshot(&mut tx, canonical).await.unwrap()["gold"], 100);
        tx.commit().await.unwrap();
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn consume_idempotency_limits_and_regeneration(pool: PgPool) {
        let (id, h) = player(&pool).await;
        let s = new_state(pool.clone(), "admin", false);
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, id).await.unwrap();
        tx.commit().await.unwrap();
        sqlx::query("UPDATE wallets SET inventory='{\"hint\":1}',lives=1,lives_updated_at=now()-interval '65 minutes' WHERE player_id=$1").bind(id).execute(&pool).await.unwrap();
        let key = Uuid::new_v4();
        let attempt = Uuid::new_v4();
        let b = || Consume {
            player_id: id,
            kind: "booster_hint".into(),
            count: 1,
            attempt_ref: attempt,
            idempotency_key: key,
        };
        let w = consume(State(s.clone()), h.clone(), Json(b()))
            .await
            .unwrap()
            .0;
        assert_eq!(w["wallet"]["lives"], 3);
        assert_eq!(w["wallet"]["inventory"]["hint"], 0);
        let _ = consume(State(s.clone()), h.clone(), Json(b()))
            .await
            .unwrap();
        let mut next = b();
        next.idempotency_key = Uuid::new_v4();
        assert!(
            consume(State(s.clone()), h.clone(), Json(next))
                .await
                .is_err()
        );
        let mut next = b();
        next.kind = "life".into();
        assert!(
            consume(State(s.clone()), h.clone(), Json(next))
                .await
                .is_err()
        );
        let next = Consume {
            player_id: id,
            kind: "life".into(),
            count: 4,
            attempt_ref: attempt,
            idempotency_key: Uuid::new_v4(),
        };
        assert!(
            consume(State(s.clone()), h.clone(), Json(next))
                .await
                .is_err()
        );
        sqlx::query(
            "UPDATE wallets SET lives_updated_at=now()-interval '10 hours' WHERE player_id=$1",
        )
        .bind(id)
        .execute(&pool)
        .await
        .unwrap();
        let mut tx = pool.begin().await.unwrap();
        lock(&mut tx, id).await.unwrap();
        assert_eq!(snapshot(&mut tx, id).await.unwrap()["lives"], 5);
        tx.commit().await.unwrap();
    }
}
