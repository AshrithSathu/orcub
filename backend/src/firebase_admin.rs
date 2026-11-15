use crate::*;
#[derive(Deserialize)]
struct ServiceAccount {
    client_email: String,
    private_key: String,
    project_id: String,
}
async fn request(action: &str, body: Value) -> Result<Value> {
    let Ok(raw) = std::env::var("FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON") else {
        return Err(ApiError::unavailable());
    };
    let sa: ServiceAccount = serde_json::from_str(&raw).map_err(|_| ApiError::unavailable())?;
    if sa.project_id != "orcub-f0091" {
        return Err(ApiError::unavailable());
    }
    let now = Utc::now().timestamp();
    let jwt=jsonwebtoken::encode(&jsonwebtoken::Header::new(jsonwebtoken::Algorithm::RS256),&json!({"iss":sa.client_email,"scope":"https://www.googleapis.com/auth/identitytoolkit","aud":"https://oauth2.googleapis.com/token","iat":now,"exp":now+3600}),&jsonwebtoken::EncodingKey::from_rsa_pem(sa.private_key.as_bytes()).map_err(|_|ApiError::unavailable())?).map_err(|_|ApiError::unavailable())?;
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(10))
        .build()
        .map_err(|_| ApiError::unavailable())?;
    let token: Value = client
        .post("https://oauth2.googleapis.com/token")
        .form(&[
            ("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            ("assertion", jwt.as_str()),
        ])
        .send()
        .await
        .map_err(|_| ApiError::unavailable())?
        .error_for_status()
        .map_err(|_| ApiError::unavailable())?
        .json()
        .await
        .map_err(|_| ApiError::unavailable())?;
    let response = client
        .post(format!(
            "https://identitytoolkit.googleapis.com/v1/projects/orcub-f0091/accounts:{action}"
        ))
        .bearer_auth(
            token["access_token"]
                .as_str()
                .ok_or(ApiError::unavailable())?,
        )
        .json(&body)
        .send()
        .await
        .map_err(|_| ApiError::unavailable())?;
    if response.status().is_success() {
        return response.json().await.map_err(|_| ApiError::unavailable());
    }
    let body: Value = response.json().await.map_err(|_| ApiError::unavailable())?;
    if body["error"]["message"] == "USER_NOT_FOUND" {
        return Ok(json!({"missing":true}));
    }
    Err(ApiError::unavailable())
}
pub async fn delete(uid: &str) -> Result<bool> {
    if std::env::var("FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON").is_err() {
        return Ok(false);
    }
    request("delete", json!({"localId":uid})).await?;
    Ok(true)
}
pub async fn anonymous(uid: &str) -> Result<bool> {
    let value = request("lookup", json!({"localId":[uid]})).await?;
    Ok(value["users"]
        .as_array()
        .and_then(|u| u.first())
        .is_some_and(|u| {
            u["disabled"] != true
                && u["providerUserInfo"]
                    .as_array()
                    .is_none_or(|p| p.is_empty())
                && u["email"].is_null()
                && u["phoneNumber"].is_null()
        }))
}
