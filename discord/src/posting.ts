import type { APIEmbed } from 'discord.js';
import type { ClipFeedItem } from './api.ts';
import type { Subscription } from './db.ts';
import { buildClipEmbed } from './embeds.ts';
import { THUMBNAIL_FILENAME } from './lib/thumbnail.ts';

// Self-built embed instead of relying on Discord auto-unfurling the share URL:
// unfurls on bot-authored messages are inconsistent, and the OG images are
// presigned URLs Discord's crawler may fetch too late. The share URL lives in
// the embed title link only — putting it in `content` too would trigger a
// second, competing auto-unfurl embed.
export type ClipMessage = {
  content?: string;
  embeds: APIEmbed[];
  files?: { attachment: Buffer; name: string }[];
};

// Shared embed + attachment payload for every clip surface. A non-null
// `thumbnail` rides along as a file upload and the embed points at it.
export function buildClipPayload(
  clip: ClipFeedItem,
  publicBase: string,
  thumbnail: Buffer | null = null,
): Pick<ClipMessage, 'embeds' | 'files'> {
  const embeds = [
    buildClipEmbed(clip, publicBase, { attachedThumbnail: thumbnail !== null }).toJSON(),
  ];
  return thumbnail
    ? { embeds, files: [{ attachment: thumbnail, name: THUMBNAIL_FILENAME }] }
    : { embeds };
}

export function buildMessage(
  clip: ClipFeedItem,
  sub: Pick<Subscription, 'pingRoleId'>,
  publicBase: string,
  thumbnail: Buffer | null = null,
): ClipMessage {
  const payload = buildClipPayload(clip, publicBase, thumbnail);
  // The role mention must stay in `content` — mentions inside embeds don't ping.
  return sub.pingRoleId ? { content: `<@&${sub.pingRoleId}>`, ...payload } : { ...payload };
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
