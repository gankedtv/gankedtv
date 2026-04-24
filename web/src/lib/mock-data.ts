// Mock data for GankedTV concept UI — replaced by real API calls in future

function makeArt(label: string, hue1: string, hue2: string, glyph: string): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" preserveAspectRatio="xMidYMid slice">
    <defs>
      <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0" stop-color="${hue1}"/>
        <stop offset="1" stop-color="${hue2}"/>
      </linearGradient>
      <pattern id="stripes" width="28" height="28" patternUnits="userSpaceOnUse" patternTransform="rotate(28)">
        <rect width="28" height="28" fill="transparent"/>
        <rect width="14" height="28" fill="rgba(255,255,255,0.05)"/>
      </pattern>
      <radialGradient id="v" cx="50%" cy="45%" r="70%">
        <stop offset="0" stop-color="rgba(255,255,255,0.15)"/>
        <stop offset="1" stop-color="rgba(0,0,0,0.6)"/>
      </radialGradient>
    </defs>
    <rect width="640" height="360" fill="url(#g)"/>
    <rect width="640" height="360" fill="url(#stripes)"/>
    <rect width="640" height="360" fill="url(#v)"/>
    <text x="32" y="180" font-family="Rajdhani, sans-serif" font-weight="700" font-size="120" fill="rgba(255,255,255,0.12)" letter-spacing="0.02em">${glyph}</text>
    <text x="32" y="330" font-family="'DM Mono', monospace" font-size="12" fill="rgba(255,255,255,0.55)" letter-spacing="0.15em">${label}</text>
  </svg>`
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`
}

export interface Game {
  name: string
  slug: string
  tag: string
  art: string
}

export interface User {
  username: string
  display: string
  avatar: string
  clips: number
  followers: number
  verified: boolean
}

export interface Clip {
  id: string
  title: string
  game: keyof typeof GAMES
  user: keyof typeof USERS
  duration: number
  likes: number
  views: number
  createdAt: string
  art: string
}

export interface Comment {
  user: string
  text: string
  time: string
  likes: number
}

export const GAMES: Record<string, Game> = {
  valorant: {
    name: 'Valorant',
    slug: 'valorant',
    tag: 'VALORANT',
    art: makeArt('VALORANT', '#3d1a1a', '#180808', 'VLR'),
  },
  minecraft: {
    name: 'Minecraft',
    slug: 'minecraft',
    tag: 'MINECRAFT',
    art: makeArt('MINECRAFT', '#2a4a1e', '#0d1a08', 'MC'),
  },
  rocket: {
    name: 'Rocket League',
    slug: 'rocket-league',
    tag: 'RL',
    art: makeArt('ROCKET LEAGUE', '#1a2e5a', '#060d24', 'RL'),
  },
  overwatch: {
    name: 'Overwatch 2',
    slug: 'overwatch-2',
    tag: 'OW2',
    art: makeArt('OVERWATCH 2', '#5a3210', '#241608', 'OW'),
  },
  fortnite: {
    name: 'Fortnite',
    slug: 'fortnite',
    tag: 'FORTNITE',
    art: makeArt('FORTNITE', '#3d1a52', '#120620', 'FN'),
  },
  league: {
    name: 'League of Legends',
    slug: 'league-of-legends',
    tag: 'LoL',
    art: makeArt('LEAGUE OF LEGENDS', '#1a3a5a', '#061424', 'LoL'),
  },
}

export const USERS: Record<string, User> = {
  phantomveil: {
    username: 'phantomveil',
    display: 'PhantomVeil',
    avatar: '#6d28d9',
    clips: 142,
    followers: 8420,
    verified: true,
  },
  nyxproto: {
    username: 'nyx.proto',
    display: 'nyx.proto',
    avatar: '#00e5a0',
    clips: 87,
    followers: 12300,
    verified: true,
  },
  kael_rs: {
    username: 'kael_rs',
    display: 'Kael',
    avatar: '#ff7a00',
    clips: 63,
    followers: 2100,
    verified: false,
  },
  sundownr: {
    username: 'sundownr',
    display: 'SUNDOWNR',
    avatar: '#ff3d8b',
    clips: 221,
    followers: 45800,
    verified: true,
  },
  octaneEU: {
    username: 'octane.eu',
    display: 'octane',
    avatar: '#00aaff',
    clips: 29,
    followers: 560,
    verified: false,
  },
  wrenhowl: {
    username: 'wrenhowl',
    display: 'wren howl',
    avatar: '#ffe600',
    clips: 104,
    followers: 5240,
    verified: false,
  },
  ghostpatch: {
    username: 'ghost_patch',
    display: 'ghost_patch',
    avatar: '#b47cff',
    clips: 38,
    followers: 1420,
    verified: false,
  },
  rustyquill: {
    username: 'rustyquill',
    display: 'rustyquill',
    avatar: '#e53e3e',
    clips: 71,
    followers: 3380,
    verified: false,
  },
  vexx: {
    username: 'vexx',
    display: 'vexx',
    avatar: '#00e5a0',
    clips: 55,
    followers: 1920,
    verified: false,
  },
}

export const CLIPS: Clip[] = [
  {
    id: 'clp_01',
    title: 'Unreal 1v5 clutch on Bind — final round',
    game: 'valorant',
    user: 'phantomveil',
    duration: 42,
    likes: 12400,
    views: 284000,
    createdAt: '2h',
    art: makeArt('VALORANT — BIND', '#4a1a2a', '#1a0814', '01'),
  },
  {
    id: 'clp_02',
    title: 'Self-redirect into 50/50 → solo goal, no touch',
    game: 'rocket',
    user: 'nyxproto',
    duration: 18,
    likes: 8210,
    views: 92000,
    createdAt: '4h',
    art: makeArt('CHAMPIONSHIP FIELD', '#1a2e5a', '#060d24', '02'),
  },
  {
    id: 'clp_03',
    title: 'stacked redstone piston door w/ hidden sorter',
    game: 'minecraft',
    user: 'wrenhowl',
    duration: 55,
    likes: 3120,
    views: 41200,
    createdAt: '6h',
    art: makeArt('SURVIVAL — SMP', '#2a4a1e', '#0d1a08', '03'),
  },
  {
    id: 'clp_04',
    title: 'Hanzo 6k w/ dragonstrike through payload',
    game: 'overwatch',
    user: 'sundownr',
    duration: 12,
    likes: 22700,
    views: 512000,
    createdAt: '8h',
    art: makeArt('ILIOS LIGHTHOUSE', '#5a3210', '#241608', '04'),
  },
  {
    id: 'clp_05',
    title: 'Zero-build W-key squad wipe, final circle',
    game: 'fortnite',
    user: 'kael_rs',
    duration: 34,
    likes: 1840,
    views: 28400,
    createdAt: '11h',
    art: makeArt('FORTNITE — CH5', '#3d1a52', '#120620', '05'),
  },
  {
    id: 'clp_06',
    title: 'Pentakill from fountain → Baron steal',
    game: 'league',
    user: 'rustyquill',
    duration: 28,
    likes: 15200,
    views: 180000,
    createdAt: '14h',
    art: makeArt('SUMMONERS RIFT', '#1a3a5a', '#061424', '06'),
  },
  {
    id: 'clp_07',
    title: 'Operator 1-tap through smoke, pixel-perfect',
    game: 'valorant',
    user: 'ghostpatch',
    duration: 8,
    likes: 4520,
    views: 88000,
    createdAt: '16h',
    art: makeArt('HAVEN — A SITE', '#4a1a2a', '#1a0814', '07'),
  },
  {
    id: 'clp_08',
    title: 'Musty flick into double-touch redirect',
    game: 'rocket',
    user: 'octaneEU',
    duration: 14,
    likes: 1120,
    views: 14000,
    createdAt: '19h',
    art: makeArt('MANNFIELD', '#1a2e5a', '#060d24', '08'),
  },
  {
    id: 'clp_09',
    title: '36-stack wither farm in the End',
    game: 'minecraft',
    user: 'vexx',
    duration: 62,
    likes: 760,
    views: 6800,
    createdAt: '21h',
    art: makeArt('THE END', '#2a4a1e', '#0d1a08', '09'),
  },
  {
    id: 'clp_10',
    title: 'Genji blade through every ult, 4k',
    game: 'overwatch',
    user: 'sundownr',
    duration: 16,
    likes: 9310,
    views: 142000,
    createdAt: '1d',
    art: makeArt('KINGS ROW', '#5a3210', '#241608', '10'),
  },
  {
    id: 'clp_11',
    title: 'End-game build 1v3 into Victory Royale',
    game: 'fortnite',
    user: 'kael_rs',
    duration: 24,
    likes: 2410,
    views: 34500,
    createdAt: '1d',
    art: makeArt('BATTLE ROYALE', '#3d1a52', '#120620', '11'),
  },
  {
    id: 'clp_12',
    title: 'Fizz outplay through wall, 500 IQ flash',
    game: 'league',
    user: 'phantomveil',
    duration: 11,
    likes: 6240,
    views: 74000,
    createdAt: '1d',
    art: makeArt('MID LANE', '#1a3a5a', '#061424', '12'),
  },
]

export const COMMENTS: Comment[] = [
  {
    user: 'ghost_patch',
    text: 'the pre-aim on that last angle was UNREAL. tap me if you want my crosshair settings.',
    time: '12m',
    likes: 84,
  },
  { user: 'sundownr', text: "if this doesn't hit front page I'm rioting", time: '38m', likes: 156 },
  { user: 'octane.eu', text: 'bro reset 4 times before the spray what', time: '1h', likes: 42 },
  { user: 'vexx', text: 'which agent is that? you using jett?', time: '1h', likes: 12 },
  {
    user: 'wrenhowl',
    text: 'watch the minimap at 0:22 — he baited the rotation perfectly',
    time: '2h',
    likes: 67,
  },
]

export function formatNum(n: number): string {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M'
  if (n >= 1_000) return (n / 1_000).toFixed(1) + 'K'
  return String(n)
}

export function formatDuration(s: number): string {
  const m = Math.floor(s / 60)
  const r = s % 60
  return `${m}:${String(r).padStart(2, '0')}`
}

export function userByUsername(username: string): [string, User] | undefined {
  const key = Object.keys(USERS).find((k) => USERS[k].username === username)
  return key ? [key, USERS[key]] : undefined
}

export function clipById(id: string): Clip | undefined {
  return CLIPS.find((c) => c.id === id)
}
