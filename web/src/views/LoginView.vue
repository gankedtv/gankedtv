<script setup lang="ts">
import { watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { oauthStartUrl } from '@/api/auth'

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
          <svg
            class="shrink-0"
            width="20"
            height="20"
            viewBox="0 0 24 24"
            fill="currentColor"
            aria-hidden="true"
          >
            <path
              d="M20.317 4.37a19.791 19.791 0 0 0-4.885-1.515.074.074 0 0 0-.079.037c-.21.375-.444.864-.608 1.25a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.617-1.25.077.077 0 0 0-.079-.037A19.736 19.736 0 0 0 3.677 4.37a.07.07 0 0 0-.032.027C.533 9.046-.32 13.58.099 18.057a.082.082 0 0 0 .031.057 19.9 19.9 0 0 0 5.993 3.03.078.078 0 0 0 .084-.028c.462-.63.874-1.295 1.226-1.994a.076.076 0 0 0-.041-.106 13.107 13.107 0 0 1-1.872-.892.077.077 0 0 1-.008-.128 10.2 10.2 0 0 0 .372-.292.074.074 0 0 1 .077-.01c3.928 1.793 8.18 1.793 12.062 0a.074.074 0 0 1 .078.01c.12.098.246.198.373.292a.077.077 0 0 1-.006.127 12.299 12.299 0 0 1-1.873.892.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028 19.839 19.839 0 0 0 6.002-3.03.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.03zM8.02 15.33c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.956-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.956 2.418-2.157 2.418zm7.975 0c-1.183 0-2.157-1.085-2.157-2.419 0-1.333.955-2.419 2.157-2.419 1.21 0 2.176 1.096 2.157 2.42 0 1.333-.946 2.418-2.157 2.418z"
            />
          </svg>
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
          <svg class="shrink-0" width="20" height="20" viewBox="0 0 24 24" aria-hidden="true">
            <path
              fill="#4285F4"
              d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92c-.26 1.37-1.04 2.53-2.21 3.31v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.09z"
            />
            <path
              fill="#34A853"
              d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z"
            />
            <path
              fill="#FBBC05"
              d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z"
            />
            <path
              fill="#EA4335"
              d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z"
            />
          </svg>
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
