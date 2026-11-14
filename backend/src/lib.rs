pub mod apple;
pub mod auth;
pub mod economy;
mod firebase_admin;
mod limits;
pub mod model;
pub mod ssv;
use axum::{
    Json, Router,
    extract::Request,
    extract::{DefaultBodyLimit, Query, State},
    http::{HeaderMap, HeaderValue, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
};
use chrono::{DateTime, Utc};
use model::*;
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use sqlx::{PgPool, Row};
use std::{
    sync::Arc,
    time::{Duration, Instant},
};
use subtle::ConstantTimeEq;
use tower_http::{
    compression::CompressionLayer,
    decompression::RequestDecompressionLayer,
    limit::RequestBodyLimitLayer,
    request_id::{MakeRequestUuid, PropagateRequestIdLayer, SetRequestIdLayer},
    timeout::TimeoutLayer,
};
use uuid::Uuid;
pub type Result<T> = std::result::Result<T, ApiError>;
#[derive(Debug)]
pub struct ApiError(StatusCode, &'static str);
impl ApiError {
    pub fn bad(s: &'static str) -> Self {
        Self(StatusCode::BAD_REQUEST, s)
    }
    pub fn unauthorized() -> Self {
        Self(StatusCode::UNAUTHORIZED, "unauthorized")
    }
    pub fn unavailable() -> Self {
        Self(StatusCode::SERVICE_UNAVAILABLE, "service unavailable")
    }
}
impl From<sqlx::Error> for ApiError {
    fn from(e: sqlx::Error) -> Self {
        tracing::error!(code=?e.as_database_error().and_then(|e|e.code()),"database operation failed");
        Self::unavailable()
    }
}
impl IntoResponse for ApiError {
    fn into_response(self) -> Response {
        let mut response = (self.0, Json(json!({"error":self.1}))).into_response();
        if self.0 == StatusCode::TOO_MANY_REQUESTS {
            response.headers_mut().insert(
                "retry-after",
                HeaderValue::from_static(if self.1 == "daily event cap" {
                    "86400"
                } else {
                    "60"
                }),
            );
        }
        response
    }
}
pub struct App {
    pub pool: PgPool,
    pub firebase: auth::Firebase,
    pub admin_hash: Vec<u8>,
    pub trust_country: bool,
    pub config: Value,
    pub limiter: limits::Limits,
    exports: Arc<tokio::sync::Semaphore>,
    exporting: std::sync::Mutex<std::collections::HashSet<Uuid>>,
    readiness: tokio::sync::Mutex<(Instant, bool)>,
}
pub fn digest(s: &str) -> Vec<u8> {
    Sha256::digest(s.as_bytes()).to_vec()
}
pub fn credential() -> String {
    format!("{}{}", Uuid::new_v4().simple(), Uuid::new_v4().simple())
}
pub fn new_state(pool: PgPool, admin: &str, _trust_country: bool) -> Arc<App> {
    Arc::new(App {
        pool,
        firebase: auth::Firebase::default(),
        admin_hash: digest(admin),
        trust_country: false,
        config: json!({"tuning":serde_json::from_str::<Value>(include_str!("../config/tuning.json")).expect("tuning"),"skill_model":serde_json::from_str::<Value>(include_str!("../config/skill-model.json")).expect("model"),"ad_policy":{"interstitial_min_interval_seconds":120,"interstitial_daily_cap":6,"interstitial_session_cap":6,"rewarded_daily_cap":20},"version":"1"}),
        limiter: limits::Limits::default(),
        exports: Arc::new(tokio::sync::Semaphore::new(2)),
        exporting: std::sync::Mutex::new(Default::default()),
        readiness: tokio::sync::Mutex::new((Instant::now(), false)),
    })
}
fn page(body: &'static str) -> impl IntoResponse {
    (
        [
            (axum::http::header::CONTENT_TYPE, "text/html; charset=utf-8"),
            (axum::http::header::CACHE_CONTROL, "public, max-age=600"),
        ],
        body,
    )
}

pub fn router(s: Arc<App>) -> Router {
    Router::new()
        // Developer website for the App Store listing (orcub.sathuashrith.com): ad networks crawl
        // /app-ads.txt; the App Store requires privacy, support and account-deletion pages.
        .route(
            "/",
            get(|| async { page(include_str!("../static/index.html")) }),
        )
        .route(
            "/privacy",
            get(|| async { page(include_str!("../static/privacy.html")) }),
        )
        .route(
            "/support",
            get(|| async { page(include_str!("../static/support.html")) }),
        )
        .route(
            "/delete-account",
            get(|| async { page(include_str!("../static/delete-account.html")) }),
        )
        .route(
            "/app-ads.txt",
            get(|| async {
                (
                    [
                        (
                            axum::http::header::CONTENT_TYPE,
                            "text/plain; charset=utf-8",
                        ),
                        (axum::http::header::CACHE_CONTROL, "public, max-age=3600"),
                    ],
                    include_str!("../static/app-ads.txt"),
                )
            }),
        )
        .route("/healthz", get(|| async { StatusCode::OK }))
        .route("/readyz", get(ready))
        .route("/v1/install", post(install))
        .route("/v1/events", post(events))
        .route("/v1/config", get(config))
        .route("/v1/ads/ssv", get(ssv::callback))
        .route("/v1/ads/nonce", post(ssv::nonce))
        .route("/v1/wallet", get(economy::wallet))
        .route("/v1/wallet/spend", post(economy::spend))
        .route("/v1/wallet/grant", post(economy::grant))
        .route("/v1/wallet/consume", post(economy::consume))
        .route("/v1/offers/shown", post(economy::offer_shown))
        .route("/v1/iap/apple", post(economy::purchase))
        .route("/v1/iap/restore", post(economy::restore))
        .route("/v1/iap/apple/notifications", post(economy::notification))
        .route("/v1/admin/revenue", get(economy::revenue))
        .route("/v1/save", put(save_put).get(save_get))
        .route("/v1/link", post(link))
        .route("/v1/account", delete(account_delete))
        .route("/v1/account/export", get(account_export))
        .route("/v1/admin/funnel", get(funnel))
        .route("/v1/admin/retention", get(retention))
        .route("/v1/admin/ads", get(ads))
        .route("/v1/admin/export/attempts", get(attempts))
        .layer(DefaultBodyLimit::max(2 * 1024 * 1024))
        .layer(RequestBodyLimitLayer::new(2 * 1024 * 1024))
        .layer(RequestDecompressionLayer::new())
        .layer(RequestBodyLimitLayer::new(2 * 1024 * 1024))
        .layer(CompressionLayer::new())
        .layer(TimeoutLayer::with_status_code(
            StatusCode::REQUEST_TIMEOUT,
            Duration::from_secs(30),
        ))
        .layer(middleware::from_fn_with_state(s.clone(), guard))
        .layer(PropagateRequestIdLayer::new(
            axum::http::HeaderName::from_static("x-request-id"),
        ))
        .layer(SetRequestIdLayer::new(
            axum::http::HeaderName::from_static("x-request-id"),
            MakeRequestUuid,
        ))
        .with_state(s)
}
async fn guard(State(s): State<Arc<App>>, req: Request, next: Next) -> Response {
    let path = req.uri().path();
    let admin_route = path.starts_with("/v1/admin/").then(|| path.to_owned());
    if !matches!(path, "/healthz" | "/readyz") {
        let peer = req
            .extensions()
            .get::<axum::extract::ConnectInfo<std::net::SocketAddr>>()
            .map(|p| p.0.ip());
        // Railway edge overwrites X-Real-IP. Only enable inside the isolated Railway
        // project network: no TCP proxy and no untrusted private services (README).
        // Never consume X-Forwarded-For, whose hop layout is not a stable contract.
        let railway = std::env::var_os("RAILWAY_ENVIRONMENT_ID").is_some();
        let ip = if railway && peer.is_some() {
            req.headers()
                .get("x-real-ip")
                .and_then(|v| v.to_str().ok())
                .and_then(|v| v.parse::<std::net::IpAddr>().ok())
                .or(peer)
        } else {
            peer
        };
        let install = path == "/v1/install";
        if !s.limiter.check(
            format!("ip:{install}:{ip:?}"),
            if install { 5 } else { 600 },
            60,
        ) {
            return (StatusCode::TOO_MANY_REQUESTS, [("retry-after", "60")]).into_response();
        }
        if let Some(token) = req
            .headers()
            .get("x-install-token")
            .and_then(|v| v.to_str().ok())
            && !s
                .limiter
                .check(format!("token:{}", hex(&digest(token))), 120, 60)
        {
            return (StatusCode::TOO_MANY_REQUESTS, [("retry-after", "60")]).into_response();
        }
    }
    let start = std::time::Instant::now();
    let method = req.method().clone();
    // Never log URLs (player ids/query strings), headers, tokens, bodies, or peer addresses.
    let sensitive = req.uri().path().starts_with("/v1/wallet")
        || req.uri().path().starts_with("/v1/iap")
        || req.uri().path().starts_with("/v1/ads");
    let mut response = next.run(req).await;
    if sensitive {
        response
            .headers_mut()
            .insert("cache-control", HeaderValue::from_static("no-store"));
    }
    if let Some(route) = admin_route {
        tracing::info!(%route,status = response.status().as_u16(), "admin access");
    }
    tracing::info!(%method,status=response.status().as_u16(),elapsed_ms=start.elapsed().as_millis(),"request");
    response
}
async fn ready(State(s): State<Arc<App>>) -> Result<StatusCode> {
    let mut cache = s.readiness.lock().await;
    if cache.0 <= Instant::now() {
        cache.1 = tokio::time::timeout(
            Duration::from_secs(2),
            sqlx::query("SELECT 1").execute(&s.pool),
        )
        .await
        .is_ok_and(|r| r.is_ok());
        cache.0 = Instant::now() + Duration::from_secs(2);
    }
    if cache.1 {
        Ok(StatusCode::OK)
    } else {
        Err(ApiError::unavailable())
    }
}
fn header<'a>(h: &'a HeaderMap, k: &str) -> Result<&'a str> {
    h.get(k)
        .and_then(|x| x.to_str().ok())
        .ok_or(ApiError::unauthorized())
}
async fn install_player(s: &App, h: &HeaderMap, id: Uuid) -> Result<()> {
    let token = header(h, "x-install-token")?;
    let valid: bool = sqlx::query_scalar(
        "SELECT EXISTS(SELECT 1 FROM installs WHERE player_id=$1 AND credential_hash=$2)",
    )
    .bind(id)
    .bind(hex(&digest(token)))
    .fetch_one(&s.pool)
    .await?;
    if !valid {
        return Err(ApiError::unauthorized());
    }
    Ok(())
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
async fn buckets(tx: &mut sqlx::Transaction<'_, sqlx::Postgres>, id: Uuid) -> Result<Value> {
    let experiment = "difficulty_baseline_v1";
    let hash = digest(&format!("{id}:{experiment}"));
    let bucket = if hash[0] < 128 {
        "control"
    } else {
        "baseline_b"
    };
    sqlx::query("INSERT INTO experiments(player_id,experiment,bucket) VALUES($1,$2,$3) ON CONFLICT DO NOTHING").bind(id).bind(experiment).bind(bucket).execute(&mut **tx).await?;
    let rows = sqlx::query(
        "SELECT experiment,bucket FROM experiments WHERE player_id=$1 ORDER BY experiment",
    )
    .bind(id)
    .fetch_all(&mut **tx)
    .await?;
    let mut map = serde_json::Map::new();
    for r in rows {
        map.insert(r.get("experiment"), Value::String(r.get("bucket")));
    }
    Ok(Value::Object(map))
}
async fn install(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(mut body): Json<Install>,
) -> Result<Response> {
    body.validate()?;
    let mut tx = s.pool.begin().await?;
    // Serialise repeated first-launch requests without allowing UUID-only credential recovery.
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,0))")
        .bind(body.install_id.to_string())
        .execute(&mut *tx)
        .await?;
    let existing =
        sqlx::query("SELECT player_id,credential_hash FROM installs WHERE install_id=$1")
            .bind(body.install_id)
            .fetch_optional(&mut *tx)
            .await?;
    let (id, token) = if let Some(r) = existing {
        let supplied = header(&h, "x-install-token")?;
        let expected: String = r.get("credential_hash");
        if !bool::from(expected.as_bytes().ct_eq(hex(&digest(supplied)).as_bytes())) {
            return Err(ApiError::unauthorized());
        }
        (r.get("player_id"), supplied.to_owned())
    } else {
        let id = Uuid::new_v4();
        let token = credential();
        let country = if s.trust_country {
            h.get("cf-ipcountry")
                .and_then(|x| x.to_str().ok())
                .filter(|x| {
                    x.len() == 2
                        && x.bytes().all(|b| b.is_ascii_alphabetic())
                        && *x != "XX"
                        && *x != "T1"
                })
                .map(str::to_ascii_uppercase)
        } else {
            None
        };
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(&mut *tx)
            .await?;
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,country,first_open_ts) VALUES($1,$2,$3,$4,$5,$6)").bind(body.install_id).bind(id).bind(hex(&digest(&token))).bind(serde_json::to_value(&body).map_err(|_|ApiError::bad("install"))?).bind(country).bind(body.first_open_ts).execute(&mut *tx).await?;
        (id, token)
    };
    let assignments = buckets(&mut tx, id).await?;
    tx.commit().await?;
    Ok((
        [("cache-control", "no-store")],
        Json(json!({"player_id":id,"install_token":token,"experiments":assignments})),
    )
        .into_response())
}
async fn events(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(body): Json<Batch>,
) -> Result<Json<Value>> {
    install_player(&s, &h, body.player_id).await?;
    if body.events.is_empty() || body.events.len() > 500 {
        return Err(ApiError::bad("batch size must be 1..500"));
    }
    let validated: Vec<_> = body
        .events
        .iter()
        .map(Event::validated)
        .collect::<Result<_>>()?;
    let mut tx = s.pool.begin().await?;
    sqlx::query("SELECT id FROM players WHERE id=$1 FOR SHARE")
        .bind(body.player_id)
        .fetch_optional(&mut *tx)
        .await?
        .ok_or(ApiError::unauthorized())?;
    let quota: Option<i32> = sqlx::query_scalar("INSERT INTO event_quotas(player_id,day,count) VALUES($1,(now() AT TIME ZONE 'UTC')::date,$2) ON CONFLICT(player_id,day) DO UPDATE SET count=event_quotas.count+excluded.count WHERE event_quotas.count+excluded.count<=10000 RETURNING count").bind(body.player_id).bind(body.events.len() as i32).fetch_optional(&mut *tx).await?;
    if quota.is_none() {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "daily event cap"));
    }
    let mut inserted = 0;
    for (event, (data, raw)) in body.events.iter().zip(validated) {
        let n=sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload,dev) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING").bind(event.event_id).bind(body.player_id).bind(&event.kind).bind(event.occurred_at).bind(if raw {json!({})} else {data.clone()}).bind(event.dev).execute(&mut *tx).await?.rows_affected();
        if n > 0 && raw {
            sqlx::query("INSERT INTO raw_events(event_id,payload) VALUES($1,$2)")
                .bind(event.event_id)
                .bind(data)
                .execute(&mut *tx)
                .await?;
        }
        inserted += n;
    }
    tx.commit().await?;
    Ok(Json(
        json!({"accepted":inserted,"duplicates":body.events.len() as u64-inserted}),
    ))
}
#[derive(Deserialize)]
struct PlayerQuery {
    player_id: Uuid,
}
async fn config(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(q): Query<PlayerQuery>,
) -> Result<Response> {
    install_player(&s, &h, q.player_id).await?;
    let mut tx = s.pool.begin().await?;
    let assignments = buckets(&mut tx, q.player_id).await?;
    tx.commit().await?;
    let mut value = s.config.clone();
    value["experiments"] = assignments;
    economy::personalize(&s, q.player_id, &mut value).await?;
    let bytes = serde_json::to_vec(&value).map_err(|_| ApiError::bad("config"))?;
    let etag = format!("\"{}\"", hex(&Sha256::digest(&bytes)));
    let headers = [
        ("etag", etag.as_str()),
        ("cache-control", "private, max-age=300"),
        ("vary", "x-install-token"),
    ];
    if h.get("if-none-match")
        .and_then(|x| x.to_str().ok())
        .is_some_and(|v| v.split(',').any(|t| t.trim() == etag || t.trim() == "*"))
    {
        return Ok((StatusCode::NOT_MODIFIED, headers).into_response());
    }
    Ok((headers, Json(value)).into_response())
}
async fn verified_uid(s: &App, h: &HeaderMap) -> Result<String> {
    let uid = s.firebase.verify(h).await?;
    if !s
        .limiter
        .check(format!("uid:{}", hex(&digest(&uid))), 120, 60)
    {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "rate limited"));
    }
    Ok(uid)
}
async fn uid_player(s: &App, h: &HeaderMap) -> Result<(String, Uuid)> {
    let uid = verified_uid(s, h).await?;
    let id = sqlx::query_scalar("SELECT id FROM players WHERE firebase_uid=$1")
        .bind(&uid)
        .fetch_optional(&s.pool)
        .await?
        .ok_or(ApiError(StatusCode::NOT_FOUND, "link account first"))?;
    Ok((uid, id))
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SaveRequest {
    revision: i64,
    data: SaveData,
}
async fn save_put(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(mut body): Json<SaveRequest>,
) -> Result<Response> {
    let (_, id) = uid_player(&s, &h).await?;
    body.data.validate()?;
    let mut tx = s.pool.begin().await?;
    sqlx::query("SELECT id FROM players WHERE id=$1 FOR UPDATE")
        .bind(id)
        .fetch_optional(&mut *tx)
        .await?
        .ok_or(ApiError::unauthorized())?;
    let old = sqlx::query("SELECT revision,data FROM saves WHERE player_id=$1")
        .bind(id)
        .fetch_optional(&mut *tx)
        .await?;
    let revision = old
        .as_ref()
        .map(|r| r.get::<i64, _>("revision"))
        .unwrap_or(0);
    if body.revision != revision {
        let mut data = old
            .as_ref()
            .map(|r| serde_json::from_value::<SaveData>(r.get("data")))
            .transpose()
            .map_err(|_| ApiError::unavailable())?;
        if let Some(d) = data.as_mut() {
            economy::save_fields(&mut tx, id, d).await?;
        }
        tx.commit().await?;
        return Ok((
            StatusCode::CONFLICT,
            Json(json!({"error":"revision_conflict","revision":revision,"data":data})),
        )
            .into_response());
    }
    if let Some(old) = old {
        body.data.merge_progress(
            &serde_json::from_value(old.get("data")).map_err(|_| ApiError::unavailable())?,
        );
    }
    economy::save_fields(&mut tx, id, &mut body.data).await?;
    let revision = revision
        .checked_add(1)
        .ok_or(ApiError::bad("revision exhausted"))?;
    sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,$2,$3) ON CONFLICT(player_id) DO UPDATE SET revision=$2,data=$3,updated_at=now()").bind(id).bind(revision).bind(serde_json::to_value(&body.data).map_err(|_|ApiError::bad("save"))?).execute(&mut *tx).await?;
    tx.commit().await?;
    Ok((
        [("cache-control", "no-store")],
        Json(json!({"revision":revision,"data":body.data})),
    )
        .into_response())
}
async fn save_get(State(s): State<Arc<App>>, h: HeaderMap) -> Result<Response> {
    let (_, id) = uid_player(&s, &h).await?;
    let r = sqlx::query("SELECT revision,data FROM saves WHERE player_id=$1")
        .bind(id)
        .fetch_optional(&s.pool)
        .await?;
    Ok((
        [("cache-control", "no-store")],
        Json(match r {
            Some(r) => {
                let mut data: SaveData =
                    serde_json::from_value(r.get("data")).map_err(|_| ApiError::unavailable())?;
                let mut tx = s.pool.begin().await?;
                economy::save_fields(&mut tx, id, &mut data).await?;
                tx.commit().await?;
                json!({"revision":r.get::<i64,_>("revision"),"data":data})
            }
            None => json!({"revision":0,"data":null}),
        }),
    )
        .into_response())
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LinkRequest {
    player_id: Uuid,
}
async fn link(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Json(body): Json<LinkRequest>,
) -> Result<Json<Value>> {
    let uid = verified_uid(&s, &h).await?;
    let previous = if let Some(token) = h.get("x-previous-firebase-token") {
        let mut old = HeaderMap::new();
        old.insert(
            "authorization",
            HeaderValue::from_str(&format!(
                "Bearer {}",
                token.to_str().map_err(|_| ApiError::unauthorized())?
            ))
            .map_err(|_| ApiError::unauthorized())?,
        );
        Some(s.firebase.verify_anonymous(&old).await?)
    } else {
        None
    };
    install_player(&s, &h, body.player_id).await?;
    let mut tx = s.pool.begin().await?;
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1,0))")
        .bind(&uid)
        .execute(&mut *tx)
        .await?;
    let target: Option<Uuid> = sqlx::query_scalar("SELECT id FROM players WHERE firebase_uid=$1")
        .bind(&uid)
        .fetch_optional(&mut *tx)
        .await?;
    let id = target.unwrap_or(body.player_id);
    let locked = sqlx::query(
        "SELECT id,firebase_uid FROM players WHERE id=$1 OR id=$2 ORDER BY id FOR UPDATE",
    )
    .bind(id)
    .bind(body.player_id)
    .fetch_all(&mut *tx)
    .await?;
    let source = locked
        .iter()
        .find(|r| r.get::<Uuid, _>("id") == body.player_id)
        .ok_or(ApiError::unauthorized())?;
    if source
        .get::<Option<String>, _>("firebase_uid")
        .is_some_and(|u| u != uid && previous.as_deref() != Some(u.as_str()))
    {
        return Err(ApiError::unauthorized());
    }
    if id != body.player_id {
        economy::merge(&mut tx, id, body.player_id).await?;
        // Canonical account owns consumables; only monotonic progress crosses accounts.
        let rows = sqlx::query(
            "SELECT player_id,revision,data FROM saves WHERE player_id=$1 OR player_id=$2",
        )
        .bind(id)
        .bind(body.player_id)
        .fetch_all(&mut *tx)
        .await?;
        let canonical = rows.iter().find(|r| r.get::<Uuid, _>("player_id") == id);
        let guest = rows
            .iter()
            .find(|r| r.get::<Uuid, _>("player_id") == body.player_id);
        if let Some(g) = guest {
            let guest: SaveData =
                serde_json::from_value(g.get("data")).map_err(|_| ApiError::unavailable())?;
            let mut data: SaveData = canonical
                .map(|r| serde_json::from_value(r.get("data")))
                .transpose()
                .map_err(|_| ApiError::unavailable())?
                .unwrap_or_else(|| guest.clone());
            data.merge_progress(&guest);
            data.merge_daily_history(&guest);
            economy::save_fields(&mut tx, id, &mut data).await?;
            let rev = canonical.map(|r| r.get::<i64, _>("revision")).unwrap_or(0) + 1;
            sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,$2,$3) ON CONFLICT(player_id) DO UPDATE SET revision=$2,data=$3,updated_at=now()").bind(id).bind(rev).bind(serde_json::to_value(data).map_err(|_|ApiError::unavailable())?).execute(&mut *tx).await?;
        }
        sqlx::query("INSERT INTO event_quotas(player_id,day,count) SELECT $1,day,count FROM event_quotas WHERE player_id=$2 ON CONFLICT(player_id,day) DO UPDATE SET count=LEAST(10000,event_quotas.count+excluded.count)").bind(id).bind(body.player_id).execute(&mut *tx).await?;
        sqlx::query("UPDATE installs SET player_id=$1 WHERE player_id=$2")
            .bind(id)
            .bind(body.player_id)
            .execute(&mut *tx)
            .await?;
        sqlx::query("UPDATE events SET player_id=$1 WHERE player_id=$2")
            .bind(id)
            .bind(body.player_id)
            .execute(&mut *tx)
            .await?;
        sqlx::query("DELETE FROM players WHERE id=$1")
            .bind(body.player_id)
            .execute(&mut *tx)
            .await?;
    } else {
        sqlx::query("UPDATE players SET firebase_uid=$1 WHERE id=$2")
            .bind(uid)
            .bind(id)
            .execute(&mut *tx)
            .await?;
    }
    let assignments = buckets(&mut tx, id).await?;
    tx.commit().await?;
    Ok(Json(json!({"player_id":id,"experiments":assignments})))
}
async fn account_player(s: &App, h: &HeaderMap, q: Option<Uuid>) -> Result<Uuid> {
    if h.contains_key("authorization") {
        return Ok(uid_player(s, h).await?.1);
    }
    let id = q.ok_or(ApiError::unauthorized())?;
    install_player(s, h, id).await?;
    let guest: bool = sqlx::query_scalar(
        "SELECT EXISTS(SELECT 1 FROM players WHERE id=$1 AND firebase_uid IS NULL)",
    )
    .bind(id)
    .fetch_one(&s.pool)
    .await?;
    if !guest {
        return Err(ApiError::unauthorized());
    }
    Ok(id)
}
#[derive(Deserialize)]
struct AccountQuery {
    player_id: Option<Uuid>,
}
struct ExportGuard {
    state: Arc<App>,
    id: Uuid,
    _permit: tokio::sync::OwnedSemaphorePermit,
}
impl Drop for ExportGuard {
    fn drop(&mut self) {
        self.state.exporting.lock().unwrap().remove(&self.id);
    }
}
async fn account_delete(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(q): Query<AccountQuery>,
) -> Result<Response> {
    let uid = if h.contains_key("authorization") {
        Some(verified_uid(&s, &h).await?)
    } else {
        None
    };
    let subject = if let Some(uid) = &uid {
        hex(&digest(&format!("uid:{uid}")))
    } else {
        hex(&digest(&format!(
            "guest:{}:{}",
            q.player_id.ok_or(ApiError::unauthorized())?,
            header(&h, "x-install-token")?
        )))
    };
    let deleted: bool = sqlx::query_scalar(
        "SELECT EXISTS(SELECT 1 FROM deletion_tombstones WHERE subject_hash=$1 AND deleted_at>now()-interval '30 days')",
    )
    .bind(&subject)
    .fetch_one(&s.pool)
    .await?;
    let resolved = account_player(&s, &h, q.player_id).await;
    if !deleted {
        resolved?;
    }
    {
        let mut tx = s.pool.begin().await?;
        sqlx::query(
            "INSERT INTO deletion_tombstones(subject_hash) VALUES($1) ON CONFLICT(subject_hash) DO UPDATE SET deleted_at=now()",
        )
        .bind(&subject)
        .execute(&mut *tx)
        .await?;
        sqlx::query("DELETE FROM players WHERE ($1::text IS NOT NULL AND firebase_uid=$1) OR ($1::text IS NULL AND id=$2 AND firebase_uid IS NULL)")
            .bind(&uid).bind(q.player_id)
            .execute(&mut *tx)
            .await?;
        tx.commit().await?;
    }
    let identity_deleted = if let Some(uid) = uid {
        firebase_admin::delete(&uid).await.unwrap_or_else(|_| {
            tracing::warn!(code = "identity_delete_retry");
            false
        })
    } else {
        true
    };
    Ok(([("cache-control","no-store")],Json(json!({"receipt_id":Uuid::new_v4(),"deleted_at":Utc::now(),"scope":"first_party_backend","deleted":true,"identity_deleted":identity_deleted}))).into_response())
}
async fn account_export(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(q): Query<AccountQuery>,
) -> Result<Response> {
    let id = account_player(&s, &h, q.player_id).await?;
    let permit = s
        .exports
        .clone()
        .try_acquire_owned()
        .map_err(|_| ApiError(StatusCode::TOO_MANY_REQUESTS, "export busy"))?;
    if !s.exporting.lock().unwrap().insert(id) {
        return Err(ApiError(StatusCode::TOO_MANY_REQUESTS, "export busy"));
    }
    let export_guard = ExportGuard {
        state: s.clone(),
        id,
        _permit: permit,
    };
    let body=tokio::time::timeout(Duration::from_secs(15), async {
        let mut tx=s.pool.begin().await?;
        sqlx::query("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY").execute(&mut *tx).await?;
        sqlx::query("SET LOCAL statement_timeout='5s'").execute(&mut *tx).await?;
        sqlx::query("SET LOCAL idle_in_transaction_session_timeout='5s'").execute(&mut *tx).await?;
        if !h.contains_key("authorization") {
            let guest: bool=sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM players WHERE id=$1 AND firebase_uid IS NULL)").bind(id).fetch_one(&mut *tx).await?;
            if !guest { return Err(ApiError::unauthorized()); }
        }
        let mut out=Vec::new();
        for table in ["players","installs","experiments","saves","events","wallets","wallet_ledger","rewarded_nonces","unlimited_windows","daily_claims","daily_streaks","booster_unlock_claims","purchases","purchase_revocations","economy_offers","wallet_aliases","entitlement_transfers","player_value_cache","event_quotas","raw_events"] {
            let filter=if table=="players" {"id"} else {"player_id"};
            let sql=if table=="raw_events" {"SELECT to_jsonb(r) AS data FROM raw_events r JOIN events e USING(event_id) WHERE e.player_id=$1".to_string()} else {format!("SELECT to_jsonb(t)-'credential_hash' AS data FROM {table} t WHERE {filter}=$1")};
            use futures_util::TryStreamExt;
            let mut rows=sqlx::query(&sql).bind(id).fetch(&mut *tx);
            while let Some(row)=rows.try_next().await? {
                serde_json::to_writer(&mut out,&json!({"table":table,"row":row.get::<Value,_>("data")})).map_err(|_|ApiError::unavailable())?;
                out.push(b'\n');
                if out.len()>16*1024*1024 { return Err(ApiError(StatusCode::PAYLOAD_TOO_LARGE,"export too large; contact support")); }
            }
        }
        tx.commit().await?;
        out.extend_from_slice(b"{\"table\":\"_end\",\"ok\":true}\n");
        Ok::<_,ApiError>(out)
    }).await.map_err(|_|ApiError::unavailable())??;

    // Hold admission slots through body delivery, but never a database connection.
    let stream = async_stream::stream! {
        let _guard=export_guard;
        let deadline=Instant::now()+Duration::from_secs(15);
        for chunk in body.chunks(64*1024) {
            if Instant::now()>=deadline {
                yield Err(std::io::Error::new(std::io::ErrorKind::TimedOut,"export deadline"));
                break;
            }
            yield Ok::<_,std::io::Error>(axum::body::Bytes::copy_from_slice(chunk));
        }
    };
    Ok((
        [
            ("content-type", "application/x-ndjson"),
            ("cache-control", "no-store"),
        ],
        axum::body::Body::from_stream(stream),
    )
        .into_response())
}
fn admin(s: &App, h: &HeaderMap) -> Result<()> {
    let supplied = digest(
        h.get("x-admin-token")
            .and_then(|v| v.to_str().ok())
            .map(Ok)
            .unwrap_or_else(|| header(h, "admin_token"))?,
    );
    if !bool::from(s.admin_hash.ct_eq(&supplied)) {
        return Err(ApiError::unauthorized());
    }
    Ok(())
}
#[derive(Deserialize)]
struct Window {
    start: Option<DateTime<Utc>>,
    end: Option<DateTime<Utc>>,
    after: Option<Uuid>,
}
impl Window {
    fn bounds(&self) -> Result<(DateTime<Utc>, DateTime<Utc>)> {
        let end = self.end.unwrap_or_else(Utc::now);
        let start = self.start.unwrap_or(end - chrono::Duration::days(30));
        if start >= end || end - start > chrono::Duration::days(93) {
            return Err(ApiError::bad("window must be positive and <=93 days"));
        }
        Ok((start, end))
    }
}
async fn analytics(s: &App, h: &HeaderMap, w: Window, sql: &str) -> Result<Response> {
    admin(s, h)?;
    let (start, end) = w.bounds()?;
    let rows = sqlx::query(sql)
        .bind(start)
        .bind(end)
        .fetch_all(&s.pool)
        .await?;
    Ok((
        [("cache-control", "no-store")],
        Json(
            rows.iter()
                .map(|r| r.get::<Value, _>("data"))
                .collect::<Vec<_>>(),
        ),
    )
        .into_response())
}
async fn funnel(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(w): Query<Window>,
) -> Result<Response> {
    analytics(&s, &h, w, include_str!("../config/funnel.sql")).await
}
async fn retention(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(w): Query<Window>,
) -> Result<Response> {
    analytics(&s, &h, w, include_str!("../config/retention.sql")).await
}
async fn ads(State(s): State<Arc<App>>, h: HeaderMap, Query(w): Query<Window>) -> Result<Response> {
    analytics(&s, &h, w, include_str!("../config/ads.sql")).await
}
async fn attempts(
    State(s): State<Arc<App>>,
    h: HeaderMap,
    Query(w): Query<Window>,
) -> Result<Response> {
    admin(&s, &h)?;
    let (start, end) = w.bounds()?;
    let rows=sqlx::query("SELECT event_id,payload FROM events WHERE NOT dev AND NOT EXISTS(SELECT 1 FROM installs dev WHERE dev.player_id=events.player_id AND dev.metadata->>'platform' IN ('editor','ios-dev')) AND kind='level_end' AND occurred_at >= $1 AND occurred_at < $2 AND event_id > $3 ORDER BY event_id LIMIT 1001").bind(start).bind(end).bind(w.after.unwrap_or(Uuid::nil())).fetch_all(&s.pool).await?;
    let mut out = String::new();
    for r in rows.iter().take(1000) {
        out.push_str(
            &serde_json::to_string(&r.get::<Value, _>("payload"))
                .map_err(|_| ApiError::unavailable())?,
        );
        out.push('\n');
    }
    let mut response = (
        [
            ("content-type", "application/x-ndjson"),
            ("cache-control", "no-store"),
        ],
        out,
    )
        .into_response();
    if rows.len() > 1000 {
        response.headers_mut().insert(
            "x-next-cursor",
            HeaderValue::from_str(&rows[999].get::<Uuid, _>("event_id").to_string()).expect("uuid"),
        );
    }
    Ok(response)
}
pub async fn retain(pool: &PgPool, days: i32) -> std::result::Result<u64, sqlx::Error> {
    let deadline = Instant::now() + Duration::from_secs(15);
    let mut total = 0;
    for statement in [
        "DELETE FROM deletion_tombstones WHERE ctid IN (SELECT ctid FROM deletion_tombstones WHERE deleted_at<now()-interval '30 days' AND $1>0 LIMIT 5000)",
        "DELETE FROM rewarded_nonces WHERE ctid IN (SELECT ctid FROM rewarded_nonces WHERE created_at<now()-interval '30 days' AND $1>0 LIMIT 5000)",
        "DELETE FROM unlimited_windows WHERE ctid IN (SELECT ctid FROM unlimited_windows WHERE ends_at<now()-interval '30 days' AND $1>0 LIMIT 5000)",
        "DELETE FROM events WHERE event_id IN (SELECT event_id FROM events WHERE received_at < now()-make_interval(days => $1) LIMIT 5000)",
        "DELETE FROM event_quotas WHERE ctid IN (SELECT ctid FROM event_quotas WHERE day<CURRENT_DATE-LEAST($1,2) LIMIT 5000)",
        "DELETE FROM players WHERE id IN (SELECT p.id FROM players p WHERE firebase_uid IS NULL AND created_at<now()-make_interval(days => $1) AND NOT EXISTS(SELECT 1 FROM events WHERE player_id=p.id) AND NOT EXISTS(SELECT 1 FROM saves WHERE player_id=p.id) AND NOT EXISTS(SELECT 1 FROM wallets WHERE player_id=p.id) LIMIT 5000)",
    ] {
        loop {
            if Instant::now() >= deadline {
                return Ok(total);
            }
            let mut tx = pool.begin().await?;
            sqlx::query("SET LOCAL statement_timeout='3s'")
                .execute(&mut *tx)
                .await?;
            let n = sqlx::query(statement)
                .bind(days.clamp(1, 395))
                .execute(&mut *tx)
                .await?
                .rows_affected();
            tx.commit().await?;
            total += n;
            if n == 0 {
                break;
            }
        }
    }
    Ok(total)
}

#[cfg(test)]
mod account_tests {
    use super::*;
    use axum::body::Body;
    use http_body_util::BodyExt;
    use tower::ServiceExt;
    async fn call(
        app: Router,
        method: &str,
        path: &str,
        body: Value,
        token: &str,
        install: &str,
    ) -> (StatusCode, Value) {
        let res = app
            .oneshot(
                axum::http::Request::builder()
                    .method(method)
                    .uri(path)
                    .header("content-type", "application/json")
                    .header("authorization", format!("Bearer {token}"))
                    .header("x-install-token", install)
                    .body(Body::from(body.to_string()))
                    .unwrap(),
            )
            .await
            .unwrap();
        let status = res.status();
        let bytes = res.into_body().collect().await.unwrap().to_bytes();
        (
            status,
            serde_json::from_slice(&bytes).unwrap_or(Value::Null),
        )
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn tombstone_never_skips_a_relinked_identity(pool: PgPool) {
        let mut state = new_state(pool.clone(), "admin", false);
        Arc::get_mut(&mut state).unwrap().firebase = auth::fixture();
        let app = router(state);
        let token = auth::test_token("repeat-delete", json!({}));
        for _ in 0..2 {
            let id = Uuid::new_v4();
            sqlx::query("INSERT INTO players(id,firebase_uid) VALUES($1,'repeat-delete')")
                .bind(id)
                .execute(&pool)
                .await
                .unwrap();
            let (status, body) =
                call(app.clone(), "DELETE", "/v1/account", json!({}), &token, "").await;
            assert_eq!(status, 200);
            assert_eq!(body["deleted"], true);
            let exists: bool =
                sqlx::query_scalar("SELECT EXISTS(SELECT 1 FROM players WHERE id=$1)")
                    .bind(id)
                    .fetch_one(&pool)
                    .await
                    .unwrap();
            assert!(!exists);
        }
    }
    #[sqlx::test(migrations = "./migrations")]
    async fn saves_link_merge_conflict_and_delete(pool: PgPool) {
        let mut state = new_state(pool.clone(), "admin", false);
        Arc::get_mut(&mut state).unwrap().firebase = auth::fixture();
        let app = router(state);
        let id = Uuid::new_v4();
        let install = Uuid::new_v4();
        let secret = "guest-secret";
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(install).bind(id).bind(hex(&digest(secret))).execute(&pool).await.unwrap();
        let token = auth::test_token("test-uid", json!({}));
        assert_eq!(
            call(
                app.clone(),
                "POST",
                "/v1/link",
                json!({"player_id":id}),
                &token,
                "wrong"
            )
            .await
            .0,
            401
        );
        assert_eq!(
            call(
                app.clone(),
                "POST",
                "/v1/link",
                json!({"player_id":id}),
                &token,
                secret
            )
            .await
            .0,
            200
        );
        let data = SaveData {
            daily: Some(
                json!({"Version":1,"WinStreak":2,"PuzzleStreak":999,"Freeze":true,"Completed":["2026-11-15"],"PendingPuzzleGrants":["2026-11-15"]}),
            ),
            inventory: std::collections::BTreeMap::from([("hint".into(), 1000000)]),
            workshop: std::collections::BTreeMap::from([("gold".into(), 1000000)]),
            lives: 100,
            highest_level: 9,
            stars: std::collections::BTreeMap::from([(1, 3)]),
            ..Default::default()
        };
        let first = json!({"revision":0,"data":data});
        let (status, r) = call(
            app.clone(),
            "PUT",
            "/v1/save",
            first.clone(),
            &token,
            secret,
        )
        .await;
        assert_eq!(status, 200);
        assert_eq!(r["revision"], 1);
        assert_eq!(r["data"]["daily"]["PuzzleStreak"], 0);
        assert_eq!(r["data"]["daily"]["Freeze"], false);
        assert_eq!(r["data"]["daily"]["WinStreak"], 2);
        assert_eq!(r["data"]["daily"]["Completed"], json!(["2026-11-15"]));
        let mut stale = data.clone();
        stale.daily = Some(json!({"Version":1,"WinStreak":3}));
        let (status, conflict) = call(
            app.clone(),
            "PUT",
            "/v1/save",
            json!({"revision":0,"data":stale}),
            &token,
            secret,
        )
        .await;
        assert_eq!(status, 409);
        assert_eq!(conflict["data"]["daily"]["WinStreak"], 2);
        assert!(r["data"].get("inventory").is_none());
        assert_eq!(r["data"]["workshop"], json!({}));
        assert!(r["data"].get("lives").is_none());
        assert_eq!(
            call(app.clone(), "PUT", "/v1/save", first, &token, secret)
                .await
                .0,
            409
        );
        let (status, r) = call(
            app.clone(),
            "PUT",
            "/v1/save",
            json!({"revision":1,"data":SaveData::default()}),
            &token,
            secret,
        )
        .await;
        assert_eq!(status, 200);
        assert_eq!(r["data"]["highest_level"], 9);
        assert_eq!(r["data"]["stars"]["1"], 3);
        let (a, b) = tokio::join!(
            call(
                app.clone(),
                "PUT",
                "/v1/save",
                json!({"revision":2,"data":SaveData::default()}),
                &token,
                secret
            ),
            call(
                app.clone(),
                "PUT",
                "/v1/save",
                json!({"revision":2,"data":SaveData::default()}),
                &token,
                secret
            )
        );
        assert!((a.0 == 200 && b.0 == 409) || (b.0 == 200 && a.0 == 409));
        let res = app
            .clone()
            .oneshot(
                axum::http::Request::builder()
                    .uri("/v1/account/export")
                    .header("authorization", format!("Bearer {token}"))
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(res.status(), 200);
        let bytes = res.into_body().collect().await.unwrap().to_bytes();
        let export = String::from_utf8(bytes.to_vec()).unwrap();
        assert!(export.contains("test-uid"));
        assert!(export.ends_with("{\"table\":\"_end\",\"ok\":true}\n"));
        assert!(!export.contains("credential_hash"));
        let guest_id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(guest_id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(guest_id).bind(hex(&digest("second-guest"))).execute(&pool).await.unwrap();
        let guest_save = SaveData {
            highest_level: 20,
            inventory: std::collections::BTreeMap::from([("hint".into(), 99)]),
            ..Default::default()
        };
        sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,1,$2)")
            .bind(guest_id)
            .bind(serde_json::to_value(guest_save).unwrap())
            .execute(&pool)
            .await
            .unwrap();
        let other = auth::test_token("other-uid", json!({}));
        assert_eq!(
            call(
                app.clone(),
                "POST",
                "/v1/link",
                json!({"player_id":id}),
                &other,
                secret
            )
            .await
            .0,
            401
        );
        let (status, merged) = call(
            app.clone(),
            "POST",
            "/v1/link",
            json!({"player_id":guest_id}),
            &token,
            "second-guest",
        )
        .await;
        assert_eq!(status, 200);
        assert_eq!(merged["player_id"], id.to_string());
        let (status, saved) =
            call(app.clone(), "GET", "/v1/save", Value::Null, &token, secret).await;
        assert_eq!(status, 200);
        assert_eq!(saved["data"]["highest_level"], 20);
        assert!(saved["data"].get("inventory").is_none());
        let anon_id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id,firebase_uid) VALUES($1,'prior-anonymous')")
            .bind(anon_id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(anon_id).bind(hex(&digest("previous-install"))).execute(&pool).await.unwrap();
        let previous = auth::test_token("prior-anonymous", json!({}));
        let res = app
            .clone()
            .oneshot(
                axum::http::Request::builder()
                    .method("POST")
                    .uri("/v1/link")
                    .header("content-type", "application/json")
                    .header("authorization", format!("Bearer {token}"))
                    .header("x-install-token", "previous-install")
                    .header("x-previous-firebase-token", previous)
                    .body(Body::from(json!({"player_id":anon_id}).to_string()))
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(res.status(), 200);
        let owners: i64 = sqlx::query_scalar("SELECT count(*) FROM players")
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(owners, 1);

        assert_eq!(
            call(
                app.clone(),
                "DELETE",
                "/v1/account",
                Value::Null,
                &token,
                secret
            )
            .await
            .0,
            200
        );
        assert_eq!(
            call(
                app.clone(),
                "DELETE",
                "/v1/account",
                Value::Null,
                &token,
                secret
            )
            .await
            .0,
            200
        );
        let n: i64 = sqlx::query_scalar("SELECT count(*) FROM installs")
            .fetch_one(&pool)
            .await
            .unwrap();
        assert_eq!(n, 0);
        assert_eq!(
            call(app, "GET", "/v1/save", Value::Null, &token, secret)
                .await
                .0,
            404
        );
    }
}

#[cfg(test)]
mod abuse_tests {
    use super::*;
    use axum::body::Body;
    use http_body_util::BodyExt;
    use tower::ServiceExt;
    #[sqlx::test(migrations = "./migrations")]
    async fn quotas_guests_exports_readiness_and_retention(pool: PgPool) {
        let s = new_state(pool.clone(), "admin", true);
        assert!(!s.trust_country);
        let mut admin_headers = HeaderMap::new();
        admin_headers.insert("x-admin-token", "admin".parse().unwrap());
        assert!(admin(&s, &admin_headers).is_ok());
        let id = Uuid::new_v4();
        sqlx::query("INSERT INTO players(id,created_at) VALUES($1,now()-interval '500 days')")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest("guest"))).execute(&pool).await.unwrap();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,received_at,payload) SELECT gen_random_uuid(),$1,'test',now()-interval '500 days',now()-interval '500 days','{}' FROM generate_series(1,5100)").bind(id).execute(&pool).await.unwrap();
        sqlx::query("INSERT INTO event_quotas VALUES($1,CURRENT_DATE,10000)")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        let mut h = HeaderMap::new();
        h.insert("x-install-token", "guest".parse().unwrap());
        let batch = Batch {
            player_id: id,
            events: vec![Event {
                dev: false,
                event_id: Uuid::new_v4(),
                kind: "custom".into(),
                occurred_at: Utc::now(),
                data: json!({"metric_x":1}),
            }],
        };
        assert_eq!(
            events(State(s.clone()), h.clone(), Json(batch))
                .await
                .unwrap_err()
                .0,
            429
        );
        s.exporting.lock().unwrap().insert(id);
        assert_eq!(
            account_export(
                State(s.clone()),
                h.clone(),
                Query(AccountQuery {
                    player_id: Some(id)
                })
            )
            .await
            .unwrap_err()
            .0,
            429
        );
        s.exporting.lock().unwrap().remove(&id);
        let permit = s.exports.clone().acquire_many_owned(2).await.unwrap();
        assert_eq!(
            account_export(
                State(s.clone()),
                h.clone(),
                Query(AccountQuery {
                    player_id: Some(id)
                })
            )
            .await
            .unwrap_err()
            .0,
            429
        );
        drop(permit);
        let export = account_export(
            State(s.clone()),
            h.clone(),
            Query(AccountQuery {
                player_id: Some(id),
            }),
        )
        .await
        .unwrap();
        // Export holds its admission slot through delivery, but no DB connection.
        assert_eq!(s.exports.available_permits(), 1);
        assert!(
            export
                .into_body()
                .collect()
                .await
                .unwrap()
                .to_bytes()
                .ends_with(b"{\"table\":\"_end\",\"ok\":true}\n")
        );
        assert!(ready(State(s.clone())).await.is_ok());
        assert!(s.readiness.lock().await.0 > Instant::now());
        assert_eq!(s.exports.available_permits(), 2);
        assert!(retain(&pool, 395).await.unwrap() >= 5100);
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM players")
                .fetch_one(&pool)
                .await
                .unwrap(),
            0
        );
        sqlx::query("INSERT INTO players(id) VALUES($1)")
            .bind(id)
            .execute(&pool)
            .await
            .unwrap();
        sqlx::query("INSERT INTO installs(install_id,player_id,credential_hash,metadata,first_open_ts) VALUES($1,$2,$3,'{}',now())").bind(Uuid::new_v4()).bind(id).bind(hex(&digest("guest"))).execute(&pool).await.unwrap();
        assert_eq!(
            account_delete(
                State(s.clone()),
                h,
                Query(AccountQuery {
                    player_id: Some(id)
                })
            )
            .await
            .unwrap()
            .status(),
            200
        );
        assert_eq!(
            sqlx::query_scalar::<_, i64>("SELECT count(*) FROM installs")
                .fetch_one(&pool)
                .await
                .unwrap(),
            0
        );
        // Strict installation quota runs before JSON/DB handling; XFF cannot reset it.
        let app = router(s);
        for i in 0..7 {
            let r = app
                .clone()
                .oneshot(
                    axum::http::Request::builder()
                        .method("POST")
                        .uri("/v1/install")
                        .header("x-forwarded-for", format!("192.0.2.{i}"))
                        .header("content-type", "application/json")
                        .body(Body::from("{}"))
                        .unwrap(),
                )
                .await
                .unwrap();
            if i >= 5 {
                assert_eq!(r.status(), 429);
                assert!(r.headers().contains_key("retry-after"));
            }
        }
    }
}

// Ignore newer, already-applied versions when starting an older compatible binary.
// Known migration checksums are still validated.
pub fn migrator() -> sqlx::migrate::Migrator {
    let mut migrator = sqlx::migrate!();
    migrator.set_ignore_missing(true);
    migrator
}
