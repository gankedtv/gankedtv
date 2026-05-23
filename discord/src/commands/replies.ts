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
