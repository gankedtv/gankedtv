<script setup lang="ts">
import { computed } from 'vue'
import { parseRichText } from '@/lib/richText'

// Every piece of user text lands in a `{{ }}` interpolation and every href arrives
// scheme-checked from the parser, so no `v-html` is involved and there is nothing to sanitise.
const props = defineProps<{ text: string | null | undefined }>()

const blocks = computed(() => parseRichText(props.text))
</script>

<template>
  <div v-if="blocks.length" class="rich-text">
    <template v-for="(block, bi) in blocks" :key="bi">
      <p v-if="block.t === 'p'" class="m-0">
        <template v-for="(line, li) in block.lines" :key="li">
          <br v-if="li > 0" />
          <template v-for="(node, ni) in line" :key="ni">
            <strong v-if="node.t === 'strong'" class="font-semibold text-text-primary">{{
              node.v
            }}</strong>
            <em v-else-if="node.t === 'em'">{{ node.v }}</em>
            <a
              v-else-if="node.t === 'link'"
              :href="node.href"
              target="_blank"
              rel="noopener noreferrer nofollow"
              class="text-accent no-underline hover:underline"
              >{{ node.v }}</a
            >
            <template v-else>{{ node.v }}</template>
          </template>
        </template>
      </p>

      <component
        :is="block.t"
        v-else
        class="m-0 pl-5"
        :class="block.t === 'ul' ? 'list-disc' : 'list-decimal'"
      >
        <li v-for="(item, ii) in block.items" :key="ii">
          <template v-for="(node, ni) in item" :key="ni">
            <strong v-if="node.t === 'strong'" class="font-semibold text-text-primary">{{
              node.v
            }}</strong>
            <em v-else-if="node.t === 'em'">{{ node.v }}</em>
            <a
              v-else-if="node.t === 'link'"
              :href="node.href"
              target="_blank"
              rel="noopener noreferrer nofollow"
              class="text-accent no-underline hover:underline"
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
/* The only rules utilities can't express: em-relative rhythm, so the spacing tracks whatever
   font-size the call site sets. Everything else is a utility on the element. */
.rich-text > * + * {
  margin-top: 0.5em;
}

.rich-text li + li {
  margin-top: 0.15em;
}
</style>
