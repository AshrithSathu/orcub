WITH grouped AS (
 SELECT (payload->>'LevelIndex')::int AS level,
 count(*) FILTER(WHERE kind='level_start') AS starts,
 count(*) FILTER(WHERE kind='level_end') AS ends,
 count(*) FILTER(WHERE kind='level_end' AND payload->'Result'->>'Outcome'='0') AS wins,
 count(*) FILTER(WHERE kind='level_end' AND payload->'Result'->>'Outcome'='1') AS losses,
 count(*) FILTER(WHERE kind='level_end' AND payload->'Result'->>'Outcome'='2') AS quits,
 percentile_cont(0.5) WITHIN GROUP(ORDER BY (payload->'Result'->>'MovesUsed')::numeric) FILTER(WHERE kind='level_end') AS median_moves,
 percentile_cont(0.5) WITHIN GROUP(ORDER BY (payload->'Result'->>'Seconds')::numeric) FILTER(WHERE kind='level_end') AS median_seconds,
 sum((payload->'Result'->>'BoostersUsed')::int) FILTER(WHERE kind='level_end') AS boosters_used
 FROM events WHERE kind IN ('level_start','level_end') AND occurred_at >= $1 AND occurred_at < $2 GROUP BY 1
) SELECT to_jsonb(g)||jsonb_build_object('fail_rate', losses::numeric/nullif(ends,0)) AS data FROM grouped g ORDER BY level;
