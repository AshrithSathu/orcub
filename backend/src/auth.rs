use crate::{ApiError, Result};
use axum::http::HeaderMap;
use jsonwebtoken::{Algorithm, DecodingKey, Validation, decode, decode_header};
use serde::Deserialize;
use std::{
    collections::HashMap,
    time::{Duration, Instant},
};
use tokio::sync::Mutex;

#[derive(Deserialize)]
struct Key {
    kid: String,
    n: String,
    e: String,
    kty: String,
    alg: String,
}
#[derive(Deserialize)]
struct Keys {
    keys: Vec<Key>,
}
#[derive(Deserialize)]
struct Claims {
    sub: String,
    iat: i64,
    auth_time: i64,
    firebase: Option<Provider>,
}
#[derive(Deserialize)]
struct Provider {
    sign_in_provider: String,
}
struct Cache {
    keys: HashMap<String, DecodingKey>,
    expires: Instant,
    retry: Instant,
    failures: u32,
}
pub struct Firebase {
    #[cfg(test)]
    pub(crate) test_permanent: std::sync::Mutex<std::collections::HashSet<String>>,
    client: reqwest::Client,
    endpoint: String,
    cache: Mutex<Cache>,
    refresh: Mutex<()>,
    verifying: tokio::sync::Semaphore,
}
impl Default for Firebase {
    fn default() -> Self {
        Self {
            #[cfg(test)]
            test_permanent: Default::default(),
            endpoint: "https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com".into(),
            client: reqwest::Client::builder()
                .timeout(Duration::from_secs(10))
                .build()
                .expect("TLS client"),
            refresh: Mutex::new(()),
            verifying: tokio::sync::Semaphore::new(32),
            cache: Mutex::new(Cache {
                keys: HashMap::new(),
                expires: Instant::now(),
                retry: Instant::now(),
                failures: 0,
            }),
        }
    }
}
pub fn bearer(h: &HeaderMap) -> Result<&str> {
    h.get("authorization")
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
        .filter(|v| !v.is_empty() && v.len() <= 8192)
        .ok_or(ApiError::unauthorized())
}
impl Firebase {
    async fn fetch_keys(&self) -> Result<(HashMap<String, DecodingKey>, u64)> {
        let response = self
            .client
            .get(&self.endpoint)
            .send()
            .await
            .map_err(|_| ApiError::unavailable())?
            .error_for_status()
            .map_err(|_| ApiError::unavailable())?;
        let ttl = cache_seconds(
            response
                .headers()
                .get("cache-control")
                .and_then(|h| h.to_str().ok())
                .unwrap_or(""),
        );
        let body: Keys = response.json().await.map_err(|_| ApiError::unavailable())?;
        let mut keys = HashMap::new();
        for k in body.keys {
            if k.kty == "RSA" && k.alg == "RS256" {
                keys.insert(
                    k.kid,
                    DecodingKey::from_rsa_components(&k.n, &k.e)
                        .map_err(|_| ApiError::unavailable())?,
                );
            }
        }
        if keys.is_empty() {
            return Err(ApiError::unavailable());
        }
        Ok((keys, ttl))
    }
    pub async fn current_anonymous(&self, uid: &str) -> Result<bool> {
        #[cfg(test)]
        if self.endpoint == "http://127.0.0.1:1" {
            return Ok(!self.test_permanent.lock().unwrap().contains(uid));
        }
        crate::firebase_admin::anonymous(uid).await
    }
    pub async fn verify(&self, h: &HeaderMap) -> Result<String> {
        self.verify_kind(h, false).await
    }
    pub async fn verify_anonymous(&self, h: &HeaderMap) -> Result<String> {
        self.verify_kind(h, true).await
    }
    async fn verify_kind(&self, h: &HeaderMap, anonymous: bool) -> Result<String> {
        let token = bearer(h)?;
        let header = decode_header(token).map_err(|_| ApiError::unauthorized())?;
        if header.alg != Algorithm::RS256 {
            return Err(ApiError::unauthorized());
        }
        let kid = header.kid.ok_or(ApiError::unauthorized())?;
        let _permit = self
            .verifying
            .try_acquire()
            .map_err(|_| ApiError::unavailable())?;
        let (expired, missing, empty, retry) = {
            let c = self.cache.lock().await;
            (
                c.expires <= Instant::now(),
                !c.keys.contains_key(&kid),
                c.keys.is_empty(),
                c.retry <= Instant::now(),
            )
        };
        if (expired || missing) && retry {
            // Single flight; requests with stale keys never queue behind network I/O.
            if let Ok(_flight) = self.refresh.try_lock() {
                let should_refresh = self.cache.lock().await.retry <= Instant::now();
                if should_refresh {
                    let result = self.fetch_keys().await;
                    let mut c = self.cache.lock().await;
                    match result {
                        Ok((keys, ttl)) => {
                            c.keys = keys;
                            c.expires = Instant::now() + Duration::from_secs(ttl);
                            c.retry = Instant::now() + Duration::from_secs(60);
                            c.failures = 0;
                        }
                        Err(_) => {
                            c.failures = (c.failures + 1).min(5);
                            c.retry =
                                Instant::now() + Duration::from_secs(30_u64 << (c.failures - 1));
                        }
                    }
                }
            } else if empty {
                return Err(ApiError::unavailable());
            }
        }
        let key = {
            let c = self.cache.lock().await;
            if c.keys.is_empty() {
                return Err(ApiError::unavailable());
            }
            c.keys.get(&kid).cloned().ok_or(ApiError::unauthorized())?
        };
        let mut v = Validation::new(Algorithm::RS256);
        v.set_audience(&["orcub-f0091"]);
        v.set_issuer(&["https://securetoken.google.com/orcub-f0091"]);
        v.leeway = 0;
        v.set_required_spec_claims(&["exp", "aud", "iss", "sub", "iat", "auth_time"]);
        let c = decode::<Claims>(token, &key, &v)
            .map_err(|_| ApiError::unauthorized())?
            .claims;
        let now = chrono::Utc::now().timestamp();
        if c.sub.is_empty() || c.sub.len() > 128 || c.iat > now + 60 || c.auth_time > now + 60 {
            return Err(ApiError::unauthorized());
        }
        if anonymous
            && !c
                .firebase
                .is_some_and(|f| f.sign_in_provider == "anonymous")
        {
            return Err(ApiError::unauthorized());
        }
        Ok(c.sub)
    }
}
pub fn cache_seconds(value: &str) -> u64 {
    value
        .split(',')
        .find_map(|p| {
            p.trim()
                .strip_prefix("max-age=")?
                .trim_matches('"')
                .parse()
                .ok()
        })
        .unwrap_or(300)
        .min(86400)
}
#[cfg(test)]
mod tests {
    #[test]
    fn ttl() {
        assert_eq!(super::cache_seconds("public, max-age=123"), 123);
        assert_eq!(super::cache_seconds("max-age=0"), 0);
    }
}

#[cfg(test)]
pub(crate) fn fixture() -> Firebase {
    let key =
        DecodingKey::from_rsa_pem(include_bytes!("../tests/fixtures/test-public.pem")).unwrap();
    Firebase {
        test_permanent: Default::default(),
        endpoint: "http://127.0.0.1:1".into(),
        client: reqwest::Client::new(),
        refresh: Mutex::new(()),
        verifying: tokio::sync::Semaphore::new(32),
        cache: Mutex::new(Cache {
            keys: HashMap::from([("test".into(), key)]),
            expires: Instant::now() + Duration::from_secs(3600),
            retry: Instant::now() + Duration::from_secs(3600),
            failures: 0,
        }),
    }
}
#[cfg(test)]
pub(crate) fn test_token(uid: &str, overrides: serde_json::Value) -> String {
    let now = chrono::Utc::now().timestamp();
    let mut claims = serde_json::json!({"sub":uid,"iat":now,"auth_time":now,"exp":now+3600,"aud":"orcub-f0091","iss":"https://securetoken.google.com/orcub-f0091","firebase":{"sign_in_provider":"anonymous"}});
    for (k, v) in overrides.as_object().unwrap() {
        claims[k] = v.clone();
    }
    let mut header = jsonwebtoken::Header::new(Algorithm::RS256);
    header.kid = Some("test".into());
    jsonwebtoken::encode(
        &header,
        &claims,
        &jsonwebtoken::EncodingKey::from_rsa_pem(include_bytes!(
            "../tests/fixtures/test-private.pem"
        ))
        .unwrap(),
    )
    .unwrap()
}
#[cfg(test)]
mod verification_tests {
    use super::*;
    #[tokio::test]
    async fn concurrent_refresh_is_single_flight() {
        let count = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let calls = count.clone();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let endpoint = format!("http://{}", listener.local_addr().unwrap());
        let server = tokio::spawn(async move {
            axum::serve(
                listener,
                axum::Router::new().route(
                    "/",
                    axum::routing::get(move || {
                        let calls = calls.clone();
                        async move {
                            calls.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                            tokio::time::sleep(Duration::from_millis(50)).await;
                            axum::http::StatusCode::SERVICE_UNAVAILABLE
                        }
                    }),
                ),
            )
            .await
            .unwrap();
        });
        let mut verifier = fixture();
        verifier.endpoint = endpoint;
        {
            let mut c = verifier.cache.lock().await;
            c.expires = Instant::now();
            c.retry = Instant::now();
        }
        let verifier = std::sync::Arc::new(verifier);
        let mut jobs = tokio::task::JoinSet::new();
        for _ in 0..16 {
            let v = verifier.clone();
            jobs.spawn(async move {
                let mut h = HeaderMap::new();
                h.insert(
                    "authorization",
                    format!("Bearer {}", test_token("uid", serde_json::json!({})))
                        .parse()
                        .unwrap(),
                );
                v.verify(&h).await.unwrap()
            });
        }
        while let Some(r) = jobs.join_next().await {
            assert_eq!(r.unwrap(), "uid");
        }
        assert_eq!(count.load(std::sync::atomic::Ordering::SeqCst), 1);
        server.abort();
    }
    #[tokio::test]
    async fn failed_refresh_preserves_keys_backoff_and_negative_cache() {
        let verifier = fixture();
        {
            let mut c = verifier.cache.lock().await;
            c.expires = Instant::now();
            c.retry = Instant::now();
        }
        let mut h = HeaderMap::new();
        h.insert("authorization",format!("Bearer {}",test_token("uid",serde_json::json!({"iat":chrono::Utc::now().timestamp()+30,"auth_time":chrono::Utc::now().timestamp()+30}))).parse().unwrap());
        assert_eq!(verifier.verify(&h).await.unwrap(), "uid");
        {
            let c = verifier.cache.lock().await;
            assert_eq!(c.failures, 1);
            assert!(c.retry > Instant::now());
            assert!(c.keys.contains_key("test"));
        }
        assert!(verifier.verify(&h).await.is_ok());
        assert_eq!(verifier.cache.lock().await.failures, 1);
        let flight = verifier.refresh.lock().await;
        verifier.cache.lock().await.retry = Instant::now();
        assert!(verifier.verify(&h).await.is_ok());
        drop(flight);
        let permit = verifier.verifying.acquire_many(32).await.unwrap();
        assert_eq!(verifier.verify(&h).await.unwrap_err().0, 503);
        drop(permit);
    }
    #[tokio::test]
    async fn rejects_wrong_claims_and_signature() {
        let verifier = fixture();
        for changes in [
            serde_json::json!({"aud":"wrong"}),
            serde_json::json!({"iss":"wrong"}),
            serde_json::json!({"exp":1}),
            serde_json::json!({"sub":""}),
            serde_json::json!({"iat":chrono::Utc::now().timestamp()+3600}),
            serde_json::json!({"auth_time":chrono::Utc::now().timestamp()+3600}),
        ] {
            let mut h = HeaderMap::new();
            h.insert(
                "authorization",
                format!("Bearer {}", test_token("uid", changes))
                    .parse()
                    .unwrap(),
            );
            assert!(verifier.verify(&h).await.is_err());
        }
        let mut h = HeaderMap::new();
        h.insert(
            "authorization",
            format!("Bearer {}", test_token("uid", serde_json::json!({})))
                .parse()
                .unwrap(),
        );
        assert_eq!(verifier.verify(&h).await.unwrap(), "uid");
        let mut token = test_token("uid", serde_json::json!({}));
        let pos = token.rfind('.').unwrap() + 5;
        token.replace_range(pos..pos + 1, "!");
        h.insert("authorization", format!("Bearer {token}").parse().unwrap());
        assert!(verifier.verify(&h).await.is_err());
    }
}
