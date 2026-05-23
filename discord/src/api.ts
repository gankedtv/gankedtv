// Thin HTTP client for the GankedTV REST API. Mirrors only the read endpoints
// the bot needs — the bot never writes through the API. Contracts mirror the
// server-side records in server/src/GankedTV.Api/Contracts/ (camelCase per the
// minimal-API default System.Text.Json policy).

export type ClipFeedItem = {
  id: string;
  shareCode: string;
  title: string;
  description: string | null;
  thumbnailUrl: string;
  durationSecs: number | null;
  viewCount: number;
  likeCount: number;
  createdAt: string;
  author: { id: string; username: string; avatarUrl: string | null };
  game: { id: number; name: string; slug: string; tag: string } | null;
  tags: { id: string; name: string; slug: string }[];
  likedByMe: boolean;
};

export type ClipFeedResponse = {
  items: ClipFeedItem[];
  nextCursor: string | null;
};

export type GameListItem = {
  id: number;
  name: string;
  slug: string;
  tag: string;
  coverUrl: string | null;
};

export type SearchResponse = {
  clips: ClipFeedItem[];
  games: GameListItem[];
};

export type ApiClient = {
  getFeed(opts?: {
    cursor?: string;
    limit?: number;
    sort?: 'latest' | 'trending';
    window?: '24h' | '7d';
  }): Promise<ClipFeedResponse>;
  getClipsForGame(slug: string, opts?: { limit?: number }): Promise<ClipFeedResponse>;
  search(
    q: string,
    opts?: { type?: 'all' | 'clips' | 'games'; limit?: number },
  ): Promise<SearchResponse>;
  listGames(opts?: {
    search?: string;
    limit?: number;
    hasClips?: boolean;
  }): Promise<GameListItem[]>;
};

export function createApi(baseUrl: string): ApiClient {
  const get = async <T>(path: string, query?: Record<string, string | number | undefined>) => {
    const url = new URL(path, ensureTrailingSlash(baseUrl));
    if (query) {
      for (const [k, v] of Object.entries(query)) {
        if (v === undefined || v === null || v === '') continue;
        url.searchParams.set(k, String(v));
      }
    }
    const res = await fetch(url, { headers: { accept: 'application/json' } });
    if (!res.ok) {
      throw new Error(`GET ${url.pathname} → ${res.status}`);
    }
    return (await res.json()) as T;
  };

  return {
    getFeed: (opts = {}) =>
      get<ClipFeedResponse>('clips/feed', {
        cursor: opts.cursor,
        limit: opts.limit,
        sort: opts.sort,
        window: opts.window,
      }),
    getClipsForGame: (slug, opts = {}) =>
      get<ClipFeedResponse>(`games/${encodeURIComponent(slug)}/clips`, {
        limit: opts.limit,
      }),
    search: (q, opts = {}) =>
      get<SearchResponse>('search', {
        q,
        type: opts.type ?? 'clips',
        limit: opts.limit,
      }),
    listGames: (opts = {}) =>
      get<GameListItem[]>('games', {
        search: opts.search,
        limit: opts.limit,
        hasClips: opts.hasClips ? 'true' : undefined,
      }),
  };
}

function ensureTrailingSlash(s: string): string {
  return s.endsWith('/') ? s : s + '/';
}
