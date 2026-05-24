import type { ClipFeedItem } from './api.ts';
import type { Subscription } from './db.ts';

// Does this subscription want this clip? Three-way match:
//   - paused subs never match (filtered upstream in listAllSubscriptions, but defended here)
//   - NULL game_id means "any game"; otherwise the clip's game.id must equal it
//   - NULL creator_id means "any creator"; otherwise clip.author.id must equal it
// Returns boolean; pure function for easy testing.
export function subscriptionMatchesClip(sub: Subscription, clip: ClipFeedItem): boolean {
  if (sub.paused) return false;
  if (sub.gameId !== null && (clip.game?.id ?? null) !== sub.gameId) return false;
  if (sub.creatorId !== null && clip.author.id !== sub.creatorId) return false;
  return true;
}

// For one clip, find every subscription that wants it. Used by the poller fanout.
export function matchingSubscriptions<S extends Subscription>(
  subscriptions: readonly S[],
  clip: ClipFeedItem,
): S[] {
  return subscriptions.filter((s) => subscriptionMatchesClip(s, clip));
}
