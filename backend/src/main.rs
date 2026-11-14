use orcub_backend::{new_state, retain, router};
use sqlx::postgres::PgPoolOptions;
use std::time::Duration;
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    tracing_subscriber::fmt()
        .json()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "orcub_backend=info".into()),
        )
        .init();
    if std::env::var("APPLE_ALLOW_XCODE").as_deref() == Ok("true") {
        if std::env::var("APP_ENV").as_deref() != Ok("development")
            || std::env::var("APPLE_ENVIRONMENT").as_deref() != Ok("Sandbox")
        {
            return Err("Xcode transactions require development and Sandbox configuration".into());
        }
        tracing::warn!("UNSIGNED Xcode StoreKit testing enabled");
    }
    let url = std::env::var("DATABASE_URL")?;
    let admin = std::env::var("ADMIN_TOKEN")?;
    if admin.len() < 32 {
        return Err("ADMIN_TOKEN must be at least 32 characters".into());
    }
    let days: i32 = std::env::var("RETENTION_DAYS")
        .unwrap_or_else(|_| "395".into())
        .parse()?;
    if !(1..=395).contains(&days) {
        return Err("RETENTION_DAYS must be 1..395".into());
    }
    let pool = PgPoolOptions::new()
        .max_connections(10)
        .after_connect(|connection, _| {
            Box::pin(async move {
                sqlx::query("SET statement_timeout = '20s'")
                    .execute(connection)
                    .await?;
                Ok(())
            })
        })
        .acquire_timeout(Duration::from_secs(5))
        .connect(&url)
        .await?;
    let migrator = orcub_backend::migrator();
    let applied: Vec<i64> =
        sqlx::query_scalar("SELECT version FROM _sqlx_migrations WHERE success")
            .fetch_all(&pool)
            .await
            .unwrap_or_default();
    for version in applied {
        if !migrator.iter().any(|m| m.version == version) {
            tracing::warn!(version, "unknown applied migration");
        }
    }
    migrator.run(&pool).await?;
    if std::env::args().any(|a| a == "retention") {
        tracing::info!(deleted = retain(&pool, days).await?, "retention completed");
        return Ok(());
    }
    let state = new_state(
        pool.clone(),
        &admin,
        std::env::var("TRUST_COUNTRY_HEADERS").as_deref() == Ok("true"),
    );
    let (stop_tx, mut stop_rx) = tokio::sync::watch::channel(false);
    let job = tokio::spawn(async move {
        let mut interval = tokio::time::interval(Duration::from_secs(3600));
        loop {
            tokio::select! {_=stop_rx.changed()=>break,_=interval.tick()=>{match retain(&pool,days).await{Ok(n)=>tracing::info!(deleted=n,"retention"),Err(e)=>tracing::error!(code=?e.as_database_error().and_then(|e|e.code()),"retention failed")}}}
        }
    });
    let port = std::env::var("PORT").unwrap_or_else(|_| "8080".into());
    let listener = tokio::net::TcpListener::bind(format!("0.0.0.0:{port}")).await?;
    axum::serve(
        listener,
        router(state).into_make_service_with_connect_info::<std::net::SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown())
    .await?;
    let _ = stop_tx.send(true);
    job.await?;
    Ok(())
}
async fn shutdown() {
    #[cfg(unix)]
    {
        let mut term = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
            .expect("SIGTERM");
        tokio::select! {_=tokio::signal::ctrl_c()=>{},_=term.recv()=>{}}
    }
    #[cfg(not(unix))]
    {
        let _ = tokio::signal::ctrl_c().await;
    }
}
