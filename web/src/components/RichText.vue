<script setup lang="ts">
import { computed } from 'vue'
import { parseRichText } from '@/lib/richText'

// Renders the small markdown subset in lib/richText.ts as real elements. Every piece of user
// text lands in a `{{ }}` interpolation and every href comes back scheme-checked from the
// parser, so no `v-html` is involved and there is nothing to sanitise.
const props = defineProps<{ text: string | null | undefined }>()

const blocks = computed(() => parseRichText(props.text))
</script>

<template>
  <div v-if="blocks.length" class="rich-text">
    <template v-for="(block, bi) in blocks" :key="bi">
      <p v-if="block.t === 'p'">
        <template v-for="(line, li) in block.lines" :key="li">
          <br v-if="li > 0" />
          <template v-for="(node, ni) in line" :key="ni">
            <strong v-if="node.t === 'strong'">{{ node.v }}</strong>
            <em v-else-if="node.t === 'em'">{{ node.v }}</em>
            <a
              v-else-if="node.t === 'link'"
              :href="node.href"
              target="_blank"
              rel="noopener noreferrer nofollow"
              >{{ node.v }}</a
            >
            <template v-else>{{ node.v }}</template>
          </template>
        </template>
      </p>

      <component :is="block.t" v-else>
        <li v-for="(item, ii) in block.items" :key="ii">
          <template v-for="(node, ni) in item" :key="ni">
            <strong v-if="node.t === 'strong'">{{ node.v }}</strong>
            <em v-else-if="node.t === 'em'">{{ node.v }}</em>
            <a
              v-else-if="node.t === 'link'"
              :href="node.href"
              target="_blank"
              rel="noopener noreferrer nofollow"
              >{{ node.v }}</a
            >
            <template v-else>{{ node.v }}</template>
          </template>
        </li>
      </component>
    </template>
  </div>
</template>

<style scoped>
/* Tailwind's preflight strips list markers and element margins, so prose elements have to
   re-declare them. Sizing/colour are inherited from the call site — this component only
   restores structure. */
.rich-text > * {
  margin: 0;
}

.rich-text > * + * {
  margin-top: 0.5em;
}

.rich-text ul,
.rich-text ol {
  padding-left: 1.25em;
}

.rich-text ul {
  list-style: disc;
}

.rich-text ol {
  list-style: decimal;
}

.rich-text li + li {
  margin-top: 0.15em;
}

.rich-text strong {
  font-weight: 600;
  color: var(--color-text-primary);
}

.rich-text a {
  color: var(--color-accent);
  text-decoration: none;
}

.rich-text a:hover {
  text-decoration: underline;
}
</style>
