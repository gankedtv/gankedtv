<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { GAMES, CLIPS } from '@/lib/mock-data'
import ClipCard from '@/components/ClipCard.vue'

const router = useRouter()

const active = ref<'all' | string>('all')

const gameCount = Object.keys(GAMES).length
const clipCount = CLIPS.length

// Unique creator count across all clips
const creatorCount = new Set(CLIPS.map((c) => c.user)).size

// Per-game clip + creator counts
function gameClipCount(key: string) {
  return CLIPS.filter((c) => c.game === key).length
}
function gameCreatorCount(key: string) {
  return new Set(CLIPS.filter((c) => c.game === key).map((c) => c.user)).size
}

const filteredClips = computed(() => {
  const base = active.value === 'all' ? CLIPS : CLIPS.filter((c) => c.game === active.value)
  // Pad/repeat to fill 8 slots
  const result: typeof CLIPS = []
  while (result.length < 8) {
    result.push(...base)
  }
  return result.slice(0, 8)
})

const sectionTitle = computed(() =>
  active.value === 'all' ? 'Featured across all games' : `Top in ${GAMES[active.value]?.name}`,
)
</script>

<template>
  <main style="max-width: 1440px; margin: 0 auto; padding: 32px 24px 120px">
    <!-- Page header -->
    <div>
      <div
        style="
          font-family: var(--font-mono);
          font-size: 11px;
          color: var(--color-text-muted);
          letter-spacing: 0.1em;
          text-transform: uppercase;
          margin-bottom: 8px;
          display: flex;
          align-items: center;
          gap: 8px;
        "
      >
        <span
          style="
            width: 6px;
            height: 6px;
            background: var(--color-neon);
            border-radius: 50%;
            box-shadow: 0 0 8px var(--color-neon);
            animation: pulse 2s infinite;
            flex-shrink: 0;
          "
        ></span>
        Library · {{ gameCount }} games · {{ clipCount * 200 }}+ clips indexed
      </div>
      <h1
        style="
          font-family: var(--font-heading);
          font-weight: 700;
          font-size: clamp(32px, 4vw, 52px);
          letter-spacing: 0.02em;
          text-transform: uppercase;
          margin: 0 0 8px;
          line-height: 1;
          color: var(--color-text-primary);
        "
      >
        Games
      </h1>
      <p
        style="
          color: var(--color-text-secondary);
          font-size: 15px;
          margin: 0;
          max-width: 56ch;
          line-height: 1.5;
        "
      >
        Every clip is tagged with its game. Pick a game to see its feed, top creators, and today's
        highlights.
      </p>
    </div>

    <!-- Game tiles -->
    <div class="game-tile-grid">
      <!-- All games tile -->
      <button
        class="game-tile"
        :style="
          active === 'all'
            ? 'background: var(--color-brand); border-color: var(--color-brand); box-shadow: 0 14px 40px -14px var(--color-brand-glow);'
            : 'background: var(--color-surface-raised); border-color: var(--color-border);'
        "
        @click="active = 'all'"
      >
        <div
          style="
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            height: 100%;
            gap: 8px;
          "
        >
          <span
            style="
              font-family: var(--font-heading);
              font-weight: 700;
              font-size: 20px;
              text-transform: uppercase;
              color: #fff;
              line-height: 1;
            "
            >All Games</span
          >
          <div style="display: flex; flex-direction: column; gap: 3px">
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: var(--color-neon);
                letter-spacing: 0.08em;
              "
            >
              {{ clipCount * 200 }}+ clips
            </span>
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: rgba(255, 255, 255, 0.7);
                letter-spacing: 0.08em;
              "
            >
              {{ creatorCount }} creators
            </span>
          </div>
        </div>
      </button>

      <!-- Per-game tiles -->
      <button
        v-for="(game, key) in GAMES"
        :key="key"
        class="game-tile"
        style="position: relative; overflow: hidden"
        :style="
          active === key
            ? 'border-color: var(--color-brand); box-shadow: 0 14px 40px -14px var(--color-brand-glow);'
            : 'border-color: var(--color-border);'
        "
        @click="active = key"
      >
        <!-- Background art -->
        <img
          :src="game.art"
          alt=""
          style="
            position: absolute;
            inset: 0;
            width: 100%;
            height: 100%;
            object-fit: cover;
            opacity: 0.4;
          "
        />
        <!-- Gradient overlay -->
        <div
          style="
            position: absolute;
            inset: 0;
            background: linear-gradient(160deg, rgba(8, 8, 16, 0.4) 0%, rgba(8, 8, 16, 0.85) 100%);
          "
        ></div>
        <!-- Content -->
        <div
          style="
            position: relative;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            height: 100%;
            gap: 8px;
          "
        >
          <span
            style="
              font-family: var(--font-heading);
              font-weight: 700;
              font-size: 20px;
              text-transform: uppercase;
              color: #fff;
              line-height: 1;
            "
            >{{ game.name }}</span
          >
          <div style="display: flex; flex-direction: column; gap: 3px">
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: var(--color-neon);
                letter-spacing: 0.08em;
              "
            >
              {{ gameClipCount(key) * 200 }}+ clips
            </span>
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: rgba(255, 255, 255, 0.7);
                letter-spacing: 0.08em;
              "
            >
              {{ gameCreatorCount(key) }} creators
            </span>
          </div>
        </div>
      </button>
    </div>

    <!-- Clip section -->
    <div style="margin-top: 48px">
      <!-- Section header -->
      <div
        style="
          display: flex;
          align-items: baseline;
          justify-content: space-between;
          margin-bottom: 20px;
          gap: 16px;
        "
      >
        <h2
          class="section-title-bar"
          style="
            font-family: var(--font-heading);
            font-weight: 700;
            font-size: 24px;
            text-transform: uppercase;
            margin: 0;
            display: flex;
            align-items: center;
            gap: 14px;
            color: var(--color-text-primary);
          "
        >
          {{ sectionTitle }}
        </h2>
        <a
          href="#"
          style="
            font-family: var(--font-mono);
            font-size: 11px;
            color: var(--color-text-secondary);
            text-transform: uppercase;
            letter-spacing: 0.06em;
            white-space: nowrap;
          "
          >See all ·→</a
        >
      </div>

      <!-- Clip grid -->
      <div class="feed-grid">
        <ClipCard
          v-for="(clip, i) in filteredClips"
          :key="`${clip.id}-${i}`"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>
    </div>
  </main>
</template>

<style scoped>
.game-tile-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 14px;
  margin-top: 32px;
}

.game-tile {
  min-height: 110px;
  padding: 16px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  cursor: pointer;
  text-align: left;
  transition:
    border-color 150ms,
    box-shadow 150ms;
  background: var(--color-surface-raised);
}

.game-tile:hover {
  border-color: var(--color-border-hover);
}
</style>
