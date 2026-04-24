<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { USERS, CLIPS, GAMES, formatNum, userByUsername } from '@/lib/mock-data'
import UserAvatar from '@/components/UserAvatar.vue'
import ClipCard from '@/components/ClipCard.vue'

const route = useRoute()
const router = useRouter()

const resolved = computed(() => {
  const r = userByUsername(route.params.username as string)
  return r ?? (['phantomveil', USERS.phantomveil] as [string, (typeof USERS)[string]])
})

const userKey = computed(() => resolved.value[0])
const user = computed(() => resolved.value[1])

const bannerGradient = computed(
  () =>
    `linear-gradient(135deg, ${user.value.avatar}, color-mix(in oklab, ${user.value.avatar} 20%, #000))`,
)

const avatarGradient = computed(
  () =>
    `linear-gradient(135deg, ${user.value.avatar}, color-mix(in oklab, ${user.value.avatar} 20%, #000))`,
)

const initials = computed(() =>
  user.value.display
    .replace(/[^a-zA-Z]/g, '')
    .slice(0, 2)
    .toUpperCase() || '??',
)

const userClips = computed(() =>
  CLIPS.filter((c) => c.user === userKey.value).concat(CLIPS.slice(0, 8)),
)

// Aggregate stats derived from clips
const totalPlays = computed(() => userClips.value.reduce((s, c) => s + c.views, 0))
const totalLikes = computed(() => userClips.value.reduce((s, c) => s + c.likes, 0))
const avgLikes = computed(() =>
  userClips.value.length ? Math.round(totalLikes.value / userClips.value.length) : 0,
)

// Games the user has clips in
const userGameKeys = computed(() => {
  const seen = new Set<string>()
  for (const c of userClips.value) {
    if (!seen.has(c.game)) seen.add(c.game)
  }
  return [...seen].slice(0, 5)
})

const gameClipCount = computed(() => {
  const counts: Record<string, number> = {}
  for (const c of userClips.value) {
    counts[c.game] = (counts[c.game] ?? 0) + 1
  }
  return counts
})

type Tab = 'clips' | 'liked' | 'about' | 'followers'
const tab = ref<Tab>('clips')

const following = ref(false)

const TABS: { key: Tab; label: string }[] = [
  { key: 'clips', label: 'Clips' },
  { key: 'liked', label: 'Liked' },
  { key: 'about', label: 'About' },
  { key: 'followers', label: 'Followers' },
]

const sort = ref('recent')

// Follower cards — other users
const followerUsers = computed(() =>
  Object.entries(USERS)
    .filter(([k]) => k !== userKey.value)
    .slice(0, 8),
)

const joinedDate = 'Jan 2024'
</script>

<template>
  <main style="position: relative;">
    <!-- ===================== BANNER ===================== -->
    <div
      class="banner"
      :style="{
        position: 'relative',
        height: '280px',
        background: bannerGradient,
        overflow: 'hidden',
      }"
    >
      <!-- Stripe texture -->
      <div
        style="position: absolute; inset: 0; background: repeating-linear-gradient(45deg, rgba(255,255,255,0.04) 0 12px, transparent 12px 24px);"
      ></div>
      <!-- Fade to base at bottom -->
      <div
        style="position: absolute; inset: 0; background: linear-gradient(0deg, var(--color-surface-base), transparent 60%);"
      ></div>

      <!-- Breadcrumb -->
      <div
        class="inner-wrap"
        style="position: absolute; top: 24px; left: 0; right: 0;"
      >
        <div
          style="max-width: 1280px; margin: 0 auto; padding: 0 24px;"
        >
          <button
            style="font-family: var(--font-mono); font-size: 11px; color: rgba(255,255,255,0.55); letter-spacing: 0.08em; text-transform: uppercase; background: none; border: none; cursor: pointer; padding: 0; display: flex; align-items: center; gap: 6px;"
            @click="router.push({ name: 'home' })"
          >
            ← Feed / @{{ user.username }}
          </button>
        </div>
      </div>
    </div>

    <!-- ===================== INNER CONTENT ===================== -->
    <div style="max-width: 1280px; margin: 0 auto; padding: 0 24px 120px;">

      <!-- ---- Profile header ---- -->
      <div
        style="margin-top: -70px; display: flex; gap: 28px; align-items: flex-start; flex-wrap: wrap;"
      >
        <!-- Large avatar -->
        <div
          :style="{
            width: '140px',
            height: '140px',
            borderRadius: '50%',
            flexShrink: '0',
            border: '4px solid var(--color-surface-base)',
            background: avatarGradient,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            fontFamily: 'var(--font-heading)',
            fontWeight: '700',
            fontSize: '56px',
            color: '#fff',
            letterSpacing: '-0.02em',
            userSelect: 'none',
          }"
        >
          {{ initials }}
        </div>

        <!-- User info -->
        <div style="flex: 1; min-width: 220px; padding-top: 76px;">
          <!-- Eyebrow -->
          <div
            style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); letter-spacing: 0.08em; text-transform: uppercase; margin-bottom: 6px;"
          >
            {{ user.verified ? 'Verified Creator / Player' : 'Player' }} · Joined {{ joinedDate }}
          </div>

          <!-- Display name + verified badge -->
          <div style="display: flex; align-items: center; gap: 10px; flex-wrap: wrap;">
            <h1
              style="font-family: var(--font-heading); font-weight: 700; font-size: 44px; text-transform: uppercase; margin: 0; line-height: 1; color: var(--color-text-primary); letter-spacing: 0.02em;"
            >
              {{ user.display }}
            </h1>
            <svg
              v-if="user.verified"
              width="22"
              height="22"
              viewBox="0 0 24 24"
              fill="none"
              style="flex-shrink: 0; margin-top: 4px;"
            >
              <circle cx="12" cy="12" r="11" fill="var(--color-brand)" />
              <path
                d="M7 12.5l3.5 3.5 6.5-7"
                stroke="#fff"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </svg>
          </div>

          <!-- Handle -->
          <div
            style="font-family: var(--font-mono); font-size: 14px; color: var(--color-neon); margin-top: 6px; letter-spacing: 0.04em;"
          >
            @{{ user.username }}
          </div>

          <!-- Bio -->
          <p
            style="color: var(--color-text-secondary); font-size: 14px; max-width: 520px; margin: 10px 0 0; line-height: 1.55;"
          >
            Grinding the ranked ladder one clip at a time. Content creator &amp; full-time gamer.
            Clips, vods, and the occasional tutorial.
          </p>
        </div>

        <!-- Action buttons -->
        <div
          style="display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding-top: 76px;"
        >
          <!-- Follow / Following -->
          <button
            :style="{
              padding: '9px 22px',
              background: following ? 'transparent' : 'var(--color-brand)',
              color: following ? 'var(--color-text-primary)' : '#fff',
              border: following ? '1px solid var(--color-border-strong)' : '1px solid transparent',
              borderRadius: 'var(--radius-sm)',
              fontFamily: 'var(--font-mono)',
              fontSize: '11px',
              letterSpacing: '0.1em',
              textTransform: 'uppercase',
              cursor: 'pointer',
              transition: 'all 150ms',
            }"
            @click="following = !following"
          >
            {{ following ? 'Following' : 'Follow' }}
          </button>

          <!-- Share -->
          <button
            style="width: 36px; height: 36px; border-radius: var(--radius-sm); border: 1px solid var(--color-border); background: var(--color-surface-raised); color: var(--color-text-secondary); cursor: pointer; display: flex; align-items: center; justify-content: center; transition: border-color 150ms;"
            @mouseenter="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border-hover)'"
            @mouseleave="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border)'"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
              <path d="M18 16a3 3 0 00-2.4 1.2L8.7 13.1c.05-.34.08-.69.08-1.04 0-.36-.03-.71-.08-1.05l6.9-4.07A3 3 0 1014.4 4.5l-7.04 4.15A3 3 0 103 12a3 3 0 001.36-.33l7.04 4.15A3 3 0 1018 19a3 3 0 000-3z"/>
            </svg>
          </button>

          <!-- More -->
          <button
            style="width: 36px; height: 36px; border-radius: var(--radius-sm); border: 1px solid var(--color-border); background: var(--color-surface-raised); color: var(--color-text-secondary); cursor: pointer; display: flex; align-items: center; justify-content: center; transition: border-color 150ms;"
            @mouseenter="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border-hover)'"
            @mouseleave="($event.currentTarget as HTMLElement).style.borderColor = 'var(--color-border)'"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor">
              <circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/>
            </svg>
          </button>
        </div>
      </div>

      <!-- ---- Stat block ---- -->
      <div
        class="stat-grid"
        style="margin-top: 28px; border: 1px solid var(--color-border); border-radius: var(--radius-md); overflow: hidden; background: var(--color-border);"
      >
        <div
          v-for="stat in [
            { label: 'Clips', value: formatNum(user.clips) },
            { label: 'Followers', value: formatNum(user.followers) },
            { label: 'Following', value: '284' },
            { label: 'Total plays', value: formatNum(totalPlays) },
            { label: 'Total likes', value: formatNum(totalLikes) },
            { label: 'Avg / clip', value: formatNum(avgLikes) },
          ]"
          :key="stat.label"
          style="background: var(--color-surface-raised); padding: 16px 20px; display: flex; flex-direction: column; gap: 4px;"
        >
          <span
            style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;"
          >{{ stat.label }}</span>
          <span
            style="font-family: var(--font-heading); font-weight: 700; font-size: 22px; color: var(--color-text-primary); line-height: 1;"
          >{{ stat.value }}</span>
        </div>
      </div>

      <!-- ---- Main arsenal ---- -->
      <div
        style="margin-top: 20px; background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); padding: 16px 20px;"
      >
        <div
          style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.1em; margin-bottom: 14px;"
        >
          Main Arsenal
        </div>
        <div style="display: flex; gap: 10px; flex-wrap: wrap;">
          <div
            v-for="gk in userGameKeys"
            :key="gk"
            style="display: flex; align-items: center; gap: 9px; padding: 6px 14px 6px 6px; background: var(--color-surface-overlay); border: 1px solid var(--color-border); border-radius: 100px;"
          >
            <!-- Game circle thumb -->
            <div
              style="width: 28px; height: 28px; border-radius: 50%; overflow: hidden; flex-shrink: 0; display: flex; align-items: center; justify-content: center; font-family: var(--font-mono); font-size: 9px; font-weight: 700; color: #fff; letter-spacing: 0.04em;"
              :style="{ backgroundImage: `url(${GAMES[gk].art})`, backgroundSize: 'cover' }"
            ></div>
            <div style="display: flex; flex-direction: column; gap: 1px; line-height: 1;">
              <span
                style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-primary); font-weight: 500;"
              >{{ GAMES[gk].name }}</span>
              <span
                style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted);"
              >{{ gameClipCount[gk] }} clips</span>
            </div>
          </div>
        </div>
      </div>

      <!-- ---- Tabs ---- -->
      <div style="margin-top: 36px;">
        <!-- Tab bar -->
        <div
          style="display: flex; align-items: center; border-bottom: 1px solid var(--color-border);"
        >
          <div style="display: flex; gap: 0; flex: 1;">
            <button
              v-for="t in TABS"
              :key="t.key"
              class="tab-btn"
              :class="{ active: tab === t.key }"
              @click="tab = t.key"
            >
              {{ t.label }}
            </button>
          </div>

          <!-- Sort -->
          <div
            v-if="tab === 'clips'"
            style="display: flex; align-items: center; gap: 8px; font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); text-transform: uppercase; padding-bottom: 8px;"
          >
            <span>Sort:</span>
            <select
              v-model="sort"
              style="background: var(--color-surface-raised); color: var(--color-text-primary); border: 1px solid var(--color-border); padding: 5px 10px; border-radius: var(--radius-sm); font-family: var(--font-mono); font-size: 11px; cursor: pointer; outline: none;"
            >
              <option value="recent">Recent</option>
              <option value="top">Top</option>
              <option value="views">Views</option>
            </select>
          </div>
        </div>

        <!-- Tab content -->
        <div style="margin-top: 24px;">

          <!-- Clips tab -->
          <div v-if="tab === 'clips'">
            <div class="feed-grid">
              <ClipCard
                v-for="clip in userClips"
                :key="clip.id"
                :clip="clip"
                @click="router.push({ name: 'clip', params: { id: clip.id } })"
              />
            </div>
          </div>

          <!-- Liked tab -->
          <div
            v-else-if="tab === 'liked'"
            style="display: flex; align-items: center; justify-content: center; padding: 80px 0;"
          >
            <p
              style="font-family: var(--font-mono); font-size: 13px; color: var(--color-text-muted); letter-spacing: 0.06em;"
            >
              Liked clips are private.
            </p>
          </div>

          <!-- About tab -->
          <div v-else-if="tab === 'about'">
            <div class="about-grid">
              <div
                v-for="card in [
                  { label: 'Peak Rank', value: 'Diamond I', icon: '◆' },
                  { label: 'Preferred Role', value: 'Duelist / Entry', icon: '⚡' },
                  { label: 'Socials', value: 'twitch · youtube · x', icon: '🔗' },
                  { label: 'Rig', value: 'RTX 4080 · i9-13900K', icon: '🖥' },
                  { label: 'Joined', value: joinedDate, icon: '📅' },
                  { label: 'Region', value: 'NA East', icon: '🌎' },
                ]"
                :key="card.label"
                style="background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); padding: 20px 24px; display: flex; flex-direction: column; gap: 8px;"
              >
                <div style="font-size: 20px; line-height: 1;">{{ card.icon }}</div>
                <div
                  style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;"
                >
                  {{ card.label }}
                </div>
                <div
                  style="font-family: var(--font-heading); font-weight: 700; font-size: 18px; color: var(--color-text-primary); line-height: 1.2;"
                >
                  {{ card.value }}
                </div>
              </div>
            </div>
          </div>

          <!-- Followers tab -->
          <div v-else-if="tab === 'followers'">
            <div class="follower-grid">
              <div
                v-for="[fk, fu] in followerUsers"
                :key="fk"
                style="background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); padding: 20px; display: flex; flex-direction: column; align-items: center; gap: 10px; text-align: center;"
              >
                <UserAvatar :user="fk" :size="52" />
                <div style="display: flex; flex-direction: column; gap: 3px;">
                  <span
                    style="font-family: var(--font-heading); font-weight: 700; font-size: 16px; color: var(--color-text-primary); text-transform: uppercase; letter-spacing: 0.04em;"
                  >{{ fu.display }}</span>
                  <span
                    style="font-family: var(--font-mono); font-size: 11px; color: var(--color-neon);"
                  >@{{ fu.username }}</span>
                </div>
                <button
                  style="margin-top: 4px; padding: 6px 18px; background: var(--color-brand); color: #fff; border: none; border-radius: var(--radius-sm); font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.1em; text-transform: uppercase; cursor: pointer; transition: background 150ms;"
                  @mouseenter="($event.currentTarget as HTMLElement).style.background = 'var(--color-brand-light)'"
                  @mouseleave="($event.currentTarget as HTMLElement).style.background = 'var(--color-brand)'"
                  @click="router.push({ name: 'user', params: { username: fu.username } })"
                >
                  Follow
                </button>
              </div>
            </div>
          </div>

        </div>
      </div>
    </div>
  </main>
</template>

<style scoped>
/* Stat grid: auto-fit columns separated by 1px border gaps */
.stat-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 1px;
}

/* Tab button */
.tab-btn {
  font-family: var(--font-mono);
  font-size: 12px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  padding: 12px 18px;
  background: none;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  position: relative;
  transition: color 150ms;
}

.tab-btn:hover {
  color: var(--color-text-primary);
}

.tab-btn.active {
  color: var(--color-text-primary);
}

.tab-btn.active::after {
  content: '';
  position: absolute;
  bottom: -1px;
  left: 0;
  right: 0;
  height: 2px;
  background: var(--color-brand-light);
  border-radius: 2px 2px 0 0;
}

/* About info cards */
.about-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 12px;
}

/* Followers grid */
.follower-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: 12px;
}
</style>
