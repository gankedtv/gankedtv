<template>
  <main class="flex min-h-[calc(100vh-4rem)] flex-col items-center justify-center gap-4 px-6 text-center">
    <!-- The number is the illustration. -->
    <h1
      class="m-0 font-heading text-[clamp(140px,24vw,240px)] font-bold leading-[0.85] tracking-[-0.01em] text-ink"
    >
      404
    </h1>
    <p class="m-0 font-heading text-2xl font-bold uppercase tracking-[0.02em] text-text-primary">
      Off the Record
    </p>
    <p class="m-0 max-w-[40ch] font-body text-sm text-text-secondary">
      This page never made it to print. The filing may have been moved, retracted, or never
      existed.
    </p>
    <RouterLink
      to="/"
      class="mt-2 font-mono text-[11px] uppercase tracking-[0.12em] text-ink no-underline hover:underline"
    >
      ← Back to the feed
    </RouterLink>
  </main>
</template>
