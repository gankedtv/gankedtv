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

  // pg_advisory_lock serialises concurrent boots so two instances can't race
  // through the apply loop (which would otherwise double-run a migration's
  // DDL before either gets to INSERT the filename). Session-scoped, so a
  // crashed instance auto-releases. Lock is held only for the apply loop,
  // not for the lifetime of the connection.
  await sql`SELECT pg_advisory_lock(${MIGRATION_LOCK_ID})`;
  try {
    const all = (await readdir(MIGRATIONS_DIR)).filter((f) => f.endsWith('.sql')).sort();

    const applied = await sql<{ filename: string }[]>`
      SELECT filename FROM discord_migrations
    `;
    const appliedSet = new Set(applied.map((r) => r.filename));

    const ranNow: string[] = [];
    for (const filename of all) {
      if (appliedSet.has(filename)) continue;
      const body = await readFile(join(MIGRATIONS_DIR, filename), 'utf8');
      await sql.begin(async (tx) => {
        await tx.unsafe(body);
        await tx`INSERT INTO discord_migrations (filename) VALUES (${filename})`;
      });
      ranNow.push(filename);
    }
    return ranNow;
  } finally {
    await sql`SELECT pg_advisory_unlock(${MIGRATION_LOCK_ID})`;
  }
}
