CREATE TABLE daily_claims (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 reason text NOT NULL CHECK(reason IN ('daily_puzzle','daily_gift')),
 utc_day date NOT NULL, receipt jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(player_id,reason,utc_day)
);
CREATE TABLE daily_streaks (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 last_day date, streak integer NOT NULL DEFAULT 0 CHECK(streak>=0),
 "freeze" boolean NOT NULL DEFAULT false, freeze_earned boolean NOT NULL DEFAULT false
);
INSERT INTO daily_claims(player_id,reason,utc_day,receipt)
SELECT DISTINCT player_id,reason,(created_at AT TIME ZONE 'UTC')::date,
 jsonb_build_object('utc_day',to_char(created_at AT TIME ZONE 'UTC','YYYY-MM-DD'),'reason',reason,'streak',0,'freeze',false,'reward',jsonb_build_object('gold',20,'inventory','{}'::jsonb,'unlimited_minutes',0))
FROM wallet_ledger WHERE reason='daily_gift'
ON CONFLICT DO NOTHING;
CREATE INDEX events_daily_evidence ON events(player_id,received_at) WHERE kind='daily_puzzle_end';
