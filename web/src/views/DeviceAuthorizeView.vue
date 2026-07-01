<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { device } from '@/api/device'
import { ApiError } from '@/api/client'

const route = useRoute()

type Phase = 'enter' | 'loading' | 'confirm' | 'approved' | 'denied' | 'error'
const phase = ref<Phase>('enter')
const code = ref('')
const clientName = ref<string | null>(null)
const errorMessage = ref('')
const busy = ref(false)

onMounted(() => {
  const q = route.query.code
  if (typeof q === 'string' && q.length > 0) {
    code.value = q
    void doLookup()
  }
})

async function doLookup() {
  if (!code.value.trim()) return
  phase.value = 'loading'
  errorMessage.value = ''
  try {
    const info = await device.lookup(code.value.trim())
    if (info.status !== 'pending') {
      // Already approved/denied elsewhere, or consumed.
      phase.value = 'error'
      errorMessage.value = 'This request is no longer pending. Start again from the app.'
      return
    }
    clientName.value = info.clientName
    phase.value = 'confirm'
  } catch (err) {
    phase.value = 'error'
    errorMessage.value = mapError(err)
  }
}

async function approve() {
  busy.value = true
  try {
    await device.approve(code.value.trim())
    phase.value = 'approved'
  } catch (err) {
    phase.value = 'error'
    errorMessage.value = mapError(err)
  } finally {
    busy.value = false
  }
}

async function deny() {
  busy.value = true
  try {
    await device.deny(code.value.trim())
    phase.value = 'denied'
  } catch (err) {
    phase.value = 'error'
    errorMessage.value = mapError(err)
  } finally {
    busy.value = false
  }
}

function mapError(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 404)
      return "That code wasn't found, or it has expired. Start again from the app."
    if (err.status === 409) return 'This request was already approved or denied.'
  }
  return 'Something went wrong. Try again.'
}

const appLabel = computed(() => clientName.value || 'A device')
</script>

<template>
  <div class="mx-auto flex w-full max-w-md flex-col gap-6 px-6 py-16">
    <header>
      <h1 class="m-0 mb-1 font-heading text-[28px] font-bold uppercase text-text-primary">
        Connect a device
      </h1>
      <p class="m-0 font-body text-sm text-text-secondary">
        Authorize a desktop app to upload clips to your account.
      </p>
    </header>

    <!-- Enter code manually (when not prefilled from the app's link) -->
    <form
      v-if="phase === 'enter'"
      class="flex flex-col gap-3 rounded-lg border border-border bg-surface-raised px-6 py-6"
      @submit.prevent="doLookup"
    >
      <label class="flex flex-col gap-1.5">
        <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted">
          Enter the code shown in the app
        </span>
        <input
          v-model="code"
          type="text"
          autocomplete="off"
          placeholder="WDJB-MJHT"
          class="rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-mono text-lg uppercase tracking-widest text-text-primary outline-none focus:border-border-hover"
        />
      </label>
      <button
        type="submit"
        :disabled="!code.trim()"
        class="rounded-md bg-brand px-5 py-3 font-heading text-[14px] font-bold uppercase tracking-[0.06em] text-white transition-colors duration-150 hover:bg-brand-light disabled:cursor-not-allowed disabled:opacity-50"
      >
        Continue
      </button>
    </form>

    <p
      v-else-if="phase === 'loading'"
      class="m-0 font-mono text-[11px] tracking-wide text-text-secondary"
    >
      Checking…
    </p>

    <!-- Confirm -->
    <div
      v-else-if="phase === 'confirm'"
      class="flex flex-col gap-4 rounded-lg border border-border bg-surface-raised px-6 py-6"
    >
      <p class="m-0 font-body text-sm text-text-primary">
        <strong class="font-heading uppercase">{{ appLabel }}</strong> wants to upload clips to your
        account and act on your behalf.
      </p>
      <p
        class="m-0 rounded-md border border-border-strong bg-surface-overlay px-3 py-2 font-mono text-[11px] leading-relaxed text-text-muted"
      >
        Only approve if <em>you</em> just started this on your own device. Never approve a code
        someone else gave you — it would give them access to your account.
      </p>
      <div class="flex gap-2.5">
        <button
          type="button"
          :disabled="busy"
          class="flex-1 rounded-md bg-brand px-5 py-3 font-heading text-[14px] font-bold uppercase tracking-[0.06em] text-white transition-colors duration-150 hover:bg-brand-light disabled:opacity-50"
          @click="approve"
        >
          {{ busy ? 'Working…' : 'Approve' }}
        </button>
        <button
          type="button"
          :disabled="busy"
          class="flex-1 rounded-md border border-border-strong bg-transparent px-5 py-3 font-heading text-[14px] font-bold uppercase tracking-[0.06em] text-text-secondary transition-colors duration-150 hover:border-error hover:text-error disabled:opacity-50"
          @click="deny"
        >
          Deny
        </button>
      </div>
    </div>

    <div
      v-else-if="phase === 'approved'"
      class="flex flex-col gap-2 rounded-lg border border-neon bg-surface-raised px-6 py-6"
    >
      <p class="m-0 font-heading text-lg font-bold uppercase text-neon">Device connected</p>
      <p class="m-0 font-body text-sm text-text-secondary">
        You can return to the app — it's ready to upload. Manage or revoke this connection anytime
        under Connected apps.
      </p>
    </div>

    <div
      v-else-if="phase === 'denied'"
      class="flex flex-col gap-2 rounded-lg border border-border bg-surface-raised px-6 py-6"
    >
      <p class="m-0 font-heading text-lg font-bold uppercase text-text-primary">Request denied</p>
      <p class="m-0 font-body text-sm text-text-secondary">
        The app was not granted access. You can close this page.
      </p>
    </div>

    <div
      v-else
      class="flex flex-col gap-3 rounded-lg border border-border bg-surface-raised px-6 py-6"
    >
      <p class="m-0 font-mono text-[11px] tracking-wide text-error" role="alert">
        {{ errorMessage }}
      </p>
      <button
        type="button"
        class="self-start rounded-md border border-border-strong bg-transparent px-4 py-2 font-heading text-[12px] font-bold uppercase tracking-[0.06em] text-text-secondary hover:border-border-hover hover:text-text-primary"
        @click="phase = 'enter'"
      >
        Enter a code
      </button>
    </div>
  </div>
</template>
