import type { ClipFeedItem } from '../src/api.ts';
import type { Subscription } from '../src/db.ts';

let clipSeq = 0;
let subSeq = 0;

export const clip = (overrides: Partial<ClipFeedItem> = {}): ClipFeedItem => {
  const n = ++clipSeq;
  return {
    id: `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`,
    shareCode: `code${String(n).padStart(4, '0')}`,
    title: `Clip ${n}`,
    description: null,
    thumbnailUrl: `https://example.test/thumb/${n}.jpg`,
    durationSecs: 30,
    viewCount: 0,
    likeCount: 0,
    createdAt: new Date(2026, 0, 1, 0, 0, n).toISOString(),
    author: {
      id: `11111111-0000-0000-0000-${String(n).padStart(12, '0')}`,
      username: `user${n}`,
      avatarUrl: null,
    },
    game: { id: 100 + n, name: `Game ${n}`, slug: `game-${n}`, tag: `g${n}` },
    tags: [],
    likedByMe: false,
    ...overrides,
  };
};

export const subscription = (overrides: Partial<Subscription> = {}): Subscription => {
  const n = ++subSeq;
  return {
    id: `22222222-0000-0000-0000-${String(n).padStart(12, '0')}`,
    guildId: `guild-${n}`,
    channelId: `channel-${n}`,
    gameId: null,
    creatorId: null,
    paused: false,
    pingRoleId: null,
    createdAt: new Date(2026, 0, 1, 0, 0, n),
    createdBy: `user-${n}`,
    ...overrides,
  };
};
