WITH daily AS (
 SELECT (occurred_at AT TIME ZONE 'UTC')::date AS day,
 count(DISTINCT player_id) FILTER(WHERE kind='session_start') AS dau,
 count(*) FILTER(WHERE kind='ad_shown') AS impressions,
 count(*) FILTER(WHERE kind='ad_shown' AND left(payload->>'placement',9)='rewarded_') AS rewarded_impressions,
 count(*) FILTER(WHERE kind='ad_reward' AND left(payload->>'placement',9)='rewarded_') AS rewards,
 count(*) FILTER(WHERE kind='ad_offer' AND left(payload->>'placement',9)='rewarded_') AS offers
 FROM events WHERE occurred_at >= $1 AND occurred_at < $2 GROUP BY 1
), revenue AS (
 SELECT (occurred_at AT TIME ZONE 'UTC')::date AS day,payload->>'currency' AS currency,sum((payload->>'value_micros')::numeric)/1000000 AS revenue
 FROM events WHERE kind='ad_paid' AND occurred_at >= $1 AND occurred_at < $2 GROUP BY 1,2
) SELECT to_jsonb(d)||jsonb_build_object('rewarded_completion_rate',rewards::numeric/nullif(rewarded_impressions,0),'rewarded_completion_per_offer',rewards::numeric/nullif(offers,0),'currency',r.currency,'revenue',r.revenue,'arpdau',r.revenue/nullif(dau,0)) AS data FROM daily d LEFT JOIN revenue r USING(day) ORDER BY day,currency;
