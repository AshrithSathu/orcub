CREATE TABLE event_quotas (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 day date NOT NULL, count integer NOT NULL CHECK(count<=10000),
 PRIMARY KEY(player_id,day)
);
CREATE INDEX players_created ON players(created_at);
CREATE INDEX events_retention ON events(received_at);
