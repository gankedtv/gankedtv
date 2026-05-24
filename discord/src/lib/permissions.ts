import { PermissionFlagsBits, type ChatInputCommandInteraction } from 'discord.js';

// Subscribe/unsubscribe/pause/resume are channel-scoped admin actions; gating them
// to ManageChannels stops random members from reconfiguring server-wide clip posts.
// Listing + /clip * are open (return true).
export function requireManageChannels(interaction: ChatInputCommandInteraction): boolean {
  if (!interaction.inGuild()) return false;
  const perms = interaction.memberPermissions;
  if (!perms) return false;
  return perms.has(PermissionFlagsBits.ManageChannels);
}
