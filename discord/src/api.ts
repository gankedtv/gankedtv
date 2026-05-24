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

// Optional per-call timeout in ms. Defaults to FETCH_TIMEOUT_MS (10s) which is
// fine for poll-loop calls but too long for slash-command autocomplete, which
// Discord deadlines at 3 seconds — overshoot and `interaction.respond` throws
// UnknownInteraction. Autocomplete handlers pass a tighter value (~2.5s).
export type RequestOptions = { timeoutMs?: number };

export type ApiClient = {
  getFeed(
    opts?: {
      cursor?: string;
      limit?: number;
      sort?: 'latest' | 'trending';
      window?: '24h' | '7d';
    } & RequestOptions,
  ): Promise<ClipFeedResponse>;
  getClipsForGame(
    slug: string,
    opts?: { limit?: number } & RequestOptions,
  ): Promise<ClipFeedResponse>;
  search(
    q: string,
    opts?: { type?: 'all' | 'clips' | 'games'; limit?: number } & RequestOptions,
  ): Promise<SearchResponse>;
  listGames(
    opts?: {
      search?: string;
      limit?: number;
      hasClips?: boolean;
    } & RequestOptions,
  ): Promise<GameListItem[]>;
};

// Caps any single API call. The bot's HTTP queries are small reads from the
// local-network GankedTV API, so 10s is generous — anything slower is either
// a deadlock or a stalled connection and should fail loudly rather than block
// the poller (which would chain into overlapping ticks). Pure HTTP timeout;
// separate from the DB statement_timeout in db.ts.
const FETCH_TIMEOUT_MS = 10_000;

export function createApi(baseUrl: string, fetchImpl: typeof fetch = fetch): ApiClient {
  const get = async <T>(
    path: string,
    query?: Record<string, string | number | undefined>,
    opts?: RequestOptions,
  ) => {
    const url = new URL(path, ensureTrailingSlash(baseUrl));
    if (query) {
      for (const [k, v] of Object.entries(query)) {
        if (v === undefined || v === null || v === '') continue;
        url.searchParams.set(k, String(v));
      }
    }
    const timeoutMs = opts?.timeoutMs ?? FETCH_TIMEOUT_MS;
    let res: Response;
    try {
      res = await fetchImpl(url, {
        headers: { accept: 'application/json' },
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (err) {
      if (err instanceof Error && err.name === 'TimeoutError') {
        throw new Error(`GET ${url.pathname} timed out after ${timeoutMs}ms`, { cause: err });
      }
      throw err;
    }
    if (!res.ok) {
      throw new Error(`GET ${url.pathname} → ${res.status}`);
    }
    return (await res.json()) as T;
  };

  return {
    getFeed: (opts = {}) =>
      get<ClipFeedResponse>(
        'clips/feed',
        { cursor: opts.cursor, limit: opts.limit, sort: opts.sort, window: opts.window },
        { timeoutMs: opts.timeoutMs },
      ),
    getClipsForGame: (slug, opts = {}) =>
      get<ClipFeedResponse>(
        `games/${encodeURIComponent(slug)}/clips`,
        { limit: opts.limit },
        { timeoutMs: opts.timeoutMs },
      ),
    search: (q, opts = {}) =>
      get<SearchResponse>(
        'search',
        { q, type: opts.type ?? 'clips', limit: opts.limit },
        { timeoutMs: opts.timeoutMs },
      ),
    listGames: (opts = {}) =>
      get<GameListItem[]>(
        'games',
        {
          search: opts.search,
          limit: opts.limit,
          hasClips: opts.hasClips ? 'true' : undefined,
        },
        { timeoutMs: opts.timeoutMs },
      ),
  };
}

function ensureTrailingSlash(s: string): string {
  return s.endsWith('/') ? s : s + '/';
}
