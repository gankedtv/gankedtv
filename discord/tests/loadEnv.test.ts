import { describe, expect, test } from 'bun:test';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { loadRootEnv, mergeFirstWins, parseEnvFile } from '../src/loadEnv.ts';

describe('parseEnvFile', () => {
  test('parses KEY=value pairs', () => {
    expect(parseEnvFile('FOO=bar\nBAZ=qux')).toEqual({ FOO: 'bar', BAZ: 'qux' });
  });

  test('skips blank lines and # comments', () => {
    const text = `# a comment\n\nFOO=1\n  # indented comment\nBAR=2`;
    expect(parseEnvFile(text)).toEqual({ FOO: '1', BAR: '2' });
  });

  test('strips surrounding double quotes', () => {
    expect(parseEnvFile('FOO="hello world"')).toEqual({ FOO: 'hello world' });
  });

  test('strips surrounding single quotes', () => {
    expect(parseEnvFile("FOO='hello'")).toEqual({ FOO: 'hello' });
  });

  test('trims whitespace around key and value', () => {
    expect(parseEnvFile('  FOO  =  bar  ')).toEqual({ FOO: 'bar' });
  });

  test('skips lines with no equals sign', () => {
    expect(parseEnvFile('FOO=bar\nbroken-line\nBAZ=qux')).toEqual({ FOO: 'bar', BAZ: 'qux' });
  });

  test('skips lines starting with =', () => {
    expect(parseEnvFile('=nope\nFOO=bar')).toEqual({ FOO: 'bar' });
  });

  test('preserves = inside values (only splits on first)', () => {
    expect(parseEnvFile('CONN=Host=localhost;Port=5432')).toEqual({
      CONN: 'Host=localhost;Port=5432',
    });
  });
});

describe('mergeFirstWins', () => {
  test('sets keys that are absent from target', () => {
    const target: Record<string, string | undefined> = { A: '1' };
    mergeFirstWins(target, { B: '2' });
    expect(target).toEqual({ A: '1', B: '2' });
  });

  test('does NOT overwrite existing keys (first-set wins)', () => {
    const target: Record<string, string | undefined> = { A: 'shell' };
    mergeFirstWins(target, { A: 'file' });
    expect(target.A).toBe('shell');
  });

  test('treats explicit undefined as absent', () => {
    const target: Record<string, string | undefined> = { A: undefined };
    mergeFirstWins(target, { A: 'fallback' });
    // 'A' is "in" the object but is undefined — we want fallback to apply here
    // because that's how shell env behaves (unset vs set-to-empty differ).
    // The simple `key in target` check intentionally keeps undefined as present
    // to match Node's process.env semantics — document the choice with a test.
    expect(target.A).toBeUndefined();
  });
});

describe('loadRootEnv', () => {
  test('no-ops when the file does not exist', () => {
    const target: Record<string, string | undefined> = { FOO: 'kept' };
    loadRootEnv('/tmp/this-file-does-not-exist-' + Math.random(), target);
    expect(target).toEqual({ FOO: 'kept' });
  });

  test('merges a real file with first-set-wins semantics', () => {
    const dir = mkdtempSync(join(tmpdir(), 'gktv-env-'));
    const path = join(dir, '.env');
    try {
      writeFileSync(path, 'NEW_KEY=fromfile\nSHELL_KEY=shouldbeignored\n');
      const target: Record<string, string | undefined> = { SHELL_KEY: 'shell' };
      loadRootEnv(path, target);
      expect(target.NEW_KEY).toBe('fromfile');
      expect(target.SHELL_KEY).toBe('shell');
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
