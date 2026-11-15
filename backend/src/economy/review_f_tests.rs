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
        if !(0..100000).contains(&level) { assert!(grant(State(s.clone()),h.clone(),Json(b)).await.is_err()); } else { let _=grant(State(s.clone()),h.clone(),Json(b)).await.unwrap(); }
    }
    let gold:i64=sqlx::query_scalar("SELECT COALESCE((SELECT gold FROM wallets WHERE player_id=$1),0)::bigint").bind(id).fetch_one(&pool).await.unwrap(); assert_eq!(gold,150);
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
async fn wallet_refresh_filters_acknowledgements_by_pending_age(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,id).await.unwrap();
    entry(&mut tx,id,0,"consume","x","old-key",&json!({})).await.unwrap();
    sqlx::query("UPDATE wallet_ledger SET created_at=now()-interval '2 hours' WHERE player_id=$1").bind(id).execute(&mut *tx).await.unwrap();
    entry(&mut tx,id,0,"consume","x","recent-key",&json!({})).await.unwrap(); tx.commit().await.unwrap();
    let response=wallet(State(s),h,Query(WalletQuery{player_id:id,applied_since:Some((Utc::now()-chrono::Duration::minutes(5)).to_rfc3339())})).await.unwrap();
    let bytes=axum::body::to_bytes(response.into_body(),100000).await.unwrap();
    let value:Value=serde_json::from_slice(&bytes).unwrap(); assert_eq!(value["applied_keys"],json!(["recent-key"]));
}

#[sqlx::test(migrations = "./migrations")]
async fn deferred_caps_roll_over_and_expired_evidence_and_dropped_claims_recover(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    let mut paid=0; let mut deferred=0;
    for level in 0..47 {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,received_at,payload) VALUES($1,$2,'level_end',now(),now()-interval '2 days',$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        let b=Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
        let v=grant(State(s.clone()),h.clone(),Json(b)).await.unwrap().0;
        if level%20!=19 { if v["granted"]==5 {paid+=1;}
            if v["deferred"]==true {deferred+=1;} }
    }
    assert_eq!((paid,deferred),(20,25));
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT gold FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap(),200);
    sqlx::query("UPDATE wallet_ledger SET created_at=now()-interval '1 day' WHERE player_id=$1").bind(id).execute(&pool).await.unwrap();
    let _=wallet(State(s.clone()),h.clone(),Query(WalletQuery{player_id:id,applied_since:None})).await.unwrap();
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT gold FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap(),300);
    sqlx::query("UPDATE wallet_ledger SET created_at=now()-interval '1 day' WHERE player_id=$1").bind(id).execute(&pool).await.unwrap();
    let _=wallet(State(s.clone()),h.clone(),Query(WalletQuery{player_id:id,applied_since:None})).await.unwrap();
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT gold FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap(),325);
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT count(*) FROM deferred_level_rewards WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap(),0);
    // Lost client queue item at 47 is recovered when 48 is claimed.
    for level in 47..49 {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        if level==48 { let b=Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None}; assert_eq!(grant(State(s.clone()),h.clone(),Json(b)).await.unwrap().0["granted"],10); }
    }
}

#[sqlx::test(migrations = "./migrations")]
async fn legacy_frontier_survives_pruning_and_mixed_paid_booster_survives_merge(pool: PgPool) {
    let (guest,_)=player(&pool).await; let (account,_)=player(&pool).await;
    let event=Uuid::new_v4();
    sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),'{\"LevelIndex\":4,\"Result\":{\"Outcome\":0}}')").bind(event).bind(guest).execute(&pool).await.unwrap();
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,guest).await.unwrap();
    entry(&mut tx,guest,5,"level_reward",&event.to_string(),"legacy",&json!({})).await.unwrap();
    entry(&mut tx,guest,1000,"iap","paid","iap",&json!({})).await.unwrap();
    entry(&mut tx,guest,-40,"booster","x","booster",&json!({"booster":"hint"})).await.unwrap();
    sqlx::query("UPDATE wallets SET inventory='{\"hint\":1}' WHERE player_id=$1").bind(guest).execute(&mut *tx).await.unwrap(); tx.commit().await.unwrap();
    sqlx::raw_sql("DROP TABLE deferred_level_rewards; DROP INDEX events_player_win_level; DROP INDEX ledger_reward_daily; ALTER TABLE players DROP COLUMN rewarded_frontier;").execute(&pool).await.unwrap();
    sqlx::raw_sql(include_str!("../../migrations/0011_review_h.sql")).execute(&pool).await.unwrap();
    sqlx::query("DELETE FROM events WHERE player_id=$1").bind(guest).execute(&pool).await.unwrap();
    assert_eq!(sqlx::query_scalar::<_,i32>("SELECT rewarded_frontier FROM players WHERE id=$1").bind(guest).fetch_one(&pool).await.unwrap(),4);
    assert_eq!(sqlx::query_scalar::<_,String>("SELECT ref_id FROM wallet_ledger WHERE player_id=$1 AND reason='level_reward'").bind(guest).fetch_one(&pool).await.unwrap(),"level:4");
    let mut tx=pool.begin().await.unwrap(); merge(&mut tx,account,guest).await.unwrap();
    assert_eq!(snapshot(&mut tx,account).await.unwrap()["inventory"]["hint"],1);
}

#[sqlx::test(migrations = "./migrations")]
async fn boss_debt_and_normal_debt_have_independent_daily_caps(pool: PgPool) {
    let (id,_)=player(&pool).await; let mut tx=pool.begin().await.unwrap(); lock(&mut tx,id).await.unwrap();
    for level in 0..100 { sqlx::query("INSERT INTO deferred_level_rewards VALUES($1,$2)").bind(id).bind(level).execute(&mut *tx).await.unwrap(); }
    assert_eq!(pay_deferred(&mut tx,id).await.unwrap(),250); // 20 normals + 3 bosses
    assert_eq!(pay_deferred(&mut tx,id).await.unwrap(),0);
    sqlx::query("UPDATE wallet_ledger SET created_at=now()-interval '1 day' WHERE player_id=$1").bind(id).execute(&mut *tx).await.unwrap();
    assert_eq!(pay_deferred(&mut tx,id).await.unwrap(),200); // 20 normals + remaining 2 bosses
}

#[sqlx::test(migrations = "./migrations")]
async fn missing_level_five_does_not_block_six_to_twelve(pool: PgPool) {
    let (id,h)=player(&pool).await; let s=new_state(pool.clone(),"admin",false);
    for level in (0..13).filter(|n| *n!=5) {
        let event=Uuid::new_v4();
        sqlx::query("INSERT INTO events(event_id,player_id,kind,occurred_at,payload) VALUES($1,$2,'level_end',now(),$3)").bind(event).bind(id).bind(json!({"LevelIndex":level,"Result":{"Outcome":0}})).execute(&pool).await.unwrap();
        let b=Action{player_id:id,idempotency_key:event,ref_id:event,reason:"level_reward".into(),booster:None,utc_day:None};
        assert_eq!(grant(State(s.clone()),h.clone(),Json(b)).await.unwrap().0["granted"],5);
    }
    assert_eq!(sqlx::query_scalar::<_,i64>("SELECT gold FROM wallets WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap(),60);
}
#[sqlx::test(migrations = "./migrations")]
async fn merge_paid_guest_cancels_account_debt(pool: PgPool) {
    let (account,_)=player(&pool).await; let (guest,_)=player(&pool).await;
    let mut tx=pool.begin().await.unwrap(); lock(&mut tx,account).await.unwrap(); lock(&mut tx,guest).await.unwrap();
    for level in 0..6 {
        entry(&mut tx,guest,5,"level_reward",&format!("level:{level}"),&format!("g:{level}"),&json!({})).await.unwrap();
        if level<3 {entry(&mut tx,account,5,"level_reward",&format!("level:{level}"),&format!("c:{level}"),&json!({})).await.unwrap();}
        else {sqlx::query("INSERT INTO deferred_level_rewards VALUES($1,$2)").bind(account).bind(level).execute(&mut *tx).await.unwrap();}
    }
    merge(&mut tx,account,guest).await.unwrap();
    assert_eq!(snapshot(&mut tx,account).await.unwrap()["gold"],30);
    assert_eq!(pay_deferred(&mut tx,account).await.unwrap(),0);
    assert_eq!(snapshot(&mut tx,account).await.unwrap()["gold"],30);
}
#[test]
fn reward_limits_read_config() {
    let mut config=catalog();
    config["grants"]=json!({"level_reward_daily_cap":2,"level_reward":7,"boss_reward_daily_cap":1,"boss_reward":31});
    assert_eq!(reward_limits(&config),[(false,2,7),(true,1,31)]);
}

#[sqlx::test(migrations = "./migrations")]
async fn boss_backlog_is_capped_and_excess_does_not_return(pool: PgPool) {
    let (id,h)=player(&pool).await;
    let s=new_state(pool.clone(),"admin",false);
    sqlx::query("INSERT INTO deferred_level_rewards(player_id,level) SELECT $1,n*20+19 FROM generate_series(0,4999) n").bind(id).execute(&pool).await.unwrap();
    let mut tx=pool.begin().await.unwrap();
    lock(&mut tx,id).await.unwrap();
    assert_eq!(pay_deferred(&mut tx,id).await.unwrap(),150);
    tx.commit().await.unwrap();
    let count:i64=sqlx::query_scalar("SELECT count(*) FROM deferred_level_rewards WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap();
    assert_eq!(count,57);
    let _=wallet(State(s),h,Query(WalletQuery{player_id:id,applied_since:None})).await.unwrap();
    let count:i64=sqlx::query_scalar("SELECT count(*) FROM deferred_level_rewards WHERE player_id=$1").bind(id).fetch_one(&pool).await.unwrap();
    assert_eq!(count,57);
}
