use crate::*;
use axum::extract::RawQuery;
use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use openssl::{hash::MessageDigest, pkey::PKey, sign::Verifier};
use std::{collections::HashMap, sync::OnceLock};
use tokio::sync::Mutex;

type Keys = HashMap<String, String>;
static KEYS: OnceLock<Mutex<(Instant, Instant, Keys)>> = OnceLock::new();
fn verify(raw: &str, keys: &Keys) -> Result<HashMap<String, String>> {
    let (content, tail) = raw
        .split_once("&signature=")
        .ok_or(ApiError::unauthorized())?;
    let (_, key_id) = tail
        .split_once("&key_id=")
        .ok_or(ApiError::unauthorized())?;
    if key_id.contains('&') || tail.split('&').count() != 2 {
        return Err(ApiError::unauthorized());
    }
    let parsed = reqwest::Url::parse(&format!("https://ssv.invalid/?{raw}"))
        .map_err(|_| ApiError::unauthorized())?;
    let mut fields = HashMap::new();
    for (key, value) in parsed.query_pairs() {
        if fields
            .insert(key.into_owned(), value.into_owned())
            .is_some()
        {
            return Err(ApiError::unauthorized());
        }
    }
    let pem = fields
        .get("key_id")
        .and_then(|id| keys.get(id))
        .ok_or(ApiError::unauthorized())?;
    let sig = URL_SAFE_NO_PAD
        .decode(
            fields
                .get("signature")
                .ok_or(ApiError::unauthorized())?
                .trim_end_matches('='),
        )
        .map_err(|_| ApiError::unauthorized())?;
    let key = PKey::public_key_from_pem(pem.as_bytes()).map_err(|_| ApiError::unauthorized())?;
    let mut verifier =
        Verifier::new(MessageDigest::sha256(), &key).map_err(|_| ApiError::unauthorized())?;
    if !verifier
        .verify_oneshot(&sig, content.as_bytes())
        .unwrap_or(false)
    {
        return Err(ApiError::unauthorized());
    }
    Ok(fields)
}
pub async fn callback(State(s): State<Arc<App>>, RawQuery(raw): RawQuery) -> Result<Json<Value>> {
    let raw = raw.ok_or(ApiError::unauthorized())?;
    let cache = KEYS.get_or_init(|| Mutex::new((Instant::now(), Instant::now(), Keys::new())));
    let id = reqwest::Url::parse(&format!("https://ssv.invalid/?{raw}"))
        .map_err(|_| ApiError::unauthorized())?
        .query_pairs()
        .find(|(k, _)| k == "key_id")
        .map(|(_, v)| v.into_owned())
        .ok_or(ApiError::unauthorized())?;
    let refresh = {
        let mut keys = cache.lock().await;
        let due = keys.0 <= Instant::now() || !keys.2.contains_key(&id);
        if due && keys.1 <= Instant::now() {
            keys.0 = Instant::now() + Duration::from_secs(3600);
            keys.1 = Instant::now() + Duration::from_secs(60);
            true
        } else {
            false
        }
    };
    if refresh {
        let fetched = async {
            let response: Value = reqwest::Client::new()
                .get("https://www.gstatic.com/admob/reward/verifier-keys.json")
                .timeout(Duration::from_secs(5))
                .send()
                .await?
                .error_for_status()?
                .json()
                .await?;
            Ok::<_, reqwest::Error>(response)
        }
        .await;
        if let Ok(response) = fetched
            && let Some(list) = response["keys"].as_array()
        {
            let fresh: Keys = list
                .iter()
                .filter_map(|k| {
                    Some((
                        k["keyId"].as_u64()?.to_string(),
                        k["pem"].as_str()?.to_owned(),
                    ))
                })
                .collect();
            if !fresh.is_empty() {
                cache.lock().await.2 = fresh;
            }
        }
    }
    let fields = verify(&raw, &cache.lock().await.2)?;
    // The signature proves the request came from Google. Callbacks that cannot map to one of our
    // impressions (AdMob's console "Verify URL" ping, other units, missing/foreign ids) are
    // acknowledged with 200 and grant nothing, so Google neither fails verification nor retries.
    let ack = || Ok(Json(json!({"ok":true,"granted":false})));
    let unit = fields.get("ad_unit").map(String::as_str);
    if unit != Some("3829033868") && unit != Some("ca-app-pub-7345153763598739/3829033868") {
        return ack();
    }
    let Some(nonce) = fields
        .get("custom_data")
        .and_then(|v| v.parse::<Uuid>().ok())
    else {
        return ack();
    };
    let Some(player) = fields.get("user_id").and_then(|v| v.parse::<Uuid>().ok()) else {
        return ack();
    };
    let Some(transaction) = fields
        .get("transaction_id")
        .filter(|v| !v.is_empty() && v.len() <= 256)
    else {
        return ack();
    };
    let canonical: Option<Uuid> =
        sqlx::query_scalar("SELECT player_id FROM wallet_aliases WHERE token=$1")
            .bind(player)
            .fetch_optional(&s.pool)
            .await?;
    match confirm(&s, nonce, canonical.unwrap_or(player), transaction).await {
        Ok(()) => (),
        Err(e) if e.0 == StatusCode::UNAUTHORIZED => return ack(),
        Err(e) => return Err(e),
    }
    Ok(Json(json!({"ok":true})))
}
async fn confirm(s: &App, nonce: Uuid, player: Uuid, transaction: &str) -> Result<()> {
    let mut tx = s.pool.begin().await?;
    sqlx::query("SELECT id FROM players WHERE id=$1 FOR UPDATE")
        .bind(player)
        .fetch_optional(&mut *tx)
        .await?
        .ok_or(ApiError::unauthorized())?;
    let row = sqlx::query("SELECT transaction_id,created_at FROM rewarded_nonces WHERE nonce=$1 AND player_id=$2 FOR UPDATE").bind(nonce).bind(player).fetch_optional(&mut *tx).await?.ok_or(ApiError::unauthorized())?;
    if let Some(old) = row.get::<Option<String>, _>("transaction_id") {
        return if old == transaction {
            Ok(())
        } else {
            Err(ApiError::unauthorized())
        };
    }
    let created: DateTime<Utc> = row.get("created_at");
    if Utc::now() - created > chrono::Duration::days(1) {
        return Err(ApiError::unauthorized());
    }
    let used: i64=sqlx::query_scalar("SELECT count(*) FROM rewarded_nonces WHERE player_id=$1 AND (confirmed_at AT TIME ZONE 'UTC')::date=(now() AT TIME ZONE 'UTC')::date").bind(player).fetch_one(&mut *tx).await?;
    if used >= 20 {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "rewarded cap"));
    }
    let duplicate: bool =
        sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM rewarded_nonces WHERE transaction_id=$1)")
            .bind(transaction)
            .fetch_one(&mut *tx)
            .await?;
    if duplicate {
        return Err(ApiError::unauthorized());
    }
    sqlx::query("UPDATE rewarded_nonces SET transaction_id=$2,confirmed_at=now() WHERE nonce=$1")
        .bind(nonce)
        .bind(transaction)
        .execute(&mut *tx)
        .await?;
    sqlx::query("UPDATE wallets SET lives=5,lives_updated_at=now() WHERE player_id=$1")
        .bind(player)
        .execute(&mut *tx)
        .await?;
    economy::entry(
        &mut tx,
        player,
        0,
        "rewarded_refill",
        transaction,
        &format!("ssv:{nonce}"),
        &json!({"nonce":nonce,"revenue":false}),
    )
    .await?;
    tx.commit().await?;
    Ok(())
}
#[derive(Deserialize)]
pub struct NonceRequest {
    player_id: Uuid,
    placement: String,
}
pub async fn nonce(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(b): Json<NonceRequest>,
) -> Result<Json<Value>> {
    install_player(&s, &h, b.player_id).await?;
    if ![
        "rewarded_lives",
        "out_of_lives",
        "retry_lives",
        "rewarded_out_of_lives",
        "rewarded_retry_lives",
    ]
    .contains(&b.placement.as_str())
    {
        return Err(ApiError::bad("placement"));
    }
    let mut tx = s.pool.begin().await?;
    economy::lock(&mut tx, b.player_id).await?;
    let n:i64=sqlx::query_scalar("SELECT count(*) FROM rewarded_nonces WHERE player_id=$1 AND created_at>=date_trunc('day',now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'").bind(b.player_id).fetch_one(&mut *tx).await?;
    if n >= 20 {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "rewarded cap"));
    }
    let nonce = Uuid::new_v4();
    sqlx::query("INSERT INTO rewarded_nonces(nonce,player_id,placement) VALUES($1,$2,$3)")
        .bind(nonce)
        .bind(b.player_id)
        .bind(b.placement)
        .execute(&mut *tx)
        .await?;
    tx.commit().await?;
    Ok(Json(
        json!({"nonce":nonce,"player_id":b.player_id,"daily_cap":20}),
    ))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn signature_uses_exact_encoded_query() {
        use openssl::{
            ec::{EcGroup, EcKey},
            nid::Nid,
            sign::Signer,
        };
        let key = PKey::from_ec_key(
            EcKey::generate(&EcGroup::from_curve_name(Nid::X9_62_PRIME256V1).unwrap()).unwrap(),
        )
        .unwrap();
        let content = "custom_data=abc%20def&transaction_id=test";
        let mut signer = Signer::new(MessageDigest::sha256(), &key).unwrap();
        let sig = URL_SAFE_NO_PAD.encode(signer.sign_oneshot_to_vec(content.as_bytes()).unwrap());
        let keys = HashMap::from([(
            "1".into(),
            String::from_utf8(key.public_key_to_pem().unwrap()).unwrap(),
        )]);
        let raw = format!("{content}&signature={sig}&key_id=1");
        assert!(verify(&raw, &keys).is_ok());
        assert!(verify(&format!("{raw}&custom_data=unsigned"), &keys).is_err());
        assert!(verify(&raw.replace("abc", "bad"), &keys).is_err());
        assert!(verify(&raw, &HashMap::new()).is_err());
    }
}

#[cfg(test)]
mod database_tests {
    use super::*;
    #[sqlx::test(migrations = "./migrations")]
    async fn nonce_single_use_dedupe_and_cap(pool: PgPool) {
        let player = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(player)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO wallets(player_id,lives) VALUES($1,0)")
            .bind(player)
            .execute(&pool)
            .await
            .unwrap();
        let s = new_state(pool.clone(), "admin", false);
        let n = Uuid::new_v4();
        sqlx::query(
            "INSERT INTO rewarded_nonces(nonce,player_id,placement) VALUES($1,$2,'rewarded_lives')",
        )
        .bind(n)
        .bind(player)
        .execute(&pool)
        .await
        .unwrap();
        confirm(&s, n, player, "transaction").await.unwrap();
        confirm(&s, n, player, "transaction").await.unwrap();
        assert!(confirm(&s, n, player, "different").await.is_err());
        for i in 1..20 {
            let n = Uuid::new_v4();
            sqlx::query("INSERT INTO rewarded_nonces(nonce,player_id,placement) VALUES($1,$2,'rewarded_lives')").bind(n).bind(player).execute(&pool).await.unwrap();
            confirm(&s, n, player, &format!("tx{i}")).await.unwrap();
        }
        let n = Uuid::new_v4();
        sqlx::query(
            "INSERT INTO rewarded_nonces(nonce,player_id,placement) VALUES($1,$2,'rewarded_lives')",
        )
        .bind(n)
        .bind(player)
        .execute(&pool)
        .await
        .unwrap();
        assert!(confirm(&s, n, player, "overcap").await.is_err());
        let count: i64 = sqlx::query_scalar(
            "SELECT count(*) FROM wallet_ledger WHERE reason='rewarded_refill' AND delta=0",
        )
        .fetch_one(&pool)
        .await
        .unwrap();
        assert_eq!(count, 20);
    }
}
