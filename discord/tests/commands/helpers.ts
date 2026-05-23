import { mock } from 'bun:test';
import type {
  AutocompleteInteraction,
  ChatInputCommandInteraction,
  PermissionsBitField,
} from 'discord.js';
import { PermissionFlagsBits } from 'discord.js';
import type {
  ApiClient,
  ClipFeedItem,
  GameListItem,
  SearchResponse,
  ClipFeedResponse,
} from '../../src/api.ts';
import type { CommandContext } from '../../src/commands/index.ts';
import type { Db, Subscription } from '../../src/db.ts';

// Hand-rolled mock interaction. We mock only the surface the command handlers
// actually touch, so a discord.js version bump that adds new methods doesn't
// silently make these tests pass for the wrong reason.
export type FakeChatInputOpts = {
  subcommand: string;
  strings?: Record<string, string | null>;
  roles?: Record<string, { id: string } | null>;
  guildId?: string | null;
  channelId?: string;
  userId?: string;
  hasManageChannels?: boolean;
};

export function fakeChatInput(opts: FakeChatInputOpts) {
  const replies: { phase: 'reply' | 'editReply'; payload: unknown }[] = [];
  let deferred = false;

  const memberPermissions =
    opts.hasManageChannels === false
      ? ({
          has: (flag: bigint) => flag !== PermissionFlagsBits.ManageChannels,
        } as unknown as PermissionsBitField)
      : ({ has: () => true } as unknown as PermissionsBitField);

  const interaction = {
    commandName: 'test',
    guildId: opts.guildId ?? 'guild-1',
    channelId: opts.channelId ?? 'channel-1',
    user: { id: opts.userId ?? 'user-1' },
    memberPermissions,
    replied: false,
    inGuild: () => opts.guildId !== null,
    isRepliable: () => true,
    options: {
      getSubcommand: () => opts.subcommand,
      getString: (name: string, required?: boolean) => {
        const v = opts.strings?.[name] ?? null;
        if (required && v === null) throw new Error(`required string '${name}' missing`);
        return v;
      },
      getRole: (name: string) => opts.roles?.[name] ?? null,
    },
    deferReply: mock(async (_opts?: unknown) => {
      deferred = true;
      return undefined;
    }),
    editReply: mock(async (payload: unknown) => {
      replies.push({ phase: 'editReply', payload });
      return undefined;
    }),
    reply: mock(async (payload: unknown) => {
      interaction.replied = true;
      replies.push({ phase: 'reply', payload });
      return undefined;
    }),
  };

  return {
    interaction: interaction as unknown as ChatInputCommandInteraction,
    replies,
    wasDeferred: () => deferred,
  };
}

export function fakeAutocomplete(focused: { name: string; value: string }) {
  const responses: unknown[] = [];
  const interaction = {
    commandName: 'test',
    options: {
      getFocused: () => focused,
    },
    respond: mock(async (choices: unknown) => {
      responses.push(choices);
      return undefined;
    }),
  };
  return {
    interaction: interaction as unknown as AutocompleteInteraction,
    responses,
  };
}

export function fakeApi(overrides: Partial<ApiClient> = {}): ApiClient {
  const empty: ClipFeedResponse = { items: [], nextCursor: null };
  return {
    getFeed: async () => empty,
    getClipsForGame: async () => empty,
    search: async (): Promise<SearchResponse> => ({ clips: [], games: [] }),
    listGames: async (): Promise<GameListItem[]> => [],
    ...overrides,
  };
}

export function fakeDb(overrides: Partial<Db> = {}): Db {
  return {
    sql: null as never,
    async close() {},
    async addSubscription() {
      return null;
    },
    async removeSubscription() {
      return 0;
    },
    async listSubscriptionsForChannel() {
      return [] as Subscription[];
    },
    async listAllSubscriptions() {
      return [];
    },
    async setPaused() {
      return 0;
    },
    async isPosted() {
      return false;
    },
    async recordPost() {},
    async getState() {
      return null;
    },
    async setState() {},
    ...overrides,
  };
}

export function ctx(over: Partial<CommandContext> = {}): CommandContext {
  return {
    db: over.db ?? fakeDb(),
    api: over.api ?? fakeApi(),
    publicBase: over.publicBase ?? 'https://gankedtv.com',
  };
}

export function asClipFeedItem(c: ClipFeedItem): ClipFeedItem {
  return c;
}
