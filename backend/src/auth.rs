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
}
pub struct Firebase {
    client: reqwest::Client,
    cache: Mutex<Cache>,
}
impl Default for Firebase {
    fn default() -> Self {
        Self {
            client: reqwest::Client::builder()
                .timeout(Duration::from_secs(10))
                .build()
                .expect("TLS client"),
            cache: Mutex::new(Cache {
                keys: HashMap::new(),
                expires: Instant::now(),
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
        let mut cache = self.cache.lock().await;
        if cache.expires <= Instant::now() {
            let response = self.client.get("https://www.googleapis.com/service_accounts/v1/jwk/securetoken@system.gserviceaccount.com").send().await.map_err(|_| ApiError::unavailable())?.error_for_status().map_err(|_| ApiError::unavailable())?;
            let ttl = cache_seconds(
                response
                    .headers()
                    .get("cache-control")
                    .and_then(|v| v.to_str().ok())
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
            cache.keys = keys;
            cache.expires = Instant::now() + Duration::from_secs(ttl);
        }
        let key = cache.keys.get(&kid).ok_or(ApiError::unauthorized())?;
        let mut v = Validation::new(Algorithm::RS256);
        v.set_audience(&["orcub-f0091"]);
        v.set_issuer(&["https://securetoken.google.com/orcub-f0091"]);
        v.leeway = 0;
        v.set_required_spec_claims(&["exp", "aud", "iss", "sub", "iat", "auth_time"]);
        let c = decode::<Claims>(token, key, &v)
            .map_err(|_| ApiError::unauthorized())?
            .claims;
        let now = chrono::Utc::now().timestamp();
        if c.sub.is_empty() || c.sub.len() > 128 || c.iat > now || c.auth_time > now {
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
        client: reqwest::Client::new(),
        cache: Mutex::new(Cache {
            keys: HashMap::from([("test".into(), key)]),
            expires: Instant::now() + Duration::from_secs(3600),
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
