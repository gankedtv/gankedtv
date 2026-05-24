// Build the public share URL for a clip. The /c/{code} route serves OG/Twitter
// meta tags (ClipsReadEndpoints.GetByShareCode) so any Discord-supported client
// auto-unfurls into title + thumbnail + video player. The bot's only job is to
// emit this URL — Discord handles the rest.
export function shareUrl(code: string, publicBase: string): string {
  const trimmed = publicBase.endsWith('/') ? publicBase.slice(0, -1) : publicBase;
  return `${trimmed}/c/${code}`;
}
