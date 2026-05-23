import { describe, expect, test } from 'bun:test';
import { matchingSubscriptions, subscriptionMatchesClip } from '../src/filters.ts';
import { clip, subscription } from './factories.ts';

describe('subscriptionMatchesClip', () => {
  test('no-filter subscription matches any clip', () => {
    const sub = subscription();
    const c = clip();
    expect(subscriptionMatchesClip(sub, c)).toBe(true);
  });

  test('paused subscription never matches', () => {
    const sub = subscription({ paused: true });
    const c = clip();
    expect(subscriptionMatchesClip(sub, c)).toBe(false);
  });

  test('game filter blocks non-matching game', () => {
    const sub = subscription({ gameId: 999 });
    const c = clip({ game: { id: 1, name: 'X', slug: 'x', tag: 'x' } });
    expect(subscriptionMatchesClip(sub, c)).toBe(false);
  });

  test('game filter matches when ids align', () => {
    const sub = subscription({ gameId: 42 });
    const c = clip({ game: { id: 42, name: 'X', slug: 'x', tag: 'x' } });
    expect(subscriptionMatchesClip(sub, c)).toBe(true);
  });

  test('game filter blocks clips with no game when filter is set', () => {
    const sub = subscription({ gameId: 1 });
    const c = clip({ game: null });
    expect(subscriptionMatchesClip(sub, c)).toBe(false);
  });

  test('creator filter blocks non-matching creator', () => {
    const sub = subscription({ creatorId: '33333333-0000-0000-0000-000000000001' });
    const c = clip({
      author: { id: '44444444-0000-0000-0000-000000000001', username: 'x', avatarUrl: null },
    });
    expect(subscriptionMatchesClip(sub, c)).toBe(false);
  });

  test('creator filter matches when ids align', () => {
    const userId = '33333333-0000-0000-0000-000000000001';
    const sub = subscription({ creatorId: userId });
    const c = clip({ author: { id: userId, username: 'x', avatarUrl: null } });
    expect(subscriptionMatchesClip(sub, c)).toBe(true);
  });

  test('game AND creator filters both required', () => {
    const userId = '33333333-0000-0000-0000-000000000001';
    const sub = subscription({ gameId: 7, creatorId: userId });
    const matching = clip({
      game: { id: 7, name: 'X', slug: 'x', tag: 'x' },
      author: { id: userId, username: 'x', avatarUrl: null },
    });
    const wrongGame = clip({
      game: { id: 8, name: 'X', slug: 'x', tag: 'x' },
      author: { id: userId, username: 'x', avatarUrl: null },
    });
    expect(subscriptionMatchesClip(sub, matching)).toBe(true);
    expect(subscriptionMatchesClip(sub, wrongGame)).toBe(false);
  });
});

describe('matchingSubscriptions', () => {
  test('returns only subscriptions whose filters allow the clip', () => {
    const c = clip({
      game: { id: 5, name: 'X', slug: 'x', tag: 'x' },
      author: { id: '55555555-0000-0000-0000-000000000001', username: 'x', avatarUrl: null },
    });
    const subs = [
      subscription(),
      subscription({ gameId: 5 }),
      subscription({ gameId: 6 }),
      subscription({ paused: true }),
    ];
    const matches = matchingSubscriptions(subs, c);
    expect(matches).toHaveLength(2);
    expect(matches[0]).toBe(subs[0]!);
    expect(matches[1]).toBe(subs[1]!);
  });
});
