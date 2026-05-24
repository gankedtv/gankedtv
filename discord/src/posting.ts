import type { ClipFeedItem } from './api.ts';
import type { Subscription } from './db.ts';
import { shareUrl } from './lib/shareUrl.ts';

// Discord auto-unfurls the share URL into an embed (OG meta tags from
// ClipsReadEndpoints.GetByShareCode). All we have to do is post a plain message
// with the URL — no manual EmbedBuilder, no thumbnail download, no S3 round-trip.
// If the subscription has a ping role, prepend a role mention.
export function buildMessage(
  clip: ClipFeedItem,
  sub: Pick<Subscription, 'pingRoleId'>,
  publicBase: string,
): string {
  const url = shareUrl(clip.shareCode, publicBase);
  return sub.pingRoleId ? `<@&${sub.pingRoleId}> ${url}` : url;
}

// Minimal duck-typed sendable: anything with a `send` method that accepts a
// content payload. Lets the poller pass a real discord.js TextChannel while
// tests can pass a fake — the caller has already narrowed via isTextBased().
export type Sendable = {
  send(payload: {
    content: string;
    allowedMentions: { parse: ('roles' | 'users' | 'everyone')[] };
  }): Promise<unknown>;
};

export async function postToChannel(channel: Sendable | null, content: string): Promise<boolean> {
  if (!channel) return false;
  await channel.send({ content, allowedMentions: { parse: ['roles'] } });
  return true;
}
