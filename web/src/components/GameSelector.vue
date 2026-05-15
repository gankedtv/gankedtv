<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted } from 'vue'
import { games as gamesApi, type GameListItem } from '@/api/games'
import type { GameSummary } from '@/api/clips'
import GameChipButton from '@/components/GameChipButton.vue'
import GameSearchResult from '@/components/GameSearchResult.vue'

defineProps<{ modelValue: GameSummary | null }>()
const emit = defineEmits<{ 'update:modelValue': [value: GameListItem | null] }>()

const popularGames = ref<GameListItem[]>([])
const gameSearch = ref('')
const gameResults = ref<GameListItem[]>([])
const showGameDropdown = ref(false)
let gameSearchTimer: ReturnType<typeof setTimeout> | null = null
let gameBlurTimer: ReturnType<typeof setTimeout> | null = null

onMounted(async () => {
  try {
    popularGames.value = await gamesApi.list(6)
  } catch {
    // Picker degrades to typeahead-only if the popular list fails.
    popularGames.value = []
  }
})

watch(gameSearch, (q) => {
  if (gameSearchTimer) clearTimeout(gameSearchTimer)
  const trimmed = q.trim()
  if (!trimmed) {
    gameResults.value = []
    showGameDropdown.value = false
    return
  }
  gameSearchTimer = setTimeout(async () => {
    // Drop stale responses when the user has typed more since scheduling.
    const queryAtCall = trimmed
    try {
      const results = await gamesApi.search(queryAtCall, 8)
      if (gameSearch.value.trim() !== queryAtCall) return
      gameResults.value = results
      showGameDropdown.value = true
    } catch {
      if (gameSearch.value.trim() !== queryAtCall) return
      gameResults.value = []
    }
  }, 200)
})

function pickGame(g: GameListItem) {
  emit('update:modelValue', g)
  gameSearch.value = ''
  gameResults.value = []
  showGameDropdown.value = false
}

function clearGame() {
  emit('update:modelValue', null)
}

function onGameInputBlur() {
  // Delay so a click on a dropdown item registers before we hide it.
  // mousedown.prevent on the <li> handles most cases; iOS taps may skip mousedown.
  if (gameBlurTimer) clearTimeout(gameBlurTimer)
  gameBlurTimer = setTimeout(() => {
    showGameDropdown.value = false
    gameBlurTimer = null
  }, 150)
}

onUnmounted(() => {
  if (gameSearchTimer) clearTimeout(gameSearchTimer)
  if (gameBlurTimer) clearTimeout(gameBlurTimer)
})

const inputClass =
  'w-full rounded-md border border-border bg-surface-raised px-3.5 py-3 font-body text-sm text-text-primary outline-none'
</script>

<template>
  <div>
    <!-- Selected pill -->
    <div
      v-if="modelValue"
      class="mb-2 inline-flex items-center gap-2 rounded-md border border-brand-light bg-brand-glow px-3 py-1.5"
    >
      <span class="font-mono text-[10px] uppercase tracking-[0.06em] text-text-primary">
        {{ modelValue.tag }}
      </span>
      <span class="font-body text-xs text-text-secondary">{{ modelValue.name }}</span>
      <button
        type="button"
        @click="clearGame"
        aria-label="Clear selected game"
        class="cursor-pointer font-mono text-[11px] leading-none text-text-muted transition-colors duration-150 hover:text-text-primary"
      >
        ×
      </button>
    </div>

    <!-- Popular chips -->
    <div v-if="!modelValue && popularGames.length" class="mb-2 flex flex-wrap gap-2">
      <GameChipButton
        v-for="g in popularGames"
        :key="g.id"
        :tag="g.tag"
        @click="pickGame(g)"
      />
    </div>

    <!-- Typeahead -->
    <div v-if="!modelValue" class="relative">
      <input
        v-model="gameSearch"
        placeholder="Search games…"
        :class="inputClass"
        @focus="showGameDropdown = gameResults.length > 0"
        @blur="onGameInputBlur"
      />
      <ul
        v-if="showGameDropdown && gameResults.length"
        role="listbox"
        class="absolute left-0 right-0 top-full z-10 mt-1 max-h-60 overflow-auto rounded-md border border-border-strong bg-surface-raised"
      >
        <GameSearchResult
          v-for="g in gameResults"
          :key="g.id"
          :tag="g.tag"
          :name="g.name"
          @select="pickGame(g)"
        />
      </ul>
    </div>
  </div>
</template>
