import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Sql } from 'postgres';

// fileURLToPath instead of `.pathname` because on Windows the latter yields
// `/C:/...` which fs APIs can't open. Linux-only deploy today (Bun Alpine
// container), but cheap cross-platform safety for dev machines.
const MIGRATIONS_DIR = fileURLToPath(new URL('./migrations/', import.meta.url));

// Arbitrary stable identifier for the discord-bot migration advisory lock.
// Postgres docs recommend application-defined int64s here; any value works as
// long as it's stable across boots (so two starting instances pick the same
// lock). 32-bit signed so it stays within the single-arg pg_advisory_lock(int)
// overload across drivers.
const MIGRATION_LOCK_ID = 0x6766_7462; // hex 'gftb' — Gankedtv Discord Bot

export async function runMigrations(sql: Sql): Promise<string[]> {
  await sql.unsafe(`
    CREATE TABLE IF NOT EXISTS discord_migrations (
      filename    text PRIMARY KEY,
      applied_at  timestamptz NOT NULL DEFAULT now()
    )
  `);

  // Reserve a single backend connection for the entire lock → apply → unlock
  // sequence. PG advisory locks are session-scoped (per-backend); without
  // sql.reserve() the three queries can land on different pooled connections
  // and the lock provides zero protection: two concurrent boots would each
  // acquire the lock on their own connection (PG sees them as separate
  // sessions, both get the lock), race through the apply loop, and the
  // INSERT INTO discord_migrations PK collision would crash one of them.
  const conn = await sql.reserve();
  try {
    await conn`SELECT pg_advisory_lock(${MIGRATION_LOCK_ID})`;
    try {
      const all = (await readdir(MIGRATIONS_DIR)).filter((f) => f.endsWith('.sql')).sort();

      const applied = await conn<{ filename: string }[]>`
        SELECT filename FROM discord_migrations
      `;
      const appliedSet = new Set(applied.map((r) => r.filename));

      const ranNow: string[] = [];
      for (const filename of all) {
        if (appliedSet.has(filename)) continue;
        const body = await readFile(join(MIGRATIONS_DIR, filename), 'utf8');
        // begin() on the reserved connection inherits the same session, so the
        // advisory lock held above also covers the DDL transaction.
        await conn.begin(async (tx) => {
          await tx.unsafe(body);
          await tx`INSERT INTO discord_migrations (filename) VALUES (${filename})`;
        });
        ranNow.push(filename);
      }
      return ranNow;
    } finally {
      await conn`SELECT pg_advisory_unlock(${MIGRATION_LOCK_ID})`;
    }
  } finally {
    conn.release();
  }
}
