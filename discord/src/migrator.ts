import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import type { Sql } from 'postgres';

const MIGRATIONS_DIR = new URL('./migrations/', import.meta.url).pathname;

export async function runMigrations(sql: Sql): Promise<string[]> {
  await sql.unsafe(`
    CREATE TABLE IF NOT EXISTS discord_migrations (
      filename    text PRIMARY KEY,
      applied_at  timestamptz NOT NULL DEFAULT now()
    )
  `);

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
}
