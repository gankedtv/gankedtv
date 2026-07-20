<script setup lang="ts">
defineProps<{ title: string; updated: string }>()
</script>

<template>
  <div class="mx-auto w-full max-w-[76ch] px-7 pb-20 pt-10 max-tablet:px-4">
    <h1
      class="m-0 font-condensed text-[clamp(30px,3.6vw,42px)] font-black uppercase leading-none text-text-primary"
    >
      {{ title }}
    </h1>
    <p class="m-0 mt-3 text-[11px] text-text-muted">Last updated: {{ updated }}</p>
    <div class="legal-prose mt-8">
      <slot />
    </div>
  </div>
</template>

<style scoped>
/* Long-form legal prose is authored as plain semantic HTML in the views; styling it
   element-by-element with utilities would repeat the same classes dozens of times, so
   this is one of the sanctioned scoped-CSS cases. Tokens only — no literals. */
.legal-prose :deep(h2) {
  margin: 28px 0 10px;
  font-family: var(--font-condensed);
  font-size: 20px;
  font-weight: 800;
  text-transform: uppercase;
  line-height: 1.15;
  color: var(--color-text-primary);
}

.legal-prose :deep(p),
.legal-prose :deep(li) {
  margin: 0 0 12px;
  font-size: 14px;
  line-height: 1.65;
  color: var(--color-text-secondary);
}

.legal-prose :deep(li) {
  margin-bottom: 6px;
}

.legal-prose :deep(ul) {
  margin: 0 0 12px;
  padding-left: 20px;
  list-style: disc;
}

.legal-prose :deep(strong) {
  font-weight: 600;
  color: var(--color-text-primary);
}

.legal-prose :deep(a) {
  color: var(--color-accent);
  text-decoration: none;
}

.legal-prose :deep(a:hover) {
  text-decoration: underline;
}
</style>
