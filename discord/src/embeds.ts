import { EmbedBuilder } from 'discord.js';
import type { ClipFeedItem } from './api.ts';
import { shareUrl } from './lib/shareUrl.ts';
import { THUMBNAIL_FILENAME } from './lib/thumbnail.ts';

// GankedTV brand purple — keep in sync with the web theme accent if it changes.
export const BRAND_COLOR = 0x8b5cf6;

const TITLE_MAX = 256;
const DESCRIPTION_MAX = 200;

function truncate(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max - 1)}…`;
}

export function formatDuration(secs: number | null): string | null {
  if (secs === null || !Number.isFinite(secs) || secs < 0) return null;
  const whole = Math.round(secs);
  const minutes = Math.floor(whole / 60);
  const seconds = whole % 60;
  return `${minutes}:${String(seconds).padStart(2, '0')}`;
}

// One embed shape for every clip surface (poller announcements and the /clip
// commands), so a clip looks the same regardless of how it reached the channel.
// With `attachedThumbnail` the image points at the message's own attachment
// (uploaded to Discord's CDN, never expires); the raw thumbnailUrl is only the
// fallback when the download failed — it's presigned and short-lived.
export function buildClipEmbed(
  clip: ClipFeedItem,
  publicBase: string,
  opts?: { attachedThumbnail?: boolean },
): EmbedBuilder {
  const embed = new EmbedBuilder()
    .setTitle(truncate(clip.title, TITLE_MAX))
    .setURL(shareUrl(clip.shareCode, publicBase))
    .setColor(BRAND_COLOR)
    .setTimestamp(new Date(clip.createdAt))
    .setAuthor({
      name: clip.author.username,
      iconURL: clip.author.avatarUrl ?? undefined,
    });

  if (clip.description) {
    embed.setDescription(truncate(clip.description, DESCRIPTION_MAX));
  }
  if (opts?.attachedThumbnail) {
    embed.setImage(`attachment://${THUMBNAIL_FILENAME}`);
  } else if (clip.thumbnailUrl) {
    embed.setImage(clip.thumbnailUrl);
  }

  const fields: { name: string; value: string; inline: boolean }[] = [];
  if (clip.game) {
    fields.push({ name: 'Game', value: clip.game.name, inline: true });
  }
  const duration = formatDuration(clip.durationSecs);
  if (duration) {
    fields.push({ name: 'Duration', value: duration, inline: true });
  }
  fields.push(
    { name: 'Views', value: String(clip.viewCount), inline: true },
    { name: 'Likes', value: String(clip.likeCount), inline: true },
  );
  embed.addFields(fields);

  return embed;
}
