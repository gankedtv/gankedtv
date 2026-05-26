import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { trackPageView } from '@/lib/analytics'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('@/views/HomeView.vue'),
    },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
    },
    {
      path: '/register',
      name: 'register',
      component: () => import('@/views/RegisterView.vue'),
    },
    {
      path: '/upload',
      name: 'upload',
      component: () => import('@/views/UploadView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/settings/password',
      name: 'settings-password',
      component: () => import('@/views/SettingsPasswordView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/games',
      name: 'games',
      component: () => import('@/views/GamesView.vue'),
    },
    {
      path: '/game/:slug',
      name: 'game-detail',
      component: () => import('@/views/GameView.vue'),
    },
    {
      path: '/tag/:slug',
      name: 'tag-detail',
      component: () => import('@/views/TagView.vue'),
    },
    {
      path: '/trending',
      name: 'trending',
      component: () => import('@/views/TrendingView.vue'),
    },
    {
      path: '/leaderboards',
      name: 'leaderboards',
      component: () => import('@/views/LeaderboardsView.vue'),
    },
    {
      path: '/search',
      name: 'search',
      component: () => import('@/views/SearchView.vue'),
    },
    {
      path: '/clip/:id',
      name: 'clip',
      component: () => import('@/views/ClipView.vue'),
    },
    {
      path: '/feed/reels',
      name: 'reels',
      component: () => import('@/views/ReelsView.vue'),
    },
    {
      path: '/feed/reels/:id',
      name: 'reel-clip',
      component: () => import('@/views/ReelsView.vue'),
    },
    {
      path: '/c/:code',
      name: 'clip-share',
      component: () => import('@/views/ClipView.vue'),
    },
    {
      path: '/user/:username',
      name: 'user',
      component: () => import('@/views/UserView.vue'),
    },
    {
      path: '/user/:username/followers',
      name: 'user-followers',
      component: () => import('@/views/UserFollowListView.vue'),
      // `kind` lives on meta rather than being parsed from the path inside the
      // view so a single component can serve both /followers and /following
      // without URL-sniffing.
      meta: { kind: 'followers' },
    },
    {
      path: '/user/:username/following',
      name: 'user-following',
      component: () => import('@/views/UserFollowListView.vue'),
      meta: { kind: 'following' },
    },
    {
      path: '/notifications',
      name: 'notifications',
      component: () => import('@/views/NotificationsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/admin',
      name: 'admin',
      component: () => import('@/views/AdminView.vue'),
      // Moderator covers admin too (admin is a superset) — mirrors RoleAuthorization on the
      // server, so the gate is identical on both sides.
      meta: { requiresAuth: true, requiresRole: 'moderator' as const },
    },
    {
      path: '/auth/callback',
      name: 'auth-callback',
      component: () => import('@/views/AuthCallbackView.vue'),
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      component: () => import('@/views/NotFoundView.vue'),
    },
  ],
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (to.meta.requiresAuth && !auth.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
  // Role gates redirect to home (not 403) so users who navigate to /admin via address-bar
  // typing land somewhere usable. The nav link itself is hidden for non-mods.
  const required = (to.meta as { requiresRole?: 'admin' | 'moderator' }).requiresRole
  if (required === 'admin' && !auth.isAdmin) {
    return { name: 'home' }
  }
  if (required === 'moderator' && !auth.isModerator) {
    return { name: 'home' }
  }
})

// Emit SPA page views after each successful navigation. No-op until analytics is initialised
// with a measurement id (production only), so this is inert in dev and tests.
router.afterEach((to) => {
  trackPageView(to.fullPath)
})

export default router
