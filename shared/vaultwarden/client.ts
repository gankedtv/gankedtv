// Shared Vaultwarden-API client for the TypeScript consumers — the Discord bot (startup) and the
// web build (prebuild). Pure and dependency-free so it runs under both Bun and Node. The contract
// (collection selection, env-wins, sequential fetch) is documented in DEPLOYMENT.md.

/**
 * Explicit `VAULTWARDEN_COLLECTION` wins; otherwise Production (`ASPNETCORE_ENVIRONMENT`, falling
 * back to `NODE_ENV`) maps to `"Secrets - PROD"` and anything else to `"Secrets - DEV"`.
 */
export function resolveCollection(env: Record<string, string | undefined>): string {
  const explicit = env.VAULTWARDEN_COLLECTION?.trim();
  if (explicit) return explicit;
  const e = env.ASPNETCORE_ENVIRONMENT ?? env.NODE_ENV;
  return e?.toLowerCase() === 'production' ? 'Secrets - PROD' : 'Secrets - DEV';
}

export interface FetchSecretsOptions {
  /** Base URL of the Vaultwarden-API (trailing slash optional). */
  apiUrl: string;
  /** Bearer token (the `secrets@` service user's API key). */
  apiKey: string;
  /** Collection to scope the fetch to (see {@link resolveCollection}). */
  collection: string;
  /** Exact secret names to fetch — never enumerates the whole vault. */
  manifest: readonly string[];
  /** Vaultwarden organization. Defaults to `GankedTV`. */
  organization?: string;
  /** Throw when a manifest secret is absent (404). Default false (skip — caller falls back). */
  throwIfMissing?: boolean;
  /** Throw on a non-404 / transport / timeout error. Default false (skip). */
  throwOnError?: boolean;
  /** Skip the request for a key already provided (env wins) — saves a call and survives a down vault. */
  alreadySet?: (key: string) => boolean;
  /** Injectable for tests. Defaults to the global `fetch`. */
  fetchImpl?: typeof fetch;
  /** Per-request timeout. Defaults to 10s. */
  timeoutMs?: number;
}

/**
 * Fetches the manifest sequentially, scoped to org + collection, and returns the `{ key: value }`
 * map for the caller to apply. Keys where `alreadySet` returns true are skipped. A 404 throws only
 * when `throwIfMissing`; a non-2xx / transport error throws only when `throwOnError`; else skipped.
 */
export async function fetchSecrets(opts: FetchSecretsOptions): Promise<Record<string, string>> {
  const fetchImpl = opts.fetchImpl ?? fetch;
  const organization = opts.organization ?? 'GankedTV';
  const timeoutMs = opts.timeoutMs ?? 10_000;
  const throwIfMissing = opts.throwIfMissing ?? false;
  const throwOnError = opts.throwOnError ?? false;
  const base = opts.apiUrl.replace(/\/+$/, '');
  const out: Record<string, string> = {};

  for (const name of opts.manifest) {
    if (opts.alreadySet?.(name)) continue; // already provided → env wins, no request

    const url =
      `${base}/secret/${encodeURIComponent(name)}` +
      `?organization_name=${encodeURIComponent(organization)}` +
      `&collection_name=${encodeURIComponent(opts.collection)}`;

    let res: Response;
    try {
      res = await fetchImpl(url, {
        headers: { authorization: `Bearer ${opts.apiKey}`, accept: 'application/json' },
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (err) {
      if (throwOnError) {
        throw new Error(`Vaultwarden: failed to fetch ${name} from ${opts.collection}`, { cause: err });
      }
      continue;
    }

    if (res.status === 404) {
      if (throwIfMissing) {
        throw new Error(`Vaultwarden: required secret ${name} not found in ${opts.collection}`);
      }
      continue;
    }
    if (!res.ok) {
      if (throwOnError) {
        throw new Error(`Vaultwarden: ${name} → ${res.status}`);
      }
      continue;
    }

    let body: { name?: string; value?: string };
    try {
      body = (await res.json()) as { name?: string; value?: string };
    } catch (err) {
      if (throwOnError) {
        throw new Error(`Vaultwarden: invalid JSON for ${name} from ${opts.collection}`, { cause: err });
      }
      continue;
    }
    if (body.value) out[name] = body.value;
  }

  return out;
}
