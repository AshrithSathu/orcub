CREATE TABLE players (
 id uuid PRIMARY KEY, firebase_uid varchar(128) UNIQUE,
 created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE installs (
 install_id uuid PRIMARY KEY, player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 credential_hash text NOT NULL, metadata jsonb NOT NULL, country char(2),
 first_open_ts timestamptz NOT NULL, created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX installs_player ON installs(player_id);
CREATE TABLE experiments (
 player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 experiment text NOT NULL, bucket text NOT NULL, assigned_at timestamptz NOT NULL DEFAULT now(),
 PRIMARY KEY(player_id, experiment)
);
CREATE TABLE events (
 event_id uuid PRIMARY KEY, player_id uuid NOT NULL REFERENCES players(id) ON DELETE CASCADE,
 kind varchar(64) NOT NULL, occurred_at timestamptz NOT NULL, received_at timestamptz NOT NULL DEFAULT now(),
 payload jsonb NOT NULL
);
CREATE INDEX events_time_brin ON events USING brin(received_at);
CREATE INDEX events_player_time ON events(player_id, occurred_at);
CREATE INDEX events_kind_time ON events(kind, occurred_at);
CREATE INDEX events_level ON events((payload->>'LevelIndex')) WHERE kind IN ('level_start','level_end');
CREATE TABLE raw_events (
 event_id uuid PRIMARY KEY REFERENCES events(event_id) ON DELETE CASCADE,
 payload jsonb NOT NULL
);
CREATE TABLE saves (
 player_id uuid PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
 revision bigint NOT NULL CHECK(revision >= 1), data jsonb NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT now()
);
