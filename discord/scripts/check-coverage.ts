#!/usr/bin/env bun
// Enforces the discord/ coverage gate by parsing coverage/lcov.info and failing
// when totals fall below thresholds. Bun's own `coverageThreshold` setting in
// bunfig.toml prints the report but does NOT exit non-zero (verified on 1.3.13),
// so without this script `make ci-discord` would silently pass under-covered
// changes. Vitest enforces natively (see web/vitest.config.ts thresholds), so
// this script exists only because Bun's enforcement is broken — once that's
// fixed upstream, the script can be deleted and the bunfig.toml threshold
// becomes the source of truth.
//
// Thresholds match web/: 85% lines, 85% functions (Bun's coverage doesn't emit
// branch data, so we gate on functions instead — equivalent coverage signal in
// practice for code that uses small focused functions).

import { appendFile, readFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';

const LCOV_PATH = 'coverage/lcov.info';
const LINE_THRESHOLD = 0.85;
const FUNCTION_THRESHOLD = 0.85;

// Files included in test discovery but excluded from the coverage denominator —
// kept in sync with bunfig.toml's coveragePathIgnorePatterns. Listed by prefix
// match against the SF: path emitted by bun's lcov reporter.
const EXCLUDE_PREFIXES = [
  'src/index.ts',
  'src/migrations/',
  'src/migrator.ts',
  'src/db.ts',
  'src/api.ts',
  'tests/',
];

if (!existsSync(LCOV_PATH)) {
  console.error(`coverage gate: ${LCOV_PATH} not found — did 'bun test --coverage' run?`);
  process.exit(1);
}

const lcov = await readFile(LCOV_PATH, 'utf8');

let lineFound = 0;
let lineHit = 0;
let fnFound = 0;
let fnHit = 0;
let currentFile = '';
let includeCurrent = true;

for (const line of lcov.split('\n')) {
  if (line.startsWith('SF:')) {
    currentFile = line.slice(3);
    includeCurrent = !EXCLUDE_PREFIXES.some((p) => currentFile.startsWith(p));
    continue;
  }
  if (!includeCurrent) continue;
  if (line.startsWith('LF:')) lineFound += Number(line.slice(3));
  else if (line.startsWith('LH:')) lineHit += Number(line.slice(3));
  else if (line.startsWith('FNF:')) fnFound += Number(line.slice(4));
  else if (line.startsWith('FNH:')) fnHit += Number(line.slice(4));
}

const linePct = lineFound === 0 ? 1 : lineHit / lineFound;
const fnPct = fnFound === 0 ? 1 : fnHit / fnFound;

const fmt = (p: number) => (p * 100).toFixed(2) + '%';
const linesLabel = `${fmt(linePct)} (${lineHit}/${lineFound})`;
const fnLabel = `${fmt(fnPct)} (${fnHit}/${fnFound})`;
const thresh = (n: number) => `${(n * 100).toFixed(0)}%`;

console.log('');
console.log('Discord coverage gate:');
console.log(`  Lines:     ${linesLabel}  (threshold ${thresh(LINE_THRESHOLD)})`);
console.log(`  Functions: ${fnLabel}  (threshold ${thresh(FUNCTION_THRESHOLD)})`);

let failed = false;
if (linePct < LINE_THRESHOLD) {
  console.error(`  ✗ line coverage ${fmt(linePct)} below ${thresh(LINE_THRESHOLD)}`);
  failed = true;
}
if (fnPct < FUNCTION_THRESHOLD) {
  console.error(`  ✗ function coverage ${fmt(fnPct)} below ${thresh(FUNCTION_THRESHOLD)}`);
  failed = true;
}

// Emit a GitHub Actions step summary (markdown table) when the runner exposes
// the file. Mirrors web.yml's coverage-summary step but written in TS instead
// of a shell+jq snippet to keep the parsing in one place.
const summaryPath = process.env.GITHUB_STEP_SUMMARY;
if (summaryPath) {
  const status = failed ? '❌ failed' : '✅ passed';
  const md = [
    `## Discord coverage (scoped: src/, excludes I/O wrappers + boot)`,
    ``,
    `Gate: ${status}`,
    ``,
    `| Metric    | %     | Covered / Total | Threshold |`,
    `|-----------|-------|-----------------|-----------|`,
    `| Lines     | ${fmt(linePct)} | ${lineHit}/${lineFound} | ${thresh(LINE_THRESHOLD)} |`,
    `| Functions | ${fmt(fnPct)} | ${fnHit}/${fnFound} | ${thresh(FUNCTION_THRESHOLD)} |`,
    ``,
  ].join('\n');
  await appendFile(summaryPath, md);
}

if (failed) process.exit(1);
console.log('  ✓ coverage gate passed');
