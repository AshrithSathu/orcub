WITH e AS (SELECT * FROM events WHERE NOT dev AND NOT EXISTS(SELECT 1 FROM installs dev WHERE dev.player_id=events.player_id AND dev.metadata->>'platform' IN ('editor','ios-dev')) AND player_id=$1),
p AS (SELECT * FROM purchases WHERE player_id=$1),
a AS (SELECT payload->>'currency' currency,sum((payload->>'value_micros')::bigint) micros FROM e WHERE kind='ad_paid' GROUP BY 1),
i AS (SELECT currency,sum(price_milliunits) milliunits FROM p WHERE NOT revoked AND environment='Production' GROUP BY 1),
s AS (SELECT count(*) sessions,count(DISTINCT (occurred_at AT TIME ZONE 'UTC')::date) days FROM e WHERE kind='session_start'),
r AS (SELECT count(*) FILTER(WHERE kind='ad_offer') offers,count(*) FILTER(WHERE kind='ad_reward') rewards FROM e WHERE payload->>'placement' LIKE 'rewarded_%'),
c AS (SELECT count(*) FILTER(WHERE EXISTS(SELECT 1 FROM e shown WHERE shown.kind='ad_shown' AND shown.payload->>'placement' LIKE 'interstitial_%' AND shown.occurred_at BETWEEN ended.occurred_at-interval '10 seconds' AND ended.occurred_at) AND NOT EXISTS(SELECT 1 FROM e resumed WHERE resumed.kind='session_start' AND resumed.occurred_at>ended.occurred_at AND resumed.occurred_at<=ended.occurred_at+interval '5 minutes') AND ended.occurred_at<now()-interval '5 minutes') churn,count(*) total FROM e ended WHERE ended.kind='session_end')
SELECT jsonb_build_object('ltv_iap',COALESCE((SELECT jsonb_object_agg(currency,milliunits) FROM i WHERE currency IS NOT NULL),'{}'),
'ltv_ads',COALESCE((SELECT jsonb_object_agg(currency,micros) FROM a),'{}'),
'payer',EXISTS(SELECT 1 FROM p WHERE NOT revoked AND environment='Production'),'remove_ads',EXISTS(SELECT 1 FROM p WHERE product_id='com.orcub.puzzle.remove_ads' AND NOT revoked),
'sessions',s.sessions,'days_active',s.days,'rewarded_acceptance',LEAST(1.0,COALESCE(r.rewards::float/NULLIF(r.offers,0),0)),
'interstitial_churn',COALESCE(c.churn::float/NULLIF(c.total,0),0),
'highest_level',GREATEST(COALESCE((SELECT (data->>'highest_level')::int FROM saves WHERE player_id=$1),0),COALESCE((SELECT max((payload->>'LevelIndex')::int) FROM e WHERE kind='level_end' AND (payload->'Result'->>'Outcome')::int=0),0))) FROM s,r,c
