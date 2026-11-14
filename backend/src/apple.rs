//! Offline StoreKit 2 verification. No receipt/JWS is retained.
use crate::{ApiError, Result};
use base64::{
    Engine,
    engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD},
};
use openssl::{
    bn::BigNum,
    ecdsa::EcdsaSig,
    hash::MessageDigest,
    sign::Verifier,
    stack::Stack,
    x509::{X509, X509StoreContext, store::X509StoreBuilder},
};
use serde_json::Value;

pub fn verify(jws: &str) -> Result<Value> {
    let root = X509::from_der(include_bytes!("../config/AppleRootCA-G3.cer"))
        .map_err(|_| ApiError::unavailable())?;
    verify_root(jws, &root, true)
}
fn verify_root(jws: &str, root: &X509, apple_oids: bool) -> Result<Value> {
    let invalid = || ApiError::bad("invalid Apple signed payload");
    if jws.len() > 65536 {
        return Err(invalid());
    }
    let parts: Vec<_> = jws.split('.').collect();
    if parts.len() != 3 {
        return Err(invalid());
    }
    let h: Value =
        serde_json::from_slice(&URL_SAFE_NO_PAD.decode(parts[0]).map_err(|_| invalid())?)
            .map_err(|_| invalid())?;
    if h["alg"] != "ES256" || h.get("crit").is_some() {
        return Err(invalid());
    }
    let chain = h["x5c"]
        .as_array()
        .filter(|v| v.len() == 3)
        .ok_or_else(invalid)?;
    let certs: Vec<X509> = chain
        .iter()
        .map(|c| {
            X509::from_der(
                &STANDARD
                    .decode(c.as_str().ok_or_else(invalid)?)
                    .map_err(|_| invalid())?,
            )
            .map_err(|_| invalid())
        })
        .collect::<Result<_>>()?;
    if apple_oids {
        for (cert, oid) in [
            (&certs[0], "1.2.840.113635.100.6.11.1"),
            (&certs[1], "1.2.840.113635.100.6.2.1"),
        ] {
            let der = cert.to_der().map_err(|_| invalid())?;
            let (_, parsed) = x509_parser::parse_x509_certificate(&der).map_err(|_| invalid())?;
            if !parsed
                .extensions()
                .iter()
                .any(|e| e.oid.to_id_string() == oid)
            {
                return Err(invalid());
            }
        }
    }
    let payload: Value =
        serde_json::from_slice(&URL_SAFE_NO_PAD.decode(parts[1]).map_err(|_| invalid())?)
            .map_err(|_| invalid())?;
    let signed_time = signed_time(&payload)?;
    let mut params = openssl::x509::verify::X509VerifyParam::new().map_err(|_| invalid())?;
    params.set_time(signed_time);
    let mut store = X509StoreBuilder::new().map_err(|_| invalid())?;
    store.set_param(&params).map_err(|_| invalid())?;
    store.add_cert(root.clone()).map_err(|_| invalid())?;
    let mut intermediates = Stack::new().map_err(|_| invalid())?;
    intermediates
        .push(certs[1].clone())
        .map_err(|_| invalid())?;
    let mut ctx = X509StoreContext::new().map_err(|_| invalid())?;
    if !ctx
        .init(&store.build(), &certs[0], &intermediates, |c| {
            c.verify_cert()
        })
        .map_err(|_| invalid())?
    {
        return Err(invalid());
    }
    let key = certs[0].public_key().map_err(|_| invalid())?;
    if key.ec_key().map_err(|_| invalid())?.group().curve_name()
        != Some(openssl::nid::Nid::X9_62_PRIME256V1)
    {
        return Err(invalid());
    }
    let sig = URL_SAFE_NO_PAD.decode(parts[2]).map_err(|_| invalid())?;
    if sig.len() != 64 {
        return Err(invalid());
    }
    let sig = EcdsaSig::from_private_components(
        BigNum::from_slice(&sig[..32]).map_err(|_| invalid())?,
        BigNum::from_slice(&sig[32..]).map_err(|_| invalid())?,
    )
    .map_err(|_| invalid())?
    .to_der()
    .map_err(|_| invalid())?;
    let mut verifier = Verifier::new(MessageDigest::sha256(), &key).map_err(|_| invalid())?;
    verifier
        .update(format!("{}.{}", parts[0], parts[1]).as_bytes())
        .map_err(|_| invalid())?;
    if !verifier.verify(&sig).map_err(|_| invalid())? {
        return Err(invalid());
    }
    serde_json::from_slice(&URL_SAFE_NO_PAD.decode(parts[1]).map_err(|_| invalid())?)
        .map_err(|_| invalid())
}
fn signed_time(payload: &Value) -> Result<i64> {
    let now = chrono::Utc::now().timestamp_millis();
    let t = payload["signedDate"]
        .as_i64()
        .ok_or(ApiError::bad("signedDate required"))?;
    if t <= 0
        || t > now + 300000
        || payload
            .get("expiresDate")
            .is_some_and(|v| v.as_i64().is_none_or(|t| t <= now))
    {
        return Err(ApiError::bad("signed payload time"));
    }
    Ok(t / 1000)
}
pub fn environment(v: &Value) -> Result<()> {
    let expected = std::env::var("APPLE_ENVIRONMENT").unwrap_or_else(|_| "Production".into());
    if !matches!(expected.as_str(), "Production" | "Sandbox") || v["environment"] != expected {
        return Err(ApiError::bad("Apple environment mismatch"));
    }
    Ok(())
}
pub fn transaction(jws: &str) -> Result<Value> {
    let v = if std::env::var("APP_ENV").as_deref() == Ok("development")
        && std::env::var("APPLE_ALLOW_XCODE").as_deref() == Ok("true")
    {
        let parts: Vec<_> = jws.split('.').collect();
        let v: Value = parts
            .get(1)
            .and_then(|p| URL_SAFE_NO_PAD.decode(p).ok())
            .and_then(|b| serde_json::from_slice(&b).ok())
            .ok_or(ApiError::bad("test transaction"))?;
        if v["environment"] == "Xcode" {
            v
        } else {
            verify(jws)?
        }
    } else {
        verify(jws)?
    };
    if v["bundleId"] != "com.orcub.puzzle" {
        return Err(ApiError::bad("bundle mismatch"));
    }
    if v["environment"] != "Xcode" {
        environment(&v)?;
    }
    Ok(v)
}

/// Optional online cross-check; offline verification remains mandatory.
pub async fn server_check(v: &Value) -> Result<()> {
    let issuer = std::env::var("APPLE_API_ISSUER_ID").ok();
    let kid = std::env::var("APPLE_API_KEY_ID").ok();
    let key_path = std::env::var("APPLE_API_KEY_PATH").ok();
    if issuer.is_none() && kid.is_none() && key_path.is_none() {
        return Ok(());
    }
    let (Some(issuer), Some(kid), Some(key_path)) = (issuer, kid, key_path) else {
        return Err(ApiError::unavailable());
    };
    if v["environment"] == "Xcode" {
        return Ok(());
    }
    let key = std::fs::read(key_path).map_err(|_| ApiError::unavailable())?;
    let key = jsonwebtoken::EncodingKey::from_ec_pem(&key).map_err(|_| ApiError::unavailable())?;
    let mut header = jsonwebtoken::Header::new(jsonwebtoken::Algorithm::ES256);
    header.kid = Some(kid);
    let now = chrono::Utc::now().timestamp();
    let jwt=jsonwebtoken::encode(&header,&serde_json::json!({"iss":issuer,"iat":now,"exp":now+300,"aud":"appstoreconnect-v1","bid":"com.orcub.puzzle"}),&key).map_err(|_|ApiError::unavailable())?;
    let tid = v["transactionId"]
        .as_str()
        .filter(|s| s.len() <= 200 && s.bytes().all(|b| b.is_ascii_digit()))
        .ok_or(ApiError::bad("transactionId"))?;
    let host = if v["environment"] == "Sandbox" {
        "api.storekit-sandbox.itunes.apple.com"
    } else {
        "api.storekit.itunes.apple.com"
    };
    let response: Value = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(10))
        .build()
        .map_err(|_| ApiError::unavailable())?
        .get(format!("https://{host}/inApps/v1/transactions/{tid}"))
        .bearer_auth(jwt)
        .send()
        .await
        .map_err(|_| ApiError::unavailable())?
        .error_for_status()
        .map_err(|_| ApiError::unavailable())?
        .json()
        .await
        .map_err(|_| ApiError::unavailable())?;
    let online = transaction(
        response["signedTransactionInfo"]
            .as_str()
            .ok_or(ApiError::unavailable())?,
    )?;
    if online["transactionId"] != v["transactionId"]
        || online["productId"] != v["productId"]
        || online.get("revocationDate").is_some_and(|v| !v.is_null())
    {
        return Err(ApiError::bad("online transaction mismatch or revoked"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use openssl::{
        asn1::Asn1Time,
        ec::{EcGroup, EcKey},
        nid::Nid,
        pkey::{PKey, Private},
        sign::Signer,
        x509::{
            X509NameBuilder,
            extension::{BasicConstraints, KeyUsage},
        },
    };
    fn cert(
        name: &str,
        key: &PKey<Private>,
        issuer: Option<(&X509, &PKey<Private>)>,
        ca: bool,
    ) -> X509 {
        let mut n = X509NameBuilder::new().unwrap();
        n.append_entry_by_text("CN", name).unwrap();
        let n = n.build();
        let mut b = X509::builder().unwrap();
        b.set_version(2).unwrap();
        let serial = BigNum::from_u32(if name == "root" {
            1
        } else if ca {
            2
        } else {
            3
        })
        .unwrap()
        .to_asn1_integer()
        .unwrap();
        b.set_serial_number(&serial).unwrap();
        b.set_subject_name(&n).unwrap();
        b.set_issuer_name(issuer.map(|(c, _)| c.subject_name()).unwrap_or(&n))
            .unwrap();
        b.set_pubkey(key).unwrap();
        b.set_not_before(&Asn1Time::days_from_now(0).unwrap())
            .unwrap();
        b.set_not_after(&Asn1Time::days_from_now(1).unwrap())
            .unwrap();
        if ca {
            b.append_extension(BasicConstraints::new().critical().ca().build().unwrap())
                .unwrap();
            b.append_extension(KeyUsage::new().key_cert_sign().crl_sign().build().unwrap())
                .unwrap();
        } else {
            b.append_extension(BasicConstraints::new().critical().build().unwrap())
                .unwrap();
            b.append_extension(KeyUsage::new().digital_signature().build().unwrap())
                .unwrap();
        }
        b.sign(
            issuer.map(|(_, k)| k).unwrap_or(key),
            MessageDigest::sha256(),
        )
        .unwrap();
        b.build()
    }
    #[test]
    fn signed_dates_expiry_and_environment() {
        let now = chrono::Utc::now().timestamp_millis();
        assert!(signed_time(&serde_json::json!({})).is_err());
        assert!(signed_time(&serde_json::json!({"signedDate":now+600000})).is_err());
        assert!(signed_time(&serde_json::json!({"signedDate":now,"expiresDate":now-1})).is_err());
        assert!(signed_time(&serde_json::json!({"signedDate":now-86400000})).is_ok());
        assert!(environment(&serde_json::json!({"environment":"Xcode"})).is_err());
        assert!(environment(&serde_json::json!({"environment":"Invalid"})).is_err());
    }
    #[test]
    fn self_signed_test_chain_signature_and_root() {
        let group = EcGroup::from_curve_name(Nid::X9_62_PRIME256V1).unwrap();
        let key = || PKey::from_ec_key(EcKey::generate(&group).unwrap()).unwrap();
        let rk = key();
        let root = cert("root", &rk, None, true);
        let ik = key();
        let intermediate = cert("intermediate", &ik, Some((&root, &rk)), true);
        let lk = key();
        let leaf = cert("leaf", &lk, Some((&intermediate, &ik)), false);
        let h=URL_SAFE_NO_PAD.encode(serde_json::to_vec(&serde_json::json!({"alg":"ES256","x5c":[STANDARD.encode(leaf.to_der().unwrap()),STANDARD.encode(intermediate.to_der().unwrap()),STANDARD.encode(root.to_der().unwrap())]})).unwrap());
        let p = URL_SAFE_NO_PAD.encode(serde_json::to_vec(&serde_json::json!({"bundleId":"com.orcub.puzzle","signedDate":chrono::Utc::now().timestamp_millis()})).unwrap());
        let input = format!("{h}.{p}");
        let mut signer = Signer::new(MessageDigest::sha256(), &lk).unwrap();
        signer.update(input.as_bytes()).unwrap();
        let sig = EcdsaSig::from_der(&signer.sign_to_vec().unwrap()).unwrap();
        let mut raw = sig.r().to_vec_padded(32).unwrap();
        raw.extend(sig.s().to_vec_padded(32).unwrap());
        let jws = format!("{input}.{}", URL_SAFE_NO_PAD.encode(raw));
        assert_eq!(
            verify_root(&jws, &root, false).unwrap()["bundleId"],
            "com.orcub.puzzle"
        );
        assert!(verify(&jws).is_err());
        assert!(verify_root(&jws, &root, true).is_err());
        let corrupt = jws.replacen(&p, &URL_SAFE_NO_PAD.encode(b"{}"), 1);
        assert!(verify_root(&corrupt, &root, false).is_err());
    }
}
