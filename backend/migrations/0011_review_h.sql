-- Stabilise legacy identities before retention can remove their evidence.
UPDATE wallet_ledger l SET ref_id='level:' || (e.payload->>'LevelIndex')
FROM events e WHERE l.player_id=e.player_id AND l.ref_id=e.event_id::text
AND l.reason='level_reward' AND e.kind='level_end';
ALTER TABLE players ADD COLUMN rewarded_frontier integer NOT NULL DEFAULT -1;
-- Existing holes are grandfathered; never reset progress or repay old rewards.
UPDATE players p SET rewarded_frontier=x.frontier FROM
(SELECT player_id,max(substring(ref_id from 7)::integer) frontier FROM wallet_ledger
 WHERE reason='level_reward' AND ref_id ~ '^level:[0-9]{1,6}$' GROUP BY player_id) x
WHERE p.id=x.player_id;
CREATE TABLE deferred_level_rewards (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 level integer NOT NULL CHECK(level>=0 AND level<100000),
 PRIMARY KEY(player_id,level)
);
CREATE INDEX events_player_win_level ON events(player_id, ((payload->>'LevelIndex')::int))
WHERE kind='level_end' AND (payload->'Result'->>'Outcome')::int=0;
CREATE INDEX ledger_reward_daily ON wallet_ledger(player_id,delta,created_at) WHERE reason='level_reward';
CREATE INDEX deferred_reward_kind ON deferred_level_rewards(player_id,(level%20=19),level);
