#[sqlx::test(migrations = "./migrations")]
async fn nonce_wait_is_bounded_and_unknown_nonce_debits_real_lives(pool: PgPool) {
    let (id,h)=player(&pool).await;
    let s=new_state(pool.clone(),"admin",false);
    let nonce=Uuid::new_v4();
    sqlx::query("INSERT INTO rewarded_nonces(nonce,player_id,placement,created_at) VALUES($1,$2,'rewarded_lives',now()-interval '11 minutes')").bind(nonce).bind(id).execute(&pool).await.unwrap();
    for nonce in [nonce,Uuid::new_v4()] {
        let b=Consume {player_id:id,kind:"life".into(),count:1,attempt_ref:Uuid::new_v4(),idempotency_key:Uuid::new_v4(),occurred_at:None,rewarded_nonce:Some(nonce)};
        consume(State(s.clone()),h.clone(),Json(b)).await.unwrap();
    }
    let lives:i32=sqlx::query_scalar("SELECT lives FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap();
    assert_eq!(lives,3);
}
#[sqlx::test(migrations = "./migrations")]
async fn first_clear_rewards_are_per_level_with_separate_boss_cap(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    for (level,expected) in [(0,5),(19,50),(19,0),(219,50)] {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        let b=|| Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
        assert_eq!(grant(State(s.clone()),h.clone(),Json(b())).await.unwrap().0["granted"],expected);
        assert_eq!(grant(State(s.clone()),h.clone(),Json(b())).await.unwrap().0["granted"],0);
    }
    let mut tx=pool.begin().await.unwrap();
    assert_eq!(snapshot(&mut tx,id).await.unwrap()["gold"],105);
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
    assert_eq!(snapshot(&mut tx,id).await.unwrap()["applied_keys"].as_array().unwrap().len(),4096);
}
