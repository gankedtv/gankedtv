<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { CLIPS, GAMES, USERS, formatNum, formatDuration } from '@/lib/mock-data'
import ClipCard from '@/components/ClipCard.vue'
import UserAvatar from '@/components/UserAvatar.vue'

const router = useRouter()

const hero = CLIPS[3] // Hanzo 6k
const heroUser = USERS[hero.user]
const heroGame = GAMES[hero.game]

const filter = ref<string>('all')
const sort = ref<string>('hot')

const GAME_FILTERS = [
  { key: 'all', label: 'All Games' },
  { key: 'valorant', label: 'Valorant' },
  { key: 'rocket', label: 'Rocket League' },
  { key: 'minecraft', label: 'Minecraft' },
  { key: 'overwatch', label: 'Overwatch 2' },
  { key: 'fortnite', label: 'Fortnite' },
  { key: 'league', label: 'LoL' },
]

const filteredClips = computed(() => {
  if (filter.value === 'all') return CLIPS
  return CLIPS.filter((c) => c.game === filter.value)
})

const secondaryClips = [CLIPS[0], CLIPS[5], CLIPS[1], CLIPS[11]]
</script>

<template>
  <main
    class="page"
    style="max-width: 1440px; margin: 0 auto; padding: 32px 24px 120px;"
  >
    <!-- Page header -->
    <div>
      <div
        class="page-eyebrow"
        style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); letter-spacing: 0.1em; text-transform: uppercase; margin-bottom: 8px; display: flex; align-items: center; gap: 8px;"
      >
        <span
          class="pulse-dot"
          style="width: 6px; height: 6px; background: var(--color-neon); border-radius: 50%; box-shadow: 0 0 8px var(--color-neon); animation: pulse 2s infinite; flex-shrink: 0;"
        ></span>
        Live Feed · {{ filteredClips.length }} clips
      </div>
      <h1
        style="font-family: var(--font-heading); font-weight: 700; font-size: clamp(32px, 4vw, 52px); letter-spacing: 0.02em; text-transform: uppercase; margin: 0; line-height: 1; color: var(--color-text-primary);"
      >
        The Feed
      </h1>
    </div>

    <!-- Desktop hero card -->
    <div
      class="hero"
      style="margin-top: 28px; margin-bottom: 48px; position: relative; border: 1px solid var(--color-border); border-radius: var(--radius-lg); overflow: hidden; background: var(--color-surface-raised);"
    >
      <div style="display: grid; grid-template-columns: 1.4fr 1fr; min-height: 460px;">
        <!-- Left: thumbnail -->
        <div style="position: relative; overflow: hidden;">
          <img
            :src="hero.art"
            alt=""
            style="width: 100%; height: 100%; object-fit: cover; display: block;"
          />
          <!-- Fade overlay blending into right panel -->
          <div
            style="position: absolute; inset: 0; background: linear-gradient(90deg, transparent 50%, var(--color-surface-raised) 100%);"
          ></div>
          <!-- Game badge -->
          <div style="position: absolute; top: 20px; left: 20px;">
            <span
              style="font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; padding: 4px 10px; background: var(--color-surface-base); border: 1px solid var(--color-border-strong); border-radius: 3px; color: var(--color-text-primary);"
            >
              {{ heroGame.tag }}
            </span>
          </div>
          <!-- Duration badge -->
          <div style="position: absolute; bottom: 20px; left: 20px;">
            <span
              style="font-family: var(--font-mono); font-size: 11px; letter-spacing: 0.06em; padding: 5px 10px; background: rgba(0,0,0,0.7); backdrop-filter: blur(6px); border-radius: 4px; color: #fff;"
            >
              {{ formatDuration(hero.duration) }}
            </span>
          </div>
          <!-- Play button overlay -->
          <button
            style="position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; background: transparent; cursor: pointer;"
            @click="router.push({ name: 'clip', params: { id: hero.id } })"
          >
            <span
              style="width: 72px; height: 72px; border-radius: 50%; background: rgba(0,0,0,0.55); border: 1px solid rgba(255,255,255,0.2); display: inline-flex; align-items: center; justify-content: center; backdrop-filter: blur(6px);"
            >
              <svg width="26" height="26" viewBox="0 0 24 24" fill="#fff">
                <path d="M8 5v14l11-7L8 5z" />
              </svg>
            </span>
          </button>
        </div>

        <!-- Right: content -->
        <div
          style="padding: 40px 44px; display: flex; flex-direction: column; justify-content: space-between;"
        >
          <!-- Top content -->
          <div style="display: flex; flex-direction: column; gap: 16px;">
            <div
              style="font-family: var(--font-mono); font-size: 11px; color: var(--color-neon); letter-spacing: 0.15em; text-transform: uppercase;"
            >
              Featured Clip
            </div>
            <h2
              style="font-family: var(--font-heading); font-weight: 700; font-size: 46px; line-height: 1; text-transform: uppercase; margin: 0; color: var(--color-text-primary);"
            >
              {{ hero.title }}
            </h2>
            <p
              style="color: var(--color-text-secondary); font-size: 15px; max-width: 36ch; margin: 0; line-height: 1.5;"
            >
              {{ heroGame.name }} · uploaded {{ hero.createdAt }} ago by
              <span style="color: var(--color-text-primary);">@{{ heroUser.username }}</span>
            </p>
          </div>

          <!-- Stats row -->
          <div
            style="display: flex; gap: 28px; padding: 16px 0; border-top: 1px solid var(--color-border); border-bottom: 1px solid var(--color-border); margin: 20px 0; font-family: var(--font-mono);"
          >
            <div style="display: flex; flex-direction: column; gap: 4px;">
              <span
                style="font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;"
              >Views</span>
              <span
                style="font-family: var(--font-heading); font-weight: 700; font-size: 22px; color: var(--color-text-primary);"
              >{{ formatNum(hero.views) }}</span>
            </div>
            <div style="display: flex; flex-direction: column; gap: 4px;">
              <span
                style="font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;"
              >Likes</span>
              <span
                style="font-family: var(--font-heading); font-weight: 700; font-size: 22px; color: var(--color-text-primary);"
              >{{ formatNum(hero.likes) }}</span>
            </div>
            <div style="display: flex; flex-direction: column; gap: 4px;">
              <span
                style="font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;"
              >Duration</span>
              <span
                style="font-family: var(--font-heading); font-weight: 700; font-size: 22px; color: var(--color-text-primary);"
              >{{ formatDuration(hero.duration) }}</span>
            </div>
          </div>

          <!-- CTA row -->
          <div style="display: flex; align-items: center; gap: 12px;">
            <button
              style="padding: 10px 24px; background: var(--color-brand); color: #fff; font-family: var(--font-mono); font-size: 11px; letter-spacing: 0.12em; text-transform: uppercase; border-radius: var(--radius-sm); border: none; cursor: pointer; transition: background 150ms;"
              @click="router.push({ name: 'clip', params: { id: hero.id } })"
              @mouseenter="($event.currentTarget as HTMLElement).style.background = 'var(--color-brand-light)'"
              @mouseleave="($event.currentTarget as HTMLElement).style.background = 'var(--color-brand)'"
            >
              Watch Now
            </button>
            <!-- Author pill -->
            <button
              style="display: flex; align-items: center; gap: 8px; padding: 8px 14px 8px 8px; border-radius: 100px; background: var(--color-surface-overlay); border: 1px solid var(--color-border); cursor: pointer; transition: border-color 150ms;"
              @click="router.push({ name: 'user', params: { username: heroUser.username } })"
              @mouseenter="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border-hover)'"
              @mouseleave="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border)'"
            >
              <UserAvatar :user="hero.user" :size="28" />
              <span
                style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-secondary); letter-spacing: 0.04em;"
              >@{{ heroUser.username }}</span>
            </button>
          </div>
        </div>
      </div>
    </div>

    <!-- Feed toolbar -->
    <div
      style="display: flex; align-items: center; gap: 12px; margin-bottom: 28px; padding: 12px 0; border-bottom: 1px solid var(--color-border); flex-wrap: wrap;"
    >
      <button
        v-for="f in GAME_FILTERS"
        :key="f.key"
        :style="
          filter === f.key
            ? 'padding: 6px 12px; font-family: var(--font-mono); font-size: 11px; text-transform: uppercase; border-radius: 100px; border: 1px solid var(--color-text-primary); background: var(--color-text-primary); color: var(--color-surface-base); cursor: pointer;'
            : 'padding: 6px 12px; font-family: var(--font-mono); font-size: 11px; text-transform: uppercase; border-radius: 100px; border: 1px solid var(--color-border); background: transparent; color: var(--color-text-muted); cursor: pointer;'
        "
        @click="filter = f.key"
      >
        {{ f.label }}
      </button>

      <!-- Sort -->
      <div
        style="margin-left: auto; display: flex; align-items: center; gap: 8px; font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); text-transform: uppercase;"
      >
        <span>Sort:</span>
        <select
          v-model="sort"
          style="background: var(--color-surface-raised); color: var(--color-text-primary); border: 1px solid var(--color-border); padding: 6px 10px; border-radius: var(--radius-sm); font-family: var(--font-mono); font-size: 11px; cursor: pointer; outline: none;"
        >
          <option value="hot">Hot</option>
          <option value="new">New</option>
          <option value="top">Top</option>
        </select>
      </div>
    </div>

    <!-- Feed grid -->
    <div class="feed-grid">
      <ClipCard
        v-for="clip in filteredClips"
        :key="clip.id"
        :clip="clip"
        @click="router.push({ name: 'clip', params: { id: clip.id } })"
      />
    </div>

    <!-- Rising in your games -->
    <div>
      <div
        style="display: flex; align-items: baseline; justify-content: space-between; margin: 48px 0 20px; gap: 16px;"
      >
        <h2
          class="section-title-bar"
          style="font-family: var(--font-heading); font-weight: 700; font-size: 24px; text-transform: uppercase; margin: 0; display: flex; align-items: center; gap: 14px; color: var(--color-text-primary);"
        >
          Rising in Your Games
        </h2>
        <a
          href="#"
          style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-secondary); text-transform: uppercase; letter-spacing: 0.06em; white-space: nowrap;"
        >See All →</a>
      </div>

      <div class="feed-grid">
        <ClipCard
          v-for="clip in secondaryClips"
          :key="clip.id"
          :clip="clip"
          @click="router.push({ name: 'clip', params: { id: clip.id } })"
        />
      </div>
    </div>
  </main>
</template>

<style>
@media (max-width: 899px) {
  .page {
    padding: 16px 14px 80px !important;
  }
}

@media (min-width: 900px) {
  .hero {
    display: block;
  }
}

.hero {
  display: none;
}
</style>
