WITH cohort AS (
 SELECT id, (created_at AT TIME ZONE 'UTC')::date AS day FROM players WHERE NOT EXISTS(SELECT 1 FROM installs dev WHERE dev.player_id=players.id AND dev.metadata->>'platform' IN ('editor','ios-dev')) AND created_at >= $1 AND created_at < $2
), active AS (
 SELECT DISTINCT player_id,(occurred_at AT TIME ZONE 'UTC')::date AS day FROM events WHERE NOT dev AND NOT EXISTS(SELECT 1 FROM installs dev WHERE dev.player_id=events.player_id AND dev.metadata->>'platform' IN ('editor','ios-dev')) AND kind='session_start' AND occurred_at >= $1 AND occurred_at < $2+interval '8 days'
), grouped AS (
 SELECT c.day,count(*) AS players,
 count(*) FILTER(WHERE EXISTS(SELECT 1 FROM active a WHERE a.player_id=c.id AND a.day=c.day+1)) AS d1,
 count(*) FILTER(WHERE EXISTS(SELECT 1 FROM active a WHERE a.player_id=c.id AND a.day=c.day+7)) AS d7
 FROM cohort c GROUP BY c.day
) SELECT to_jsonb(g)||jsonb_build_object('d1_rate', CASE WHEN day+2 <= (now() AT TIME ZONE 'UTC')::date THEN d1::numeric/players END,'d7_rate',CASE WHEN day+8 <= (now() AT TIME ZONE 'UTC')::date THEN d7::numeric/players END) AS data FROM grouped g ORDER BY day;
