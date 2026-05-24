import type {
  AutocompleteInteraction,
  ChatInputCommandInteraction,
  RESTPostAPIChatInputApplicationCommandsJSONBody,
} from 'discord.js';
import type { ApiClient } from '../api.ts';
import type { Db } from '../db.ts';
import * as gankedtv from './gankedtv.ts';
import * as clipCommand from './clip.ts';
import { ephemeral } from './replies.ts';

export type CommandContext = {
  db: Db;
  api: ApiClient;
  publicBase: string;
};

// SlashCommandBuilder + .addSubcommand() chains narrow to SlashCommandSubcommandsOnlyBuilder.
// The only thing the registry actually needs is name + toJSON(), so we type the
// data field by structural shape instead of binding to any one builder subtype.
export type CommandData = {
  readonly name: string;
  toJSON(): RESTPostAPIChatInputApplicationCommandsJSONBody;
};

export type Command = {
  data: CommandData;
  execute(interaction: ChatInputCommandInteraction, ctx: CommandContext): Promise<void>;
  autocomplete?(interaction: AutocompleteInteraction, ctx: CommandContext): Promise<void>;
};

export const commands: Record<string, Command> = {
  gankedtv: gankedtv.command,
  clip: clipCommand.command,
};

export function commandDefinitions(): RESTPostAPIChatInputApplicationCommandsJSONBody[] {
  return Object.values(commands).map((c) => c.data.toJSON());
}

export async function dispatchChatInput(
  interaction: ChatInputCommandInteraction,
  ctx: CommandContext,
): Promise<void> {
  const cmd = commands[interaction.commandName];
  if (!cmd) {
    await interaction.reply(ephemeral('Unknown command.'));
    return;
  }
  await cmd.execute(interaction, ctx);
}

export async function dispatchAutocomplete(
  interaction: AutocompleteInteraction,
  ctx: CommandContext,
): Promise<void> {
  const cmd = commands[interaction.commandName];
  if (!cmd?.autocomplete) return;
  await cmd.autocomplete(interaction, ctx);
}
