import { describe, expect, test } from 'bun:test';
import { MessageFlags } from 'discord.js';
import { ephemeral, safeDefer } from '../src/commands/replies.ts';

describe('ephemeral', () => {
  test('wraps content with the ephemeral flag', () => {
    expect(ephemeral('hi')).toEqual({ content: 'hi', flags: MessageFlags.Ephemeral });
  });
});

describe('safeDefer', () => {
  test('returns true and forwards options when deferReply succeeds', async () => {
    const calls: unknown[] = [];
    const interaction = {
      deferReply: async (opts?: unknown) => {
        calls.push(opts);
      },
    };

    const ok = await safeDefer(interaction, { flags: MessageFlags.Ephemeral });

    expect(ok).toBe(true);
    expect(calls).toEqual([{ flags: MessageFlags.Ephemeral }]);
  });

  test('returns false when deferReply throws (expired interaction token)', async () => {
    const interaction = {
      deferReply: async () => {
        throw new Error('Unknown interaction');
      },
    };

    expect(await safeDefer(interaction)).toBe(false);
  });
});
