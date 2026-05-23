-- Discord bot owns these three tables. The API's EF Core DbContext does NOT model
-- them, so EF migrations and the bot's migrations cannot collide. Tables reference
-- games(id) / users(id) by value only — no DB-level FKs — so the bot migration is
-- independent of which EF migration is currently applied.
--
-- Migration conventions for this directory:
--   * Each .sql file runs inside a transaction (see src/migrator.ts), so a
--     half-applied migration rolls back cleanly. Operations that REQUIRE
--     autocommit (CREATE INDEX CONCURRENTLY, REINDEX CONCURRENTLY, VACUUM,
--     ALTER TYPE ... ADD VALUE) must go in their own file marked with a
--     `-- @no-transaction` directive — the migrator does not yet honour that
--     directive, so adding one is a deliberate "extend the migrator first" gate.
--   * snake_case names, plural-table-name + singular-column. Matches API
--     EF conventions.
--   * Use `timestamptz` (never bare `timestamp`); `gen_random_uuid()` (built-in
--     since PG13, no extension); `IS NOT DISTINCT FROM` for NULL-safe equality
--     in queries (see db.ts removeSubscription).

CREATE TABLE IF NOT EXISTS discord_subscriptions (
    id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    guild_id       text NOT NULL,
    channel_id     text NOT NULL,
    -- Optional filters. NULL means "no filter on this dimension". A subscription
    -- with both NULL is the all-clips firehose for the channel.
    -- game_id is integer because games(id) is integer (Game.cs); creator_id is uuid
    -- because users(id) is uuid (User.cs).
    game_id        integer NULL,
    creator_id     uuid NULL,
    paused         boolean NOT NULL DEFAULT false,
    ping_role_id   text NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    created_by     text NOT NULL,
    -- One rule per (channel, game, creator) combo. NULLS NOT DISTINCT (PG15+)
    -- is the key choice: two `/subscribe` calls with no filters on the same
    -- channel would otherwise create two phantom firehose rows (default
    -- NULLS DISTINCT treats (C, NULL, NULL) and (C, NULL, NULL) as distinct).
    -- The semantics we want: (C, NULL, NULL) collides with itself, but
    -- (C, 42, NULL) is still distinct from (C, NULL, NULL) because 42 ≠ NULL.
    UNIQUE NULLS NOT DISTINCT (channel_id, game_id, creator_id)
);

CREATE INDEX IF NOT EXISTS discord_subscriptions_channel_idx
    ON discord_subscriptions (channel_id);
CREATE INDEX IF NOT EXISTS discord_subscriptions_game_idx
    ON discord_subscriptions (game_id) WHERE game_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS discord_subscriptions_creator_idx
    ON discord_subscriptions (creator_id) WHERE creator_id IS NOT NULL;

-- Dedupe guard: a clip is posted at most once per channel. Written AFTER a
-- successful send (see db.ts recordPost) so a crash mid-send doesn't blacklist
-- the (channel, clip) pair — at-least-once delivery with idempotent isPosted
-- pre-check. Grows unbounded; the posted_at_idx exists to support an eventual
-- retention prune (DELETE WHERE posted_at < now() - interval '90 days') which
-- isn't wired up yet (low priority until the table reaches ~1M rows).
CREATE TABLE IF NOT EXISTS discord_post_log (
    channel_id  text NOT NULL,
    clip_id     uuid NOT NULL,
    posted_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (channel_id, clip_id)
);

CREATE INDEX IF NOT EXISTS discord_post_log_posted_at_idx
    ON discord_post_log (posted_at DESC);

-- Generic key/value bag for the bot's own state. Currently holds the poller's
-- high-water-mark (`last_clip_created_at`).
CREATE TABLE IF NOT EXISTS discord_bot_state (
    key         text PRIMARY KEY,
    value       text NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);
