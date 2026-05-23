import postgres from 'postgres';
import type { Sql } from 'postgres';

export type Subscription = {
  id: string;
  guildId: string;
  channelId: string;
  gameId: number | null;
  creatorId: string | null;
  paused: boolean;
  pingRoleId: string | null;
  createdAt: Date;
  createdBy: string;
};

export type CreateSubscriptionInput = {
  guildId: string;
  channelId: string;
  gameId: number | null;
  creatorId: string | null;
  pingRoleId: string | null;
  createdBy: string;
};

export type RemoveSubscriptionInput = {
  channelId: string;
  gameId: number | null;
  creatorId: string | null;
};

export type Db = {
  sql: Sql;
  close(): Promise<void>;
  addSubscription(input: CreateSubscriptionInput): Promise<Subscription | null>;
  removeSubscription(input: RemoveSubscriptionInput): Promise<number>;
  listSubscriptionsForChannel(channelId: string): Promise<Subscription[]>;
  listAllSubscriptions(): Promise<Subscription[]>;
  setPaused(channelId: string, paused: boolean): Promise<number>;
  isPosted(channelId: string, clipId: string): Promise<boolean>;
  recordPost(channelId: string, clipId: string): Promise<void>;
  getState(key: string): Promise<string | null>;
  setState(key: string, value: string): Promise<void>;
};

export function createDb(sql: Sql): Db {
  return {
    sql,

    async close() {
      await sql.end({ timeout: 5 });
    },

    async addSubscription(input) {
      // ON CONFLICT DO NOTHING + RETURNING gives us "inserted or already exists"
      // in one round-trip; null return == "already subscribed with these filters".
      // The UNIQUE constraint uses NULLS NOT DISTINCT (see migration), so an
      // all-null firehose subscription correctly collides with itself.
      const rows = await sql<Subscription[]>`
        INSERT INTO discord_subscriptions
          (guild_id, channel_id, game_id, creator_id, ping_role_id, created_by)
        VALUES
          (${input.guildId}, ${input.channelId}, ${input.gameId},
           ${input.creatorId}, ${input.pingRoleId}, ${input.createdBy})
        ON CONFLICT (channel_id, game_id, creator_id) DO NOTHING
        RETURNING id, guild_id, channel_id, game_id, creator_id,
                  paused, ping_role_id, created_at, created_by
      `;
      return rows[0] ?? null;
    },

    async removeSubscription(input) {
      // IS NOT DISTINCT FROM is the Postgres-idiomatic NULL-safe equality:
      // NULL "equals" NULL and otherwise behaves like =. Replaces the
      // verbose (X IS NULL AND col IS NULL) OR (col = X) pattern.
      const rows = await sql`
        DELETE FROM discord_subscriptions
        WHERE channel_id = ${input.channelId}
          AND game_id IS NOT DISTINCT FROM ${input.gameId}::int
          AND creator_id IS NOT DISTINCT FROM ${input.creatorId}::uuid
        RETURNING id
      `;
      return rows.length;
    },

    async listSubscriptionsForChannel(channelId) {
      return sql<Subscription[]>`
        SELECT id, guild_id, channel_id, game_id, creator_id,
               paused, ping_role_id, created_at, created_by
        FROM discord_subscriptions
        WHERE channel_id = ${channelId}
        ORDER BY created_at ASC
      `;
    },

    async listAllSubscriptions() {
      return sql<Subscription[]>`
        SELECT id, guild_id, channel_id, game_id, creator_id,
               paused, ping_role_id, created_at, created_by
        FROM discord_subscriptions
        WHERE paused = false
        ORDER BY created_at ASC
      `;
    },

    async setPaused(channelId, paused) {
      const rows = await sql`
        UPDATE discord_subscriptions
        SET paused = ${paused}
        WHERE channel_id = ${channelId}
        RETURNING id
      `;
      return rows.length;
    },

    async isPosted(channelId, clipId) {
      // Read-only check used by the poller BEFORE sending, so we can skip
      // clips we've already posted (idempotent on restart with un-advanced
      // cursor, or with the tied-timestamp boundary case).
      const rows = await sql<{ exists: boolean }[]>`
        SELECT EXISTS (
          SELECT 1 FROM discord_post_log
          WHERE channel_id = ${channelId} AND clip_id = ${clipId}
        ) AS exists
      `;
      return rows[0]?.exists ?? false;
    },

    async recordPost(channelId, clipId) {
      // Called AFTER a successful send. ON CONFLICT DO NOTHING because a race
      // (or restart) could repeat the insert — accepting at-least-once delivery
      // is the correct tradeoff (exactly-once would require a 2PC between
      // Discord and Postgres, which doesn't exist).
      await sql`
        INSERT INTO discord_post_log (channel_id, clip_id)
        VALUES (${channelId}, ${clipId})
        ON CONFLICT DO NOTHING
      `;
    },

    async getState(key) {
      const rows = await sql<{ value: string }[]>`
        SELECT value FROM discord_bot_state WHERE key = ${key}
      `;
      return rows[0]?.value ?? null;
    },

    async setState(key, value) {
      // updated_at omitted from the INSERT path so the column DEFAULT fires;
      // the UPDATE path sets it explicitly because DEFAULT doesn't trigger on
      // UPDATE. ON CONFLICT (key) targets the primary-key constraint.
      await sql`
        INSERT INTO discord_bot_state (key, value)
        VALUES (${key}, ${value})
        ON CONFLICT (key) DO UPDATE
          SET value = EXCLUDED.value, updated_at = now()
      `;
    },
  };
}

export function connect(url: string): Sql {
  return postgres(url, {
    // Mute server-side NOTICE messages (e.g. "IF NOT EXISTS" no-ops) so they
    // don't clutter the bot's structured JSON logs.
    onnotice: () => {},
    // Automatic snake_case ↔ camelCase column-name transforms on read, so a
    // SELECT returning `guild_id` lands in JS as `guildId` and we can type
    // queries directly with `sql<Subscription[]>` instead of hand-mapping each
    // row. `undefined: null` coerces JS `undefined` parameter values to SQL
    // NULL (porsager/postgres throws by default — the coercion saves us from
    // sprinkling `?? null` at every nullable boundary).
    //
    // Gotcha: this transform is GLOBAL — it applies to system catalog queries
    // too. If we ever SELECT from information_schema or pg_catalog, read the
    // camelCased keys on the JS side (`tableName`, not `table_name`).
    transform: { ...postgres.camel, undefined: null },
    // Server-side safety timeouts. The bot's queries are all small lookups
    // and upserts, so anything slower than 30s is a sign of trouble (lock
    // contention, runaway query, dead replica). 60s for idle-in-tx prevents
    // a hung connection from holding locks indefinitely on shutdown errors.
    connection: {
      statement_timeout: 30_000,
      idle_in_transaction_session_timeout: 60_000,
    },
  });
}
