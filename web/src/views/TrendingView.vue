<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { CLIPS, USERS, GAMES, formatNum, formatDuration } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'

const router = useRouter()

const TIME_WINDOWS = [
  { key: '1h', label: 'Last hour' },
  { key: '24h', label: '24 hours' },
  { key: '7d', label: 'This week' },
  { key: '30d', label: 'This month' },
  { key: 'all', label: 'All time' },
] as const

const window = ref<string>('24h')

const topClips = computed(() => [...CLIPS].sort((a, b) => b.likes - a.likes).slice(0, 10))

function trendFor(i: number): 'up' | 'hold' | 'down' {
  if (i < 3) return 'up'
  if (i < 6) return 'hold'
  return i % 2 === 0 ? 'up' : 'down'
}

const TOP_CREATOR_KEYS = ['sundownr', 'phantomveil', 'nyxproto', 'rustyquill', 'wrenhowl'] as const
const CREATOR_GAINED = [3180, 2740, 1820, 990, 540]

// Map username → USERS key
function userKeyByUsername(username: string): string {
  return Object.keys(USERS).find((k) => USERS[k].username === username) ?? username
}

const HOT_GAMES_CLIPS_TODAY = [412, 388, 274, 210, 188, 156]
const gameEntries = Object.entries(GAMES)

// Simple deterministic sparkline points
function sparklinePoints(index: number): string {
  const base = HOT_GAMES_CLIPS_TODAY[index]
  const vals = [
    base * 0.55,
    base * 0.62,
    base * 0.48,
    base * 0.7,
    base * 0.65,
    base * 0.82,
    base * 0.91,
    base,
  ]
  const max = Math.max(...vals)
  const min = Math.min(...vals)
  const range = max - min || 1
  return vals
    .map((v, i) => {
      const x = (i / (vals.length - 1)) * 56
      const y = 12 - ((v - min) / range) * 12
      return `${x.toFixed(1)},${y.toFixed(1)}`
    })
    .join(' ')
}
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
        "
      >
        Updated every 5 min · Ranked by likes + plays
      </div>
      <h1
        style="
          font-family: var(--font-heading);
          font-weight: 700;
          font-size: clamp(32px, 4vw, 52px);
          letter-spacing: 0.02em;
          text-transform: uppercase;
          margin: 0 0 20px;
          line-height: 1;
          color: var(--color-text-primary);
        "
      >
        Trending
      </h1>

      <!-- Time window toggle -->
      <div
        style="
          display: inline-flex;
          gap: 2px;
          padding: 4px;
          background: var(--color-surface-raised);
          border: 1px solid var(--color-border);
          border-radius: var(--radius-sm);
        "
      >
        <button
          v-for="tw in TIME_WINDOWS"
          :key="tw.key"
          :style="
            window === tw.key
              ? 'padding: 6px 14px; border-radius: var(--radius-sm); background: var(--color-brand); color: #fff; font-family: var(--font-mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; border: none; cursor: pointer; transition: background 150ms;'
              : 'padding: 6px 14px; border-radius: var(--radius-sm); background: transparent; color: var(--color-text-secondary); font-family: var(--font-mono); font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; border: none; cursor: pointer; transition: color 150ms;'
          "
          @click="window = tw.key"
        >
          {{ tw.label }}
        </button>
      </div>
    </div>

    <!-- Two-column layout -->
    <div class="trending-grid">
      <!-- LEFT: Top 10 leaderboard -->
      <div
        style="
          background: var(--color-surface-raised);
          border: 1px solid var(--color-border);
          border-radius: var(--radius-md);
          overflow: hidden;
          margin-top: 28px;
        "
      >
        <div style="padding: 14px 16px; border-bottom: 1px solid var(--color-border)">
          <span
            style="
              font-family: var(--font-heading);
              font-weight: 700;
              font-size: 14px;
              text-transform: uppercase;
              color: var(--color-text-secondary);
              letter-spacing: 0.05em;
            "
            >Top 10 this period</span
          >
        </div>

        <div
          v-for="(clip, i) in topClips"
          :key="clip.id"
          class="leaderboard-row"
          style="
            display: grid;
            grid-template-columns: 60px 120px 1fr auto auto;
            gap: 16px;
            align-items: center;
            padding: 12px 16px;
            cursor: pointer;
            transition: background 150ms;
            border-bottom: 1px solid var(--color-border);
          "
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        >
          <!-- Rank + trend -->
          <div style="display: flex; flex-direction: column; align-items: flex-start; gap: 2px">
            <span
              :style="
                i < 3
                  ? 'font-family: var(--font-heading); font-weight: 700; font-size: 28px; line-height: 1; color: var(--color-brand-light);'
                  : 'font-family: var(--font-heading); font-weight: 700; font-size: 28px; line-height: 1; color: var(--color-text-muted);'
              "
              >#{{ i + 1 }}</span
            >
            <span
              style="font-family: var(--font-mono); font-size: 11px; line-height: 1"
              :style="
                trendFor(i) === 'up'
                  ? 'color: var(--color-neon);'
                  : trendFor(i) === 'down'
                    ? 'color: var(--color-error);'
                    : 'color: var(--color-text-muted);'
              "
              >{{ trendFor(i) === 'up' ? '▲' : trendFor(i) === 'down' ? '▼' : '—' }}</span
            >
          </div>

          <!-- Thumbnail -->
          <div style="position: relative; border-radius: 4px; overflow: hidden; aspect-ratio: 16/9">
            <img
              :src="clip.art"
              alt=""
              style="width: 100%; height: 100%; object-fit: cover; display: block"
            />
            <span
              style="
                position: absolute;
                bottom: 4px;
                right: 4px;
                font-family: var(--font-mono);
                font-size: 10px;
                color: #fff;
                background: rgba(0, 0, 0, 0.75);
                padding: 2px 5px;
                border-radius: 3px;
                line-height: 1;
              "
              >{{ formatDuration(clip.duration) }}</span
            >
          </div>

          <!-- Title + meta -->
          <div style="min-width: 0; display: flex; flex-direction: column; gap: 4px">
            <span
              style="
                font-family: var(--font-body);
                font-size: 13px;
                font-weight: 500;
                color: var(--color-text-primary);
                line-height: 1.35;
                display: -webkit-box;
                -webkit-line-clamp: 2;
                -webkit-box-orient: vertical;
                overflow: hidden;
              "
              >{{ clip.title }}</span
            >
            <div
              style="
                display: flex;
                align-items: center;
                gap: 6px;
                font-family: var(--font-mono);
                font-size: 10px;
              "
            >
              <span
                style="
                  background: var(--color-surface-base);
                  border: 1px solid var(--color-border-strong);
                  border-radius: 3px;
                  padding: 2px 6px;
                  color: var(--color-text-secondary);
                  text-transform: uppercase;
                  letter-spacing: 0.06em;
                "
                >{{ GAMES[clip.game]?.tag }}</span
              >
              <span style="color: var(--color-neon)">@{{ USERS[clip.user]?.username }}</span>
            </div>
          </div>

          <!-- Stats -->
          <div
            style="
              display: flex;
              flex-direction: column;
              gap: 4px;
              text-align: right;
              font-family: var(--font-mono);
              font-size: 11px;
              color: var(--color-text-secondary);
              white-space: nowrap;
            "
          >
            <span>♥ {{ formatNum(clip.likes) }}</span>
            <span style="color: var(--color-text-muted)">{{ formatNum(clip.views) }} plays</span>
          </div>

          <!-- Chevron -->
          <div style="color: var(--color-text-muted)">
            <svg
              width="16"
              height="16"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <polyline points="9 18 15 12 9 6" />
            </svg>
          </div>
        </div>
      </div>

      <!-- RIGHT sidebar -->
      <div style="display: flex; flex-direction: column; gap: 16px; margin-top: 28px">
        <!-- Top creators -->
        <div
          style="
            background: var(--color-surface-raised);
            border: 1px solid var(--color-border);
            border-radius: var(--radius-md);
            overflow: hidden;
          "
        >
          <div style="padding: 14px 16px; border-bottom: 1px solid var(--color-border)">
            <span
              class="section-title-bar"
              style="
                font-family: var(--font-heading);
                font-weight: 700;
                font-size: 14px;
                text-transform: uppercase;
                color: var(--color-text-secondary);
                letter-spacing: 0.05em;
                display: flex;
                align-items: center;
                gap: 10px;
              "
              >Top Creators</span
            >
          </div>

          <div
            v-for="(username, i) in TOP_CREATOR_KEYS"
            :key="username"
            style="
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 10px 16px;
              border-bottom: 1px solid var(--color-border);
              cursor: pointer;
              transition: background 150ms;
            "
            class="sidebar-row"
            @click="router.push({ name: 'user', params: { username } })"
          >
            <span
              style="
                font-family: var(--font-heading);
                font-weight: 700;
                font-size: 18px;
                color: var(--color-text-muted);
                width: 24px;
                flex-shrink: 0;
                line-height: 1;
              "
              >#{{ i + 1 }}</span
            >
            <UserAvatar :user="userKeyByUsername(username)" :size="36" />
            <div style="min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 1px">
              <span
                style="
                  font-family: var(--font-body);
                  font-size: 13px;
                  font-weight: 500;
                  color: var(--color-text-primary);
                  white-space: nowrap;
                  overflow: hidden;
                  text-overflow: ellipsis;
                "
              >
                {{ USERS[userKeyByUsername(username)]?.display }}
              </span>
              <span
                style="
                  font-family: var(--font-mono);
                  font-size: 10px;
                  color: var(--color-text-muted);
                "
              >
                @{{ username }}
              </span>
            </div>
            <span
              style="
                font-family: var(--font-mono);
                font-size: 10px;
                color: var(--color-neon);
                white-space: nowrap;
                flex-shrink: 0;
              "
            >
              +{{ formatNum(CREATOR_GAINED[i]) }}
            </span>
          </div>
        </div>

        <!-- Hot games -->
        <div
          style="
            background: var(--color-surface-raised);
            border: 1px solid var(--color-border);
            border-radius: var(--radius-md);
            overflow: hidden;
          "
        >
          <div style="padding: 14px 16px; border-bottom: 1px solid var(--color-border)">
            <span
              class="section-title-bar"
              style="
                font-family: var(--font-heading);
                font-weight: 700;
                font-size: 14px;
                text-transform: uppercase;
                color: var(--color-text-secondary);
                letter-spacing: 0.05em;
                display: flex;
                align-items: center;
                gap: 10px;
              "
              >Hot Games</span
            >
          </div>

          <div
            v-for="([key, game], i) in gameEntries"
            :key="key"
            style="
              display: flex;
              align-items: center;
              gap: 12px;
              padding: 10px 16px;
              border-bottom: 1px solid var(--color-border);
              cursor: pointer;
              transition: background 150ms;
            "
            class="sidebar-row"
            @click="router.push({ name: 'games', query: { game: key } })"
          >
            <!-- Game art thumbnail -->
            <div
              style="
                width: 40px;
                height: 40px;
                border-radius: 4px;
                overflow: hidden;
                flex-shrink: 0;
                position: relative;
              "
            >
              <img
                :src="game.art"
                alt=""
                style="width: 100%; height: 100%; object-fit: cover; display: block"
              />
            </div>
            <div style="min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 1px">
              <span
                style="
                  font-family: var(--font-body);
                  font-size: 13px;
                  font-weight: 500;
                  color: var(--color-text-primary);
                  white-space: nowrap;
                  overflow: hidden;
                  text-overflow: ellipsis;
                "
              >
                {{ game.name }}
              </span>
              <span
                style="font-family: var(--font-mono); font-size: 10px; color: var(--color-neon)"
              >
                +{{ HOT_GAMES_CLIPS_TODAY[i] }} clips today
              </span>
            </div>
            <!-- Sparkline -->
            <svg
              width="56"
              height="14"
              viewBox="0 0 56 14"
              style="flex-shrink: 0; overflow: visible"
            >
              <polyline
                :points="sparklinePoints(i)"
                fill="none"
                stroke="var(--color-neon)"
                stroke-width="1.5"
                stroke-linecap="round"
                stroke-linejoin="round"
                opacity="0.7"
              />
            </svg>
          </div>
        </div>
      </div>
    </div>
  </main>
</template>

<style scoped>
.trending-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 28px;
  align-items: start;
}

.leaderboard-row:last-child,
.sidebar-row:last-child {
  border-bottom: none;
}

.leaderboard-row:hover {
  background: var(--color-surface-overlay);
}

.sidebar-row:hover {
  background: var(--color-surface-overlay);
}

@media (max-width: 960px) {
  .trending-grid {
    grid-template-columns: 1fr;
  }
}
</style>
