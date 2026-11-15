use axum::{
    body::Body,
    http::{Request, StatusCode},
};
use http_body_util::BodyExt;
use orcub_backend::{new_state, router};
use serde_json::{Value, json};
use sqlx::PgPool;
use tower::ServiceExt;
use uuid::Uuid;
async fn call(
    app: axum::Router,
    method: &str,
    path: &str,
    data: Value,
    token: Option<&str>,
) -> (StatusCode, Value) {
    let mut req = Request::builder()
        .method(method)
        .uri(path)
        .header("content-type", "application/json");
    if let Some(t) = token {
        req = req.header("x-install-token", t);
    }
    let res = app
        .oneshot(req.body(Body::from(data.to_string())).unwrap())
        .await
        .unwrap();
    let status = res.status();
    let bytes = res.into_body().collect().await.unwrap().to_bytes();
    let body = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
    (status, body)
}
#[sqlx::test(migrations = "./migrations")]
async fn install_events_config_admin_and_retention(pool: PgPool) {
    let app = router(new_state(
        pool.clone(),
        "01234567890123456789012345678901",
        false,
    ));
    let install = json!({"install_id":Uuid::new_v4(),"app_version":"1.0","build":"1","platform":"ios","os_version":"18.0","device_model":"iPhone17.1","device_ram_mb":8192,"screen":{"w":390,"h":844,"dpi":460},"locale":"en-IN","language":"en","timezone":"Asia/Kolkata","first_open_ts":chrono::Utc::now()});
    let (status, response) = call(app.clone(), "POST", "/v1/install", install.clone(), None).await;
    assert_eq!(status, StatusCode::OK);
    let token = response["install_token"].as_str().unwrap();
    let player = response["player_id"].as_str().unwrap();
    assert_eq!(
        call(app.clone(), "POST", "/v1/install", install.clone(), None)
            .await
            .0,
        StatusCode::UNAUTHORIZED
    );
    assert_eq!(
        call(app.clone(), "POST", "/v1/install", install, Some(token))
            .await
            .1["player_id"],
        player
    );
    let (status, mixed) = call(app.clone(), "POST", "/v1/events", json!({"player_id":player,"events":[
        {"event_id":Uuid::new_v4(),"kind":"level_end","occurred_at":chrono::Utc::now(),"data":{"LevelIndex":"bad","Result":{"Outcome":0}}},
        {"event_id":Uuid::new_v4(),"kind":"level_end","occurred_at":chrono::Utc::now(),"data":{"LevelIndex":1,"Result":{"Outcome":0},"TuningVersion":"November (b)"}}
    ]}), Some(token)).await;
    assert_eq!(status, StatusCode::OK);
    assert_eq!(mixed["accepted"], 1);
    assert_eq!(mixed["duplicates"], 0);
    assert_eq!(mixed["rejected"].as_array().unwrap().len(), 1);
    let path = format!("/v1/config?player_id={player}");
    assert_eq!(
        call(app.clone(), "GET", &path, Value::Null, None).await.0,
        StatusCode::UNAUTHORIZED
    );
    let response = app
        .clone()
        .oneshot(
            Request::builder()
                .uri(&path)
                .header("x-install-token", token)
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    let etag = response.headers()["etag"].clone();
    let response = app
        .clone()
        .oneshot(
            Request::builder()
                .uri(&path)
                .header("x-install-token", token)
                .header("if-none-match", etag)
                .body(Body::empty())
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), 304);
    let event_id = Uuid::new_v4();
    let batch = json!({"player_id":player,"events":[{"event_id":event_id,"kind":"session_start","occurred_at":chrono::Utc::now(),"data":{"session_id":Uuid::new_v4()}}]});
    assert_eq!(
        call(
            app.clone(),
            "POST",
            "/v1/events",
            batch.clone(),
            Some(token)
        )
        .await
        .1["accepted"],
        1
    );
    assert_eq!(
        call(
            app.clone(),
            "POST",
            "/v1/events",
            batch.clone(),
            Some(token)
        )
        .await
        .1["duplicates"],
        1
    );
    let mut gzip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    use std::io::Write;
    gzip.write_all(batch.to_string().as_bytes()).unwrap();
    let res = app
        .clone()
        .oneshot(
            Request::builder()
                .method("POST")
                .uri("/v1/events")
                .header("content-type", "application/json")
                .header("content-encoding", "gzip")
                .header("x-install-token", token)
                .body(Body::from(gzip.finish().unwrap()))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(res.status(), 200);
    let mut bomb = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    bomb.write_all(&vec![b' '; 3 * 1024 * 1024]).unwrap();
    bomb.write_all(batch.to_string().as_bytes()).unwrap();
    let res = app
        .clone()
        .oneshot(
            Request::builder()
                .method("POST")
                .uri("/v1/events")
                .header("content-type", "application/json")
                .header("content-encoding", "gzip")
                .header("x-install-token", token)
                .body(Body::from(bomb.finish().unwrap()))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(res.status(), 413);
    let mut invalid = batch.clone();
    invalid["events"][0]["data"] = json!({"email":"test@example.com"});
    let (status, result) = call(app.clone(), "POST", "/v1/events", invalid, Some(token)).await;
    assert_eq!(status, 200);
    assert_eq!(result["accepted"], 0);
    assert_eq!(result["rejected"].as_array().unwrap().len(), 1);
    let mut unknown = batch;
    unknown["events"][0]["event_id"] = json!(Uuid::new_v4());
    unknown["events"][0]["kind"] = json!("future");
    unknown["events"][0]["data"] = json!({"metric_count":5});
    assert_eq!(
        call(app.clone(), "POST", "/v1/events", unknown, Some(token))
            .await
            .0,
        200
    );
    let mut attempt: Value = serde_json::from_str(include_str!("fixtures/attempt.json")).unwrap();
    attempt["StartedUtc"] = json!(chrono::Utc::now());
    let level_batch = json!({"player_id":player,"events":[
        {"event_id":Uuid::new_v4(),"kind":"level_start","occurred_at":chrono::Utc::now(),"data":{"LevelIndex":3,"Variant":0,"Plan":attempt["Plan"]}},
        {"event_id":Uuid::new_v4(),"kind":"level_end","occurred_at":chrono::Utc::now(),"data":attempt},
        {"event_id":Uuid::new_v4(),"kind":"ad_paid","occurred_at":chrono::Utc::now(),"data":{"placement":"rewarded_continue","network":"admob","value_micros":1200,"currency":"USD","precision":"estimated"}}
    ]});
    let (status, result) = call(app.clone(), "POST", "/v1/events", level_batch, Some(token)).await;
    assert_eq!(status, 200, "{result}");
    let oversized = json!({"player_id":player,"events":(0..501).map(|_|json!({"event_id":Uuid::new_v4(),"kind":"session_start","occurred_at":chrono::Utc::now(),"data":{"session_id":Uuid::new_v4()}})).collect::<Vec<_>>()});
    assert_eq!(
        call(app.clone(), "POST", "/v1/events", oversized, Some(token))
            .await
            .0,
        400
    );
    for path in [
        "/v1/admin/funnel",
        "/v1/admin/retention",
        "/v1/admin/ads",
        "/v1/admin/export/attempts",
    ] {
        assert_eq!(
            call(app.clone(), "GET", path, Value::Null, None).await.0,
            401
        );
        let res = app
            .clone()
            .oneshot(
                Request::builder()
                    .uri(path)
                    .header("ADMIN_TOKEN", "01234567890123456789012345678901")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(res.status(), 200, "{path}");
    }
    sqlx::query("UPDATE events SET received_at=now()-interval '401 days'")
        .execute(&pool)
        .await
        .unwrap();
    assert_eq!(orcub_backend::retain(&pool, 400).await.unwrap(), 6);
    let n: i64 = sqlx::query_scalar("SELECT count(*) FROM raw_events")
        .fetch_one(&pool)
        .await
        .unwrap();
    assert_eq!(n, 0);
    sqlx::query("DELETE FROM players")
        .execute(&pool)
        .await
        .unwrap();
    let n: i64 = sqlx::query_scalar("SELECT count(*) FROM installs")
        .fetch_one(&pool)
        .await
        .unwrap();
    assert_eq!(n, 0);
}
