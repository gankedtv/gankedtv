<script setup lang="ts">
import { watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { oauthStartUrl } from '@/api/auth'
import IconDiscord from '@/components/icons/IconDiscord.vue'
import IconGoogle from '@/components/icons/IconGoogle.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const rawRedirect = route.query.redirect
let returnTo: string | undefined
if (
  typeof rawRedirect === 'string' &&
  rawRedirect.startsWith('/') &&
  !rawRedirect.startsWith('//')
) {
  returnTo = rawRedirect
}

watchEffect(() => {
  if (auth.isAuthenticated) {
    router.replace(returnTo || '/')
  }
})
</script>

<template>
  <div class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-8 px-6">
    <!-- Logo + tagline -->
    <div class="flex flex-col items-center gap-2.5 text-center">
      <div class="flex items-center gap-2.5">
        <span class="logo__mark"></span>
        <span
          class="font-display text-[28px] font-bold uppercase tracking-[0.04em] text-text-primary"
        >
          GANKED.TV
        </span>
      </div>
      <div class="font-mono text-[11px] uppercase tracking-widest text-text-muted">
        No algorithm. Just clips.
      </div>
    </div>

    <!-- Card -->
    <div class="w-full max-w-100 rounded-lg border border-border bg-surface-raised px-8 py-9">
      <div class="mb-6 text-center">
        <h1 class="m-0 mb-2 font-heading text-[32px] font-bold uppercase text-text-primary">
          Sign In
        </h1>
        <p class="m-0 font-body text-sm text-text-secondary">
          Connect with your gaming account to continue
        </p>
      </div>

      <div class="flex flex-col gap-3">
        <!-- Discord button -->
        <a
          :href="oauthStartUrl('discord', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-md bg-discord px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-white no-underline transition-colors duration-150 hover:bg-discord-hover"
        >
          <IconDiscord :size="20" class="shrink-0" />
          Continue with Discord
        </a>

        <!-- Divider -->
        <div class="flex items-center gap-3">
          <div class="h-px flex-1 bg-border"></div>
          <span class="font-mono text-[10px] uppercase tracking-widest text-text-muted"> or </span>
          <div class="h-px flex-1 bg-border"></div>
        </div>

        <!-- Google button -->
        <a
          :href="oauthStartUrl('google', returnTo)"
          class="flex items-center justify-center gap-2.5 rounded-md border border-border-strong bg-surface-overlay px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-[0.06em] text-text-primary no-underline transition-[background-color,border-color] duration-150 hover:border-border-hover hover:bg-surface-raised"
        >
          <IconGoogle :size="20" class="shrink-0" />
          Continue with Google
        </a>
      </div>

      <!-- Footer -->
      <p class="m-0 mt-5 text-center font-mono text-[10px] tracking-wider text-text-muted">
        By signing in you agree to our Terms of Service.
      </p>
    </div>
  </div>
</template>
