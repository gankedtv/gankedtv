<template>
  <main
    class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-4 px-6 text-center"
  >
    <h1 class="m-0 font-condensed text-[96px] font-black leading-none text-text-muted">404</h1>
    <p class="m-0 max-w-[40ch] text-sm text-text-secondary">
      This page doesn't exist. It may have been moved or never existed.
    </p>
    <RouterLink
      to="/"
      class="mt-2 rounded-lg bg-accent px-4 py-2 text-xs font-bold text-[#080f0d] no-underline transition-[filter] duration-150 hover:brightness-105"
    >
      Go home
    </RouterLink>
  </main>
</template>
