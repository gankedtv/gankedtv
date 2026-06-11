<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { config } from '@/config'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const status = ref<'loading' | 'error'>('loading')

onMounted(async () => {
  const token = route.query.token as string | undefined
  const refresh = route.query.refresh as string | undefined

  let returnTo = '/'
  const rawReturnTo = route.query.returnTo
  if (
    typeof rawReturnTo === 'string' &&
    rawReturnTo.startsWith('/') &&
    !rawReturnTo.startsWith('//')
  ) {
    returnTo = rawReturnTo
  }

  // Cookie mode: the server set the HttpOnly refresh cookie during the callback redirect
  // and deliberately omits the refresh query param, so only the access token is required.
  if (!token || (!refresh && !config.useSecureCookies)) {
    status.value = 'error'
    await router.replace({ name: 'login', query: { error: 'oauth_failed' } })
    return
  }

  try {
    auth.setSession(token, refresh ?? '')
    await auth.fetchMe()
    await router.replace(returnTo)
  } catch {
    status.value = 'error'
    auth.logout()
    await router.replace({ name: 'login', query: { error: 'oauth_failed' } })
  }
})
</script>

<template>
  <div class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-6">
    <div class="flex flex-col items-center gap-3 text-center">
      <p class="m-0 font-mono text-[10px] uppercase tracking-[0.22em] text-ink">Authenticating</p>
      <h1 class="m-0 font-heading text-3xl font-bold uppercase leading-none tracking-[0.02em] text-text-primary">
        One Moment
      </h1>
      <span class="block h-1.5 w-5.5 overflow-hidden bg-surface-raised" aria-hidden="true">
        <span class="block h-full w-full origin-left bg-ink animate-[tick_1.6s_ease-in-out_infinite]"></span>
      </span>
      <p class="m-0 font-mono text-[11px] uppercase tracking-widest text-text-muted">
        {{ status === 'loading' ? 'Completing sign-in · do not close' : 'Something went wrong' }}
      </p>
    </div>
  </div>
</template>
