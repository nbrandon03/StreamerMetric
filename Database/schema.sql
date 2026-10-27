
-- Streamers tracked by StreamerMetric
CREATE TABLE streamers (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    twitch_user_id TEXT NOT NULL UNIQUE,
    username TEXT NOT NULL,
    display_name TEXT NOT NULL,
    profile_image_url TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- The single Twitch account connected to this local StreamerMetric instance.
-- OAuth tokens are encrypted by the API before they are stored here.
CREATE TABLE twitch_accounts (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    twitch_user_id TEXT NOT NULL UNIQUE,
    login TEXT NOT NULL,
    display_name TEXT NOT NULL,
    access_token TEXT NOT NULL,
    refresh_token TEXT NOT NULL,
    access_token_expires_at TIMESTAMPTZ NOT NULL,
    last_sync_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE twitch_account_followed_channels (
    account_id BIGINT NOT NULL REFERENCES twitch_accounts(id)
        ON DELETE CASCADE,
    streamer_id BIGINT NOT NULL REFERENCES streamers(id),
    is_following BOOLEAN NOT NULL DEFAULT TRUE,
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (account_id, streamer_id)
);

-- Individual broadcasts
CREATE TABLE streams (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    twitch_stream_id TEXT NOT NULL UNIQUE,
    streamer_id BIGINT NOT NULL REFERENCES streamers(id),
    title TEXT,
    category_name TEXT,
    started_at TIMESTAMPTZ NOT NULL,
    ended_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Chatter identities, scoped to Twitch accounts
CREATE TABLE chatters (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    twitch_user_id TEXT NOT NULL UNIQUE,
    username TEXT NOT NULL,
    display_name TEXT NOT NULL,
    first_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Messages collected from authorized Twitch chat
CREATE TABLE chat_messages (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    twitch_message_id TEXT UNIQUE,
    stream_id BIGINT NOT NULL REFERENCES streams(id),
    chatter_id BIGINT NOT NULL REFERENCES chatters(id),
    message_text TEXT NOT NULL,
    sent_at TIMESTAMPTZ NOT NULL,
    collected_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Emotes recognized in collected messages
CREATE TABLE emotes (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    emote_key TEXT NOT NULL UNIQUE,
    emote_name TEXT NOT NULL
);

-- Which emotes appeared in each message, and how often
CREATE TABLE chat_message_emotes (
    message_id BIGINT NOT NULL REFERENCES chat_messages(id)
        ON DELETE CASCADE,
    emote_id BIGINT NOT NULL REFERENCES emotes(id),
    usage_count INTEGER NOT NULL DEFAULT 1 CHECK (usage_count > 0),
    PRIMARY KEY (message_id, emote_id)
);

-- Periodic audience snapshots for historical analytics
CREATE TABLE stream_snapshots (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    stream_id BIGINT NOT NULL REFERENCES streams(id),
    captured_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    viewer_count INTEGER NOT NULL CHECK (viewer_count >= 0),
    UNIQUE (stream_id, captured_at)
);

-- Indexes for common analytics queries
CREATE INDEX idx_streams_streamer_started
    ON streams (streamer_id, started_at DESC);

CREATE INDEX idx_chat_messages_stream_sent
    ON chat_messages (stream_id, sent_at);

CREATE INDEX idx_chat_messages_chatter
    ON chat_messages (chatter_id);

CREATE INDEX idx_stream_snapshots_stream_time
    ON stream_snapshots (stream_id, captured_at);
