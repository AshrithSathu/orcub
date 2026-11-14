WITH e AS (SELECT *, (occurred_at AT TIME ZONE 'UTC')::date AS day FROM events WHERE occurred_at >= $1 AND occurred_at < $2),
p AS (SELECT *, (purchased_at AT TIME ZONE 'UTC')::date AS day FROM purchases WHERE purchased_at >= $1 AND purchased_at < $2 AND NOT revoked),
dau AS (SELECT day,count(DISTINCT player_id) n FROM e WHERE kind='session_start' GROUP BY 1),
i AS (SELECT day,currency,sum(price_milliunits)*1000 revenue,count(DISTINCT player_id) payers FROM p WHERE currency IS NOT NULL GROUP BY 1,2),
a AS (SELECT day,payload->>'currency' currency,sum((payload->>'value_micros')::bigint) revenue FROM e WHERE kind='ad_paid' GROUP BY 1,2),
d AS (SELECT day,currency FROM i UNION SELECT day,currency FROM a),
loads AS (SELECT start.event_id,count(shown.event_id) ads FROM e start LEFT JOIN e shown ON shown.player_id=start.player_id AND shown.kind='ad_shown' AND shown.payload->>'placement' LIKE 'interstitial_%' AND shown.occurred_at>=start.occurred_at AND shown.occurred_at<COALESCE((SELECT min(next.occurred_at) FROM events next WHERE next.player_id=start.player_id AND next.kind IN ('session_start','session_end') AND next.occurred_at>start.occurred_at),$2) WHERE start.kind='session_start' GROUP BY start.event_id),
dist AS (SELECT ads,count(*) sessions FROM loads GROUP BY ads)
SELECT jsonb_build_object('daily',COALESCE((SELECT jsonb_agg(jsonb_build_object('day',d.day,'currency',d.currency,'iap_revenue_micros',COALESCE(i.revenue,0),'ad_revenue_micros',COALESCE(a.revenue,0),'dau',COALESCE(dau.n,0),'arpdau_micros',(COALESCE(i.revenue,0)+COALESCE(a.revenue,0))/NULLIF(dau.n,0),'payer_conversion',COALESCE(i.payers,0)::float/NULLIF(dau.n,0),'arppu_micros',i.revenue/NULLIF(i.payers,0)) ORDER BY d.day,d.currency) FROM d LEFT JOIN i USING(day,currency) LEFT JOIN a USING(day,currency) LEFT JOIN dau USING(day)),'[]'),
'by_product',COALESCE((SELECT jsonb_agg(to_jsonb(x)) FROM (SELECT product_id,currency,sum(price_milliunits)*1000 revenue_micros,count(*) purchases FROM p GROUP BY 1,2) x),'[]'),
'by_placement',COALESCE((SELECT jsonb_agg(to_jsonb(x)) FROM (SELECT payload->>'placement' placement,payload->>'currency' currency,sum((payload->>'value_micros')::bigint) revenue_micros FROM e WHERE kind='ad_paid' GROUP BY 1,2) x),'[]'),
'ad_load_distribution',COALESCE((SELECT jsonb_agg(to_jsonb(dist) ORDER BY ads) FROM dist),'[]')) data
