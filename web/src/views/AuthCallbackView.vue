<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

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

  if (!token || !refresh) {
    status.value = 'error'
    await router.replace({ name: 'login', query: { error: 'oauth_failed' } })
    return
  }

  try {
    auth.setSession(token, refresh)
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
    <div class="flex flex-col items-center gap-3">
      <span
        class="inline-block h-2.5 w-2.5 animate-pulse rounded-full bg-neon"
        aria-hidden="true"
      />
      <h1 class="font-heading text-2xl font-bold uppercase tracking-widest text-text-primary">
        Authenticating
      </h1>
      <p class="font-mono text-sm text-text-secondary">
        {{ status === 'loading' ? 'Verifying credentials…' : 'Something went wrong' }}
      </p>
    </div>
  </div>
</template>
