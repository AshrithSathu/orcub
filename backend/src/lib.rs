pub mod auth;
pub mod model;
use axum::{
    Json, Router,
    body::{Body, Bytes},
    extract::Request,
    extract::{DefaultBodyLimit, Query, State},
    http::{HeaderMap, HeaderValue, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{delete, get, post, put},
};
use chrono::{DateTime, Utc};
use governor::{DefaultDirectRateLimiter, Quota, RateLimiter};
use model::*;
use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use sqlx::{PgPool, Row};
use std::{num::NonZeroU32, sync::Arc, time::Duration};
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
        (self.0, Json(json!({"error":self.1}))).into_response()
    }
}
pub struct App {
    pub pool: PgPool,
    pub firebase: auth::Firebase,
    pub admin_hash: Vec<u8>,
    pub trust_country: bool,
    pub config: Value,
    pub limiter: DefaultDirectRateLimiter,
}
pub fn digest(s: &str) -> Vec<u8> {
    Sha256::digest(s.as_bytes()).to_vec()
}
pub fn credential() -> String {
    format!("{}{}", Uuid::new_v4().simple(), Uuid::new_v4().simple())
}
pub fn new_state(pool: PgPool, admin: &str, trust_country: bool) -> Arc<App> {
    Arc::new(App {
        pool,
        firebase: auth::Firebase::default(),
        admin_hash: digest(admin),
        trust_country,
        config: json!({"tuning":serde_json::from_str::<Value>(include_str!("../config/tuning.json")).expect("tuning"),"skill_model":serde_json::from_str::<Value>(include_str!("../config/skill-model.json")).expect("model"),"ad_policy":{"interstitial_min_interval_seconds":120,"interstitial_daily_cap":10,"rewarded_daily_cap":20},"version":"1"}),
        limiter: RateLimiter::direct(
            Quota::per_second(NonZeroU32::new(100).unwrap())
                .allow_burst(NonZeroU32::new(200).unwrap()),
        ),
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
    if !matches!(req.uri().path(), "/healthz" | "/readyz") && s.limiter.check().is_err() {
        return (StatusCode::TOO_MANY_REQUESTS, [("retry-after", "1")]).into_response();
    }
    let start = std::time::Instant::now();
    let method = req.method().clone();
    // Never log URLs (player ids/query strings), headers, tokens, bodies, or peer addresses.
    let response = next.run(req).await;
    tracing::info!(%method,status=response.status().as_u16(),elapsed_ms=start.elapsed().as_millis(),"request");
    response
}
async fn ready(State(s): State<Arc<App>>) -> Result<StatusCode> {
    sqlx::query("SELECT 1").execute(&s.pool).await?;
    Ok(StatusCode::OK)
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
                .or_else(|| h.get("x-country"))
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
    let mut inserted = 0;
    for (event, (data, raw)) in body.events.iter().zip(validated) {
        let n=sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING").bind(event.event_id).bind(body.player_id).bind(&event.kind).bind(event.occurred_at).bind(if raw {json!({})} else {data.clone()}).execute(&mut *tx).await?.rows_affected();
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
async fn uid_player(s: &App, h: &HeaderMap) -> Result<(String, Uuid)> {
    let uid = s.firebase.verify(h).await?;
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
        return Ok((StatusCode::CONFLICT,Json(json!({"error":"revision_conflict","revision":revision,"data":old.map(|r|r.get::<Value,_>("data"))}))).into_response());
    }
    if let Some(old) = old {
        body.data.merge_progress(
            &serde_json::from_value(old.get("data")).map_err(|_| ApiError::unavailable())?,
        );
    }
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
                json!({"revision":r.get::<i64,_>("revision"),"data":r.get::<Value,_>("data")})
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
    let uid = s.firebase.verify(&h).await?;
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
                .unwrap_or_default();
            data.merge_progress(&guest);
            let rev = canonical.map(|r| r.get::<i64, _>("revision")).unwrap_or(0) + 1;
            sqlx::query("INSERT INTO saves(player_id,revision,data) VALUES($1,$2,$3) ON CONFLICT(player_id) DO UPDATE SET revision=$2,data=$3,updated_at=now()").bind(id).bind(rev).bind(serde_json::to_value(data).map_err(|_|ApiError::unavailable())?).execute(&mut *tx).await?;
        }
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
async fn account_delete(State(s): State<Arc<App>>, h: HeaderMap) -> Result<Response> {
    let (_, id) = uid_player(&s, &h).await?;
    sqlx::query("DELETE FROM players WHERE id=$1")
        .bind(id)
        .execute(&s.pool)
        .await?;
    Ok(([("cache-control","no-store")],Json(json!({"receipt_id":Uuid::new_v4(),"deleted_at":Utc::now(),"scope":"first_party_backend","deleted":true}))).into_response())
}
async fn account_export(State(s): State<Arc<App>>, h: HeaderMap) -> Result<Response> {
    let (_, id) = uid_player(&s, &h).await?;
    // One repeatable-read snapshot; streaming bounds memory for event histories.
    let mut tx = s.pool.begin().await?;
    sqlx::query("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ, READ ONLY")
        .execute(&mut *tx)
        .await?;
    let stream = async_stream::try_stream! {
     for table in ["players","installs","experiments","saves","events"] {
      let filter=if table=="players"{"id"}else{"player_id"};
      let sql=if table=="installs"{format!("SELECT to_jsonb(t)-'credential_hash' AS data FROM {table} t WHERE {filter}=$1")}else{format!("SELECT to_jsonb(t) AS data FROM {table} t WHERE {filter}=$1")};
      let mut rows=sqlx::query(&sql).bind(id).fetch(&mut *tx);
      use futures_util::TryStreamExt;
      while let Some(row)=rows.try_next().await.map_err(std::io::Error::other)? {let data:Value=row.get("data");let line=serde_json::to_string(&json!({"table":table,"row":data})).map_err(std::io::Error::other)?+"\n";yield Bytes::from(line);}
     }
     let mut rows=sqlx::query("SELECT to_jsonb(r) AS data FROM raw_events r JOIN events e USING(event_id) WHERE e.player_id=$1").bind(id).fetch(&mut *tx);
     use futures_util::TryStreamExt;
     while let Some(row)=rows.try_next().await.map_err(std::io::Error::other)?{let line=serde_json::to_string(&json!({"table":"raw_events","row":row.get::<Value,_>("data")})).map_err(std::io::Error::other)?+"\n";yield Bytes::from(line);}
    };
    let stream: futures_util::stream::BoxStream<'static, std::io::Result<Bytes>> = Box::pin(stream);
    let body = Body::from_stream(stream);
    Ok((
        [
            ("content-type", "application/x-ndjson"),
            ("cache-control", "no-store"),
        ],
        body,
    )
        .into_response())
}
fn admin(s: &App, h: &HeaderMap) -> Result<()> {
    let supplied = digest(header(h, "admin_token")?);
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
    let rows=sqlx::query("SELECT event_id,payload FROM events WHERE kind='level_end' AND occurred_at >= $1 AND occurred_at < $2 AND event_id > $3 ORDER BY event_id LIMIT 1001").bind(start).bind(end).bind(w.after.unwrap_or(Uuid::nil())).fetch_all(&s.pool).await?;
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
    sqlx::query("DELETE FROM events WHERE received_at < now()-make_interval(days => $1)")
        .bind(days)
        .execute(pool)
        .await
        .map(|r| r.rows_affected())
}

#[cfg(test)]
mod account_tests {
    use super::*;
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
        assert_eq!(saved["data"]["inventory"], json!({}));
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
