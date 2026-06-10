import { MessageFlags } from 'discord.js';

// Shared shape for ephemeral interaction replies. Centralised so the
// `MessageFlags.Ephemeral as MessageFlags.Ephemeral` literal-widening cast
// (a TS narrowing quirk — see commands/gankedtv.ts history) lives in exactly
// one place, and so adding a new ephemeral reply elsewhere doesn't drift the
// flag set out of sync.
export const ephemeral = (content: string) => ({
  content,
  flags: MessageFlags.Ephemeral as MessageFlags.Ephemeral,
});

type Deferrable = {
  deferReply(opts?: { flags?: MessageFlags.Ephemeral }): Promise<unknown>;
};

// deferReply can fail (expired interaction token, revoked perms mid-flight);
// proceeding would make the later editReply throw InteractionAlreadyReplied or
// UnknownInteraction. Handlers bail when this returns false — there is nothing
// useful left to do with a dead interaction.
export async function safeDefer(
  interaction: Deferrable,
  opts?: { flags?: MessageFlags.Ephemeral },
): Promise<boolean> {
  try {
    await interaction.deferReply(opts);
    return true;
  } catch {
    return false;
  }
}
