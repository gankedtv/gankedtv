import type { APIEmbed } from 'discord.js';
import type { ClipFeedItem } from './api.ts';
import type { Subscription } from './db.ts';
import { buildClipEmbed } from './embeds.ts';

// Self-built embed instead of relying on Discord auto-unfurling the share URL:
// unfurls on bot-authored messages are inconsistent, and the OG images are
// presigned URLs Discord's crawler may fetch too late. The share URL lives in
// the embed title link only — putting it in `content` too would trigger a
// second, competing auto-unfurl embed.
export type ClipMessage = {
  content?: string;
  embeds: APIEmbed[];
};

export function buildMessage(
  clip: ClipFeedItem,
  sub: Pick<Subscription, 'pingRoleId'>,
  publicBase: string,
): ClipMessage {
  const embeds = [buildClipEmbed(clip, publicBase).toJSON()];
  // The role mention must stay in `content` — mentions inside embeds don't ping.
  return sub.pingRoleId ? { content: `<@&${sub.pingRoleId}>`, embeds } : { embeds };
}

// Minimal duck-typed sendable: anything with a `send` method that accepts a
// message payload. Lets the poller pass a real discord.js TextChannel while
// tests can pass a fake — the caller has already narrowed via isTextBased().
export type Sendable = {
  send(
    payload: ClipMessage & {
      allowedMentions: { parse: ('roles' | 'users' | 'everyone')[] };
    },
  ): Promise<unknown>;
};

export async function postToChannel(
  channel: Sendable | null,
  message: ClipMessage,
): Promise<boolean> {
  if (!channel) return false;
  await channel.send({ ...message, allowedMentions: { parse: ['roles'] } });
  return true;
}
