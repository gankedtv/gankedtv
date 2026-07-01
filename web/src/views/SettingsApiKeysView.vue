<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { storeToRefs } from 'pinia'
import { useApiKeysStore } from '@/stores/apiKeys'
import type { ApiKeyItem } from '@/api/apiKeys'
import ConfirmDialog from '@/components/ConfirmDialog.vue'

const store = useApiKeysStore()
const { items, loading, error } = storeToRefs(store)

const pendingRevoke = ref<ApiKeyItem | null>(null)
const revoking = ref(false)

onMounted(() => store.load())

async function confirmRevoke() {
  if (!pendingRevoke.value) return
  revoking.value = true
  await store.revoke(pendingRevoke.value.id)
  revoking.value = false
  pendingRevoke.value = null
}

function status(k: ApiKeyItem): 'revoked' | 'expired' | 'active' {
  if (k.revokedAt) return 'revoked'
  if (k.expiresAt && new Date(k.expiresAt) <= new Date()) return 'expired'
  return 'active'
}

function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}
</script>

<template>
  <div class="mx-auto flex w-full max-w-2xl flex-col gap-6 px-6 py-10">
    <header>
      <h1 class="m-0 mb-1 font-heading text-[28px] font-bold uppercase text-text-primary">
        Connected apps
      </h1>
      <p class="m-0 font-body text-sm text-text-secondary">
        Desktop apps you've authorized (like rewynd) can upload clips on your behalf. Sign in from
        the app itself to connect it; revoke access here anytime.
      </p>
    </header>

    <section class="flex flex-col gap-2">
      <p v-if="loading" class="m-0 font-mono text-[11px] tracking-wide text-text-secondary">
        Loading…
      </p>
      <p
        v-else-if="items.length === 0"
        class="m-0 font-mono text-[11px] tracking-wide text-text-secondary"
      >
        No connected apps yet.
      </p>
      <ul v-else class="m-0 flex list-none flex-col gap-2 p-0">
        <li
          v-for="k in items"
          :key="k.id"
          class="flex items-center justify-between gap-3 rounded-md border border-border bg-surface-raised px-4 py-3"
        >
          <div class="flex min-w-0 flex-col gap-1">
            <span class="truncate font-body text-sm text-text-primary">
              {{ k.name || 'Unnamed app' }}
              <span
                v-if="status(k) !== 'active'"
                class="ml-1 font-mono text-[10px] uppercase tracking-widest text-error"
              >
                {{ status(k) }}
              </span>
            </span>
            <span class="font-mono text-[11px] text-text-muted">
              {{ k.keyPrefix }}… · connected {{ formatDate(k.createdAt) }} · last used
              {{ formatDate(k.lastUsedAt) }}
              <template v-if="k.expiresAt"> · expires {{ formatDate(k.expiresAt) }}</template>
            </span>
          </div>
          <button
            v-if="status(k) !== 'revoked'"
            type="button"
            class="shrink-0 cursor-pointer rounded-md border border-border-strong bg-transparent px-3 py-1.5 font-heading text-[11px] font-bold uppercase tracking-[0.06em] text-error transition-colors duration-150 hover:border-error"
            @click="pendingRevoke = k"
          >
            Revoke
          </button>
        </li>
      </ul>
      <p v-if="error" class="m-0 font-mono text-[11px] tracking-wide text-error" role="alert">
        {{ error }}
      </p>
    </section>

    <ConfirmDialog
      :open="pendingRevoke !== null"
      title="Revoke access?"
      :body="`This immediately disconnects '${pendingRevoke?.name || 'this app'}'. It will need to sign in again to upload. This can't be undone.`"
      confirm-label="Revoke"
      variant="danger"
      :busy="revoking"
      @confirm="confirmRevoke"
      @cancel="pendingRevoke = null"
    />
  </div>
</template>
