#[sqlx::test(migrations = "./migrations")]
async fn nonce_wait_is_bounded_and_unknown_nonce_debits_real_lives(pool: PgPool) {
    let (id,h)=player(&pool).await;
    let s=new_state(pool.clone(),"admin",false);
    let nonce=Uuid::new_v4();
    sqlx::query("INSERT INTO rewarded_nonces(nonce,player_id,placement,created_at) VALUES($1,$2,'rewarded_lives',now()-interval '11 minutes')").bind(nonce).bind(id).execute(&pool).await.unwrap();
    for nonce in [nonce,Uuid::new_v4()] {
        let b=Consume {player_id:id,kind:"life".into(),count:1,attempt_ref:Uuid::new_v4(),idempotency_key:Uuid::new_v4(),occurred_at:None,rewarded_nonce:Some(nonce)};
        let _ = consume(State(s.clone()),h.clone(),Json(b)).await.unwrap();
    }
    let lives:i32=sqlx::query_scalar("SELECT lives FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap();
    assert_eq!(lives,3);
}
#[sqlx::test(migrations = "./migrations")]
async fn first_clear_rewards_are_per_level_with_separate_boss_cap(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    for (level,expected) in (0..20).map(|n|(n,if n==19 {50} else {5})).chain([(19,0)]) {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        let b=|| Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
        assert_eq!(grant(State(s.clone()),h.clone(),Json(b())).await.unwrap().0["granted"],expected);
        assert_eq!(grant(State(s.clone()),h.clone(),Json(b())).await.unwrap().0["granted"],0);
    }
    let mut tx=pool.begin().await.unwrap();
    assert_eq!(snapshot(&mut tx,id).await.unwrap()["gold"],145);
}
#[sqlx::test(migrations = "./migrations")]
async fn spent_guest_grants_never_recredit_or_remove_later_purchases(pool: PgPool) {
    let (guest,_)=player(&pool).await; let (account,_)=player(&pool).await;
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,guest).await.unwrap(); lock(&mut tx,account).await.unwrap();
    sqlx::query("INSERT INTO daily_claims(player_id,reason,utc_day,receipt) VALUES($1,'daily_gift',CURRENT_DATE,'{\"reward\":{\"gold\":20,\"inventory\":{\"hint\":1}}}')").bind(guest).execute(&mut *tx).await.unwrap();
    // Separate timestamps define real grant -> consume -> purchase ordering.
    sqlx::query("UPDATE daily_claims SET created_at=now()-interval '2 hours' WHERE player_id=$1").bind(guest).execute(&mut *tx).await.unwrap();
    entry(&mut tx,guest,-20,"booster","x","spent",&json!({})).await.unwrap();
    entry(&mut tx,guest,0,"consume","x","hint-used",&json!({"kind":"booster_hint","count":1})).await.unwrap();
    sqlx::query("UPDATE wallets SET gold=100,inventory='{\"hint\":3}' WHERE player_id=$1").bind(guest).execute(&mut *tx).await.unwrap();
    sqlx::query("UPDATE wallets SET inventory='{\"hint\":7}' WHERE player_id=$1").bind(account).execute(&mut *tx).await.unwrap();
    merge(&mut tx,account,guest).await.unwrap(); let w=snapshot(&mut tx,account).await.unwrap();
    assert_eq!(w["gold"],100); assert_eq!(w["inventory"]["hint"],10);
}
#[sqlx::test(migrations = "./migrations")]
async fn wallet_acknowledgements_are_bounded(pool: PgPool) {
    let (id,_)=player(&pool).await; let mut tx=pool.begin().await.unwrap(); lock(&mut tx,id).await.unwrap();
    sqlx::query("INSERT INTO wallet_ledger(id,player_id,delta,reason,ref_id,idempotency_key,request,balance_after) SELECT gen_random_uuid(),$1,0,'consume','x',n::text,'{}',0 FROM generate_series(1,4200) n").bind(id).execute(&mut *tx).await.unwrap();
    assert_eq!(snapshot(&mut tx,id).await.unwrap()["applied_keys"].as_array().unwrap().len(),0);
}

#[sqlx::test(migrations = "./migrations")]
async fn forged_5000_gold_farm_is_blocked(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    for level in (19..2019).step_by(20).chain([100000,-1]) {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        let b=Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
        assert!(grant(State(s.clone()),h.clone(),Json(b)).await.is_err());
    }
    let gold:i64=sqlx::query_scalar("SELECT COALESCE((SELECT gold FROM wallets WHERE player_id=$1),0)::bigint").bind(id).fetch_one(&pool).await.unwrap(); assert_eq!(gold,0);
}
#[sqlx::test(migrations = "./migrations")]
async fn merge_first_clears_and_gold_bought_boosters(pool: PgPool) {
    let (account,_)=player(&pool).await;
    for round in 0..2 {
        let (guest,_)=player(&pool).await;
        let mut tx=pool.begin().await.unwrap(); lock(&mut tx,guest).await.unwrap(); lock(&mut tx,account).await.unwrap();
        entry(&mut tx,guest,50,"level_reward","level:19","level",&json!({})).await.unwrap();
        if round==1 {
            entry(&mut tx,guest,-40,"booster","x","buy",&json!({"booster":"hint"})).await.unwrap();
            sqlx::query("UPDATE wallets SET inventory='{\"hint\":1}' WHERE player_id=$1").bind(guest).execute(&mut *tx).await.unwrap();
        }
        merge(&mut tx,account,guest).await.unwrap();
        let w=snapshot(&mut tx,account).await.unwrap(); assert_eq!(w["gold"],50); assert_eq!(w["inventory"]["hint"].as_i64().unwrap_or(0),0);
        tx.commit().await.unwrap();
    }
}

#[sqlx::test(migrations = "./migrations")]
async fn lives_placement_alias_reaches_nonce(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool,"admin",false);
    for placement in ["rewarded_lives","out_of_lives","retry_lives","rewarded_refill_lives"] {
        let b=serde_json::from_value(json!({"player_id":id,"placement":placement})).unwrap();
        let response=crate::ssv::nonce(State(s.clone()),h.clone(),Json(b)).await.unwrap();
        assert!(response.0["nonce"].is_string());
    }
}

#[sqlx::test(migrations = "./migrations")]
async fn reward_refusals_distinguish_pending_expired_and_boss_cap(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    let event=Uuid::new_v4();
    let claim=|| Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
    assert_eq!(grant(State(s.clone()),h.clone(),Json(claim())).await.unwrap_err().0,StatusCode::CONFLICT);
    sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload,received_at) VALUES($1,$2,'level_end',now(),'{\"LevelIndex\":0,\"Result\":{\"Outcome\":0}}',now()-interval '2 days')").bind(event).bind(id).execute(&pool).await.unwrap();
    assert_eq!(grant(State(s.clone()),h.clone(),Json(claim())).await.unwrap_err().0,StatusCode::GONE);
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,id).await.unwrap();
    for level in 0..79 {
        entry(&mut tx,id,if level%20==19 {50} else {5},"level_reward",&format!("level:{level}"),&Uuid::new_v4().to_string(),&json!({})).await.unwrap();
    }
    tx.commit().await.unwrap();
    sqlx::query("UPDATE events SET payload='{\"LevelIndex\":79,\"Result\":{\"Outcome\":0}}',received_at=now() WHERE event_id=$1").bind(event).execute(&pool).await.unwrap();
    let err=grant(State(s),h,Json(claim())).await.unwrap_err(); assert_eq!(err.0,StatusCode::GONE); assert_eq!(err.1,"reward_cap");
}

#[sqlx::test(migrations = "./migrations")]
async fn wallet_refresh_filters_acknowledgements_by_pending_age(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,id).await.unwrap();
    entry(&mut tx,id,0,"consume","x","old-key",&json!({})).await.unwrap();
    sqlx::query("UPDATE wallet_ledger SET created_at=now()-interval '1 hour' WHERE player_id=$1").bind(id).execute(&mut *tx).await.unwrap();
    entry(&mut tx,id,0,"consume","x","recent-key",&json!({})).await.unwrap(); tx.commit().await.unwrap();
    let response=wallet(State(s),h,Query(WalletQuery{player_id:id,applied_since:Some(Utc::now()-chrono::Duration::minutes(5))})).await.unwrap();
    let bytes=axum::body::to_bytes(response.into_body(),100000).await.unwrap();
    let value:Value=serde_json::from_slice(&bytes).unwrap(); assert_eq!(value["applied_keys"],json!(["recent-key"]));
}
