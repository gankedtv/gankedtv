import { describe, expect, test } from 'bun:test';
import { BRAND_COLOR, buildClipEmbed, formatDuration } from '../src/embeds.ts';
import { clip } from './factories.ts';

describe('formatDuration', () => {
  test('formats mm:ss with zero-padded seconds', () => {
    expect(formatDuration(0)).toBe('0:00');
    expect(formatDuration(9)).toBe('0:09');
    expect(formatDuration(65)).toBe('1:05');
    expect(formatDuration(600)).toBe('10:00');
  });

  test('rounds fractional seconds', () => {
    expect(formatDuration(29.6)).toBe('0:30');
  });

  test('returns null for null, negative, and non-finite input', () => {
    expect(formatDuration(null)).toBeNull();
    expect(formatDuration(-1)).toBeNull();
    expect(formatDuration(Number.NaN)).toBeNull();
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBeNull();
  });
});

describe('buildClipEmbed', () => {
  test('fully-populated clip maps every field', () => {
    const c = clip({
      shareCode: 'full1',
      title: 'Insane 1v5',
      description: 'He really did that.',
      durationSecs: 42,
      viewCount: 1234,
      likeCount: 56,
      createdAt: '2026-06-01T12:00:00.000Z',
      author: { id: 'a1', username: 'gamer', avatarUrl: 'https://cdn.test/a.png' },
      game: { id: 1, name: 'Valorant', slug: 'valorant', tag: 'VAL' },
    });

    const e = buildClipEmbed(c, 'https://gankedtv.com').toJSON();

    expect(e.title).toBe('Insane 1v5');
    expect(e.url).toBe('https://gankedtv.com/c/full1');
    expect(e.description).toBe('He really did that.');
    expect(e.color).toBe(BRAND_COLOR);
    expect(e.timestamp).toBe('2026-06-01T12:00:00.000Z');
    expect(e.author?.name).toBe('gamer');
    expect(e.author?.icon_url).toBe('https://cdn.test/a.png');
    expect(e.image?.url).toBe(c.thumbnailUrl);
    expect(e.fields).toEqual([
      { name: 'Game', value: 'Valorant', inline: true },
      { name: 'Duration', value: '0:42', inline: true },
      { name: 'Views', value: '1234', inline: true },
      { name: 'Likes', value: '56', inline: true },
    ]);
  });

  test('null description, game, duration, and avatar are omitted', () => {
    const c = clip({
      description: null,
      durationSecs: null,
      game: null,
      author: { id: 'a2', username: 'anon', avatarUrl: null },
    });

    const e = buildClipEmbed(c, 'https://gankedtv.com').toJSON();

    expect(e.description).toBeUndefined();
    expect(e.author?.name).toBe('anon');
    expect(e.author?.icon_url).toBeUndefined();
    expect(e.fields?.map((f) => f.name)).toEqual(['Views', 'Likes']);
  });

  test('overlong title and description are truncated with an ellipsis', () => {
    const c = clip({
      title: 'T'.repeat(300),
      description: 'D'.repeat(300),
    });

    const e = buildClipEmbed(c, 'https://gankedtv.com').toJSON();

    expect(e.title!.length).toBe(256);
    expect(e.title!.endsWith('…')).toBe(true);
    expect(e.description!.length).toBe(200);
    expect(e.description!.endsWith('…')).toBe(true);
  });

  test('empty thumbnail URL is omitted', () => {
    const c = clip({ thumbnailUrl: '' });
    const e = buildClipEmbed(c, 'https://gankedtv.com').toJSON();
    expect(e.image).toBeUndefined();
  });
});
