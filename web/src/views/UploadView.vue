<script setup lang="ts">
import { ref, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { GAMES } from '@/lib/mock-data'

const router = useRouter()

// State
const step = ref<1 | 2 | 3>(1)
const file = ref<{ name: string; size: number; type: string } | null>(null)
const title = ref('')
const desc = ref('')
const game = ref('valorant')
const visibility = ref<'public' | 'unlisted'>('public')
const progress = ref(0)
const dragging = ref(false)

// Upload simulation
let uploadInterval: ReturnType<typeof setInterval> | null = null

function startUpload() {
  step.value = 3
  progress.value = 0
  uploadInterval = setInterval(() => {
    const increment = 5 + Math.random() * 5
    progress.value = Math.min(100, progress.value + increment)
    if (progress.value >= 100) {
      progress.value = 100
      if (uploadInterval) clearInterval(uploadInterval)
    }
  }, 180)
}

onUnmounted(() => {
  if (uploadInterval) clearInterval(uploadInterval)
})

// File handling
function handleFileSelect(e: Event) {
  const input = e.target as HTMLInputElement
  if (input.files?.[0]) {
    const f = input.files[0]
    file.value = { name: f.name, size: f.size, type: f.type }
  }
}

function handleDrop(e: DragEvent) {
  dragging.value = false
  const dropped = e.dataTransfer?.files?.[0]
  if (dropped) {
    file.value = { name: dropped.name, size: dropped.size, type: dropped.type }
  }
}

function formatSize(bytes: number): string {
  if (bytes >= 1_073_741_824) return (bytes / 1_073_741_824).toFixed(1) + ' GB'
  if (bytes >= 1_048_576) return (bytes / 1_048_576).toFixed(1) + ' MB'
  return (bytes / 1024).toFixed(1) + ' KB'
}

const STEPS = [
  { num: '1', label: 'Select file' },
  { num: '2', label: 'Describe' },
  { num: '3', label: 'Upload' },
]

const SOURCES = ['OBS', 'ShadowPlay', 'Medal', 'Xbox', 'PS5', 'Switch']

const GAME_KEYS = Object.keys(GAMES)

// Checklist timing
function checklistDone(index: number): boolean {
  if (index === 0) return progress.value >= 30
  if (index === 1) return progress.value >= 80
  return progress.value >= 100
}

const inputStyle = 'width: 100%; padding: 12px 14px; background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); color: var(--color-text-primary); font-family: var(--font-body); font-size: 14px; outline: none;'
const labelStyle = 'font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted); letter-spacing: 0.1em; text-transform: uppercase; display: block; margin-bottom: 6px;'
</script>

<template>
  <main style="max-width: 900px; margin: 0 auto; padding: 32px 24px 120px;">

    <!-- Page header -->
    <div style="margin-bottom: 28px;">
      <div
        style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); letter-spacing: 0.1em; text-transform: uppercase; margin-bottom: 10px;"
      >
        Any source welcome · OBS, ShadowPlay, Medal, Xbox, consoles — just drop the file
      </div>
      <h1
        style="font-family: var(--font-heading); font-weight: 700; font-size: clamp(32px, 4vw, 52px); text-transform: uppercase; margin: 0; line-height: 1; color: var(--color-text-primary);"
      >
        Upload a clip
      </h1>
    </div>

    <!-- Stepper -->
    <div
      style="background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); overflow: hidden; display: flex; margin-bottom: 32px;"
    >
      <div
        v-for="(s, i) in STEPS"
        :key="s.num"
        :style="{
          flex: 1,
          padding: '16px 20px',
          borderRight: i < STEPS.length - 1 ? '1px solid var(--color-border)' : 'none',
          background: step >= Number(s.num) ? 'var(--color-surface-overlay)' : 'transparent',
          borderBottom: step === Number(s.num) ? '2px solid var(--color-brand-light)' : '2px solid transparent',
          position: 'relative',
        }"
      >
        <div
          :style="{
            fontFamily: 'var(--font-mono)',
            fontSize: '10px',
            letterSpacing: '0.1em',
            textTransform: 'uppercase',
            marginBottom: '4px',
            color: step >= Number(s.num) ? 'var(--color-neon)' : 'var(--color-text-muted)',
          }"
        >
          Step {{ s.num }}
        </div>
        <div
          style="font-family: var(--font-heading); font-weight: 700; font-size: 16px; text-transform: uppercase; color: var(--color-text-primary);"
        >
          {{ s.label }}
        </div>
      </div>
    </div>

    <!-- Step 1: File picker -->
    <div v-if="step === 1">
      <!-- Drop zone -->
      <div
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="handleDrop"
        :style="{
          border: dragging ? '2px dashed var(--color-brand-light)' : '2px dashed var(--color-border-strong)',
          borderRadius: 'var(--radius-lg)',
          padding: '64px 24px',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          gap: '16px',
          textAlign: 'center',
          transition: 'border-color 0.2s',
          background: dragging ? 'var(--color-brand-glow)' : 'transparent',
        }"
      >
        <!-- Upload icon circle -->
        <div
          style="width: 64px; height: 64px; border-radius: 50%; background: var(--color-surface-overlay); border: 1px solid var(--color-border-strong); display: flex; align-items: center; justify-content: center;"
        >
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="var(--color-brand-light)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/>
            <polyline points="17 8 12 3 7 8"/>
            <line x1="12" y1="3" x2="12" y2="15"/>
          </svg>
        </div>

        <div>
          <div style="font-family: var(--font-heading); font-weight: 700; font-size: 22px; text-transform: uppercase; color: var(--color-text-primary); margin-bottom: 6px;">
            Drop your clip here
          </div>
          <div style="font-family: var(--font-body); font-size: 14px; color: var(--color-text-secondary);">
            MP4, MOV, WebM — up to 4 GB
          </div>
        </div>

        <!-- Choose file button -->
        <label
          style="display: inline-flex; align-items: center; gap: 8px; padding: 10px 22px; background: var(--color-brand); border-radius: var(--radius-md); font-family: var(--font-heading); font-weight: 700; font-size: 14px; text-transform: uppercase; letter-spacing: 0.05em; color: #fff; cursor: pointer; transition: background 0.15s;"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
            <polyline points="14 2 14 8 20 8"/>
          </svg>
          Choose file
          <input type="file" accept="video/*" class="sr-only" @change="handleFileSelect" />
        </label>

        <!-- Source badges -->
        <div style="display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; margin-top: 8px;">
          <span
            v-for="src in SOURCES"
            :key="src"
            style="font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; padding: 4px 10px; background: var(--color-surface-overlay); border: 1px solid var(--color-border); border-radius: var(--radius-sm); color: var(--color-text-muted);"
          >
            {{ src }}
          </span>
        </div>
      </div>

      <!-- File confirmation row -->
      <div
        v-if="file"
        style="margin-top: 20px; padding: 16px 20px; background: var(--color-neon-dim); border: 1px solid var(--color-neon); border-radius: var(--radius-md); display: flex; align-items: center; gap: 16px;"
      >
        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="var(--color-neon)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="flex-shrink: 0;">
          <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
          <polyline points="14 2 14 8 20 8"/>
          <line x1="16" y1="13" x2="8" y2="13"/>
          <line x1="16" y1="17" x2="8" y2="17"/>
        </svg>
        <div style="flex: 1; min-width: 0;">
          <div style="font-family: var(--font-body); font-size: 14px; color: var(--color-text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">
            {{ file.name }}
          </div>
          <div style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); margin-top: 2px;">
            {{ formatSize(file.size) }}
          </div>
        </div>
        <button
          @click="step = 2"
          style="display: inline-flex; align-items: center; gap: 6px; padding: 10px 20px; background: var(--color-brand-light); border-radius: var(--radius-md); font-family: var(--font-heading); font-weight: 700; font-size: 14px; text-transform: uppercase; letter-spacing: 0.05em; color: #fff; cursor: pointer; white-space: nowrap; flex-shrink: 0;"
        >
          Continue
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
            <line x1="5" y1="12" x2="19" y2="12"/>
            <polyline points="12 5 19 12 12 19"/>
          </svg>
        </button>
      </div>
    </div>

    <!-- Step 2: Metadata -->
    <div v-else-if="step === 2">
      <div
        style="display: grid; grid-template-columns: 1fr 320px; gap: 32px;"
        class="upload-meta-grid"
      >
        <!-- Left: form -->
        <div style="display: flex; flex-direction: column; gap: 24px;">

          <!-- Title -->
          <div>
            <div style="display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 6px;">
              <label :style="labelStyle">Title</label>
              <span style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted);">
                {{ title.length }}/100
              </span>
            </div>
            <input
              v-model="title"
              maxlength="100"
              placeholder="What happened in this clip?"
              :style="inputStyle"
            />
          </div>

          <!-- Game picker -->
          <div>
            <label :style="labelStyle">Game</label>
            <div style="display: flex; flex-wrap: wrap; gap: 8px;">
              <button
                v-for="key in GAME_KEYS"
                :key="key"
                @click="game = key"
                :style="{
                  padding: '8px 14px',
                  borderRadius: 'var(--radius-md)',
                  border: game === key ? '1px solid var(--color-brand-light)' : '1px solid var(--color-border)',
                  background: game === key ? 'var(--color-brand)' : 'var(--color-surface-raised)',
                  color: game === key ? '#fff' : 'var(--color-text-secondary)',
                  fontFamily: 'var(--font-heading)',
                  fontWeight: '700',
                  fontSize: '13px',
                  textTransform: 'uppercase',
                  letterSpacing: '0.04em',
                  cursor: 'pointer',
                  transition: 'all 0.15s',
                }"
              >
                {{ GAMES[key].tag }}
              </button>
            </div>
          </div>

          <!-- Description -->
          <div>
            <div style="display: flex; justify-content: space-between; align-items: baseline; margin-bottom: 6px;">
              <label :style="labelStyle">Description <span style="color: var(--color-text-muted); font-size: 9px;">(optional)</span></label>
              <span style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted);">
                {{ desc.length }}/500
              </span>
            </div>
            <textarea
              v-model="desc"
              maxlength="500"
              rows="4"
              placeholder="Add context, callouts, settings — anything worth knowing"
              :style="inputStyle + ' resize: vertical; min-height: 96px;'"
            ></textarea>
          </div>

          <!-- Visibility -->
          <div>
            <label :style="labelStyle">Visibility</label>
            <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 10px;">
              <button
                v-for="opt in ['public', 'unlisted'] as const"
                :key="opt"
                @click="visibility = opt"
                :style="{
                  padding: '14px 16px',
                  borderRadius: 'var(--radius-md)',
                  border: visibility === opt ? '1px solid var(--color-brand-light)' : '1px solid var(--color-border)',
                  background: visibility === opt ? 'var(--color-brand-glow)' : 'var(--color-surface-raised)',
                  color: visibility === opt ? 'var(--color-text-primary)' : 'var(--color-text-secondary)',
                  cursor: 'pointer',
                  textAlign: 'left',
                  transition: 'all 0.15s',
                }"
              >
                <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 4px;">
                  <svg v-if="opt === 'public'" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <circle cx="12" cy="12" r="10"/>
                    <line x1="2" y1="12" x2="22" y2="12"/>
                    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>
                  </svg>
                  <svg v-else width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>
                  </svg>
                  <span style="font-family: var(--font-heading); font-weight: 700; font-size: 14px; text-transform: uppercase;">
                    {{ opt === 'public' ? 'Public' : 'Unlisted' }}
                  </span>
                </div>
                <div style="font-family: var(--font-body); font-size: 12px; color: var(--color-text-muted);">
                  {{ opt === 'public' ? 'Visible on feed + search' : 'Only accessible via link' }}
                </div>
              </button>
            </div>
          </div>

          <!-- Action buttons -->
          <div style="display: flex; gap: 12px; padding-top: 8px;">
            <button
              @click="step = 1"
              style="display: inline-flex; align-items: center; gap: 6px; padding: 12px 20px; background: var(--color-surface-overlay); border: 1px solid var(--color-border); border-radius: var(--radius-md); font-family: var(--font-heading); font-weight: 700; font-size: 14px; text-transform: uppercase; color: var(--color-text-secondary); cursor: pointer;"
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <line x1="19" y1="12" x2="5" y2="12"/>
                <polyline points="12 19 5 12 12 5"/>
              </svg>
              Back
            </button>
            <button
              :disabled="!title.trim()"
              @click="startUpload"
              :style="{
                flex: 1,
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                gap: '8px',
                padding: '12px 20px',
                background: title.trim() ? 'var(--color-brand-light)' : 'var(--color-surface-overlay)',
                border: title.trim() ? 'none' : '1px solid var(--color-border)',
                borderRadius: 'var(--radius-md)',
                fontFamily: 'var(--font-heading)',
                fontWeight: '700',
                fontSize: '15px',
                textTransform: 'uppercase',
                letterSpacing: '0.05em',
                color: title.trim() ? '#fff' : 'var(--color-text-muted)',
                cursor: title.trim() ? 'pointer' : 'not-allowed',
                transition: 'all 0.15s',
              }"
            >
              Start upload
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                <line x1="5" y1="12" x2="19" y2="12"/>
                <polyline points="12 5 19 12 12 19"/>
              </svg>
            </button>
          </div>
        </div>

        <!-- Right: preview card -->
        <div>
          <label :style="labelStyle + ' margin-bottom: 12px;'">Preview</label>
          <div
            style="background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); overflow: hidden;"
          >
            <!-- Thumbnail -->
            <div style="aspect-ratio: 16/9; position: relative; background: var(--color-surface-sunken);">
              <img
                v-if="GAMES[game]?.art"
                :src="GAMES[game].art"
                :alt="GAMES[game].name"
                style="width: 100%; height: 100%; object-fit: cover;"
              />
              <!-- Game tag -->
              <div
                style="position: absolute; top: 8px; left: 8px; padding: 3px 8px; background: var(--color-brand); border-radius: var(--radius-sm); font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: #fff;"
              >
                {{ GAMES[game]?.tag }}
              </div>
              <!-- Visibility badge -->
              <div
                style="position: absolute; top: 8px; right: 8px; padding: 3px 8px; background: rgba(0,0,0,0.6); border-radius: var(--radius-sm); font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: var(--color-text-muted);"
              >
                {{ visibility }}
              </div>
            </div>

            <div style="padding: 14px;">
              <!-- Title -->
              <div
                :style="{
                  fontFamily: 'var(--font-heading)',
                  fontWeight: '700',
                  fontSize: '15px',
                  lineHeight: '1.3',
                  color: title.trim() ? 'var(--color-text-primary)' : 'var(--color-text-muted)',
                  marginBottom: '10px',
                  fontStyle: title.trim() ? 'normal' : 'italic',
                }"
              >
                {{ title.trim() || 'Your clip title will appear here' }}
              </div>

              <!-- User row -->
              <div style="display: flex; align-items: center; gap: 8px;">
                <div
                  style="width: 24px; height: 24px; border-radius: 50%; background: #6d28d9; display: flex; align-items: center; justify-content: center; font-family: var(--font-mono); font-size: 9px; color: #fff; flex-shrink: 0;"
                >
                  P
                </div>
                <span style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-secondary);">
                  @phantomveil
                </span>
              </div>
            </div>

            <!-- Share URL preview -->
            <div
              style="margin: 0 14px 14px; padding: 10px 12px; border: 1px dashed var(--color-border-strong); border-radius: var(--radius-sm);"
            >
              <div style="font-family: var(--font-mono); font-size: 9px; color: var(--color-text-muted); letter-spacing: 0.08em; text-transform: uppercase; margin-bottom: 4px;">
                Share URL preview
              </div>
              <div style="font-family: var(--font-mono); font-size: 11px; color: var(--color-brand-light); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;">
                ganked.tv/clip/clp_new
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Step 3: Upload progress -->
    <div v-else-if="step === 3">
      <div style="max-width: 560px; margin: 0 auto;">

        <!-- Clip summary -->
        <div
          style="background: var(--color-surface-raised); border: 1px solid var(--color-border); border-radius: var(--radius-md); overflow: hidden; margin-bottom: 32px; display: flex; gap: 0;"
        >
          <!-- Thumbnail -->
          <div style="width: 140px; flex-shrink: 0; position: relative;">
            <img
              :src="GAMES[game]?.art"
              :alt="GAMES[game]?.name"
              style="width: 100%; height: 100%; object-fit: cover; display: block;"
            />
          </div>
          <div style="padding: 16px; flex: 1; min-width: 0;">
            <div
              style="font-family: var(--font-mono); font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: var(--color-neon); margin-bottom: 6px;"
            >
              {{ GAMES[game]?.tag }}
            </div>
            <div style="font-family: var(--font-heading); font-weight: 700; font-size: 16px; color: var(--color-text-primary); line-height: 1.3; margin-bottom: 8px;">
              {{ title }}
            </div>
            <div style="font-family: var(--font-mono); font-size: 10px; color: var(--color-text-muted); text-transform: uppercase; letter-spacing: 0.08em;">
              {{ visibility }}
            </div>
          </div>
        </div>

        <!-- Progress bar -->
        <div style="margin-bottom: 8px; display: flex; justify-content: space-between; align-items: baseline;">
          <span style="font-family: var(--font-mono); font-size: 11px; color: var(--color-text-muted); letter-spacing: 0.08em; text-transform: uppercase;">
            Uploading
          </span>
          <span style="font-family: var(--font-mono); font-size: 11px; color: var(--color-neon);">
            {{ Math.round(progress) }}%
          </span>
        </div>
        <div
          style="width: 100%; height: 6px; background: var(--color-surface-overlay); border-radius: 999px; overflow: hidden; margin-bottom: 28px;"
        >
          <div
            :style="{
              height: '100%',
              width: progress + '%',
              background: 'linear-gradient(90deg, var(--color-brand), var(--color-brand-light))',
              borderRadius: '999px',
              transition: 'width 0.18s ease',
            }"
          ></div>
        </div>

        <!-- Checklist -->
        <div style="display: flex; flex-direction: column; gap: 14px; margin-bottom: 36px;">
          <div
            v-for="(item, i) in ['Create record', 'Upload video', 'Generate thumbnail']"
            :key="item"
            style="display: flex; align-items: center; gap: 12px;"
          >
            <div
              :style="{
                width: '8px',
                height: '8px',
                borderRadius: '50%',
                background: checklistDone(i) ? 'var(--color-neon)' : 'var(--color-border-strong)',
                boxShadow: checklistDone(i) ? '0 0 8px var(--color-neon)' : 'none',
                flexShrink: 0,
                transition: 'background 0.3s, box-shadow 0.3s',
              }"
            ></div>
            <span
              :style="{
                fontFamily: 'var(--font-mono)',
                fontSize: '12px',
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
                color: checklistDone(i) ? 'var(--color-text-primary)' : 'var(--color-text-muted)',
                transition: 'color 0.3s',
              }"
            >
              {{ i + 1 }}. {{ item }}
            </span>
          </div>
        </div>

        <!-- Done actions -->
        <div v-if="progress >= 100" style="display: flex; flex-direction: column; gap: 10px;">
          <button
            @click="router.push('/clip/clp_04')"
            style="display: flex; align-items: center; justify-content: center; gap: 8px; padding: 14px 24px; background: var(--color-brand-light); border-radius: var(--radius-md); font-family: var(--font-heading); font-weight: 700; font-size: 16px; text-transform: uppercase; letter-spacing: 0.05em; color: #fff; cursor: pointer; width: 100%;"
          >
            View your clip
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
              <line x1="5" y1="12" x2="19" y2="12"/>
              <polyline points="12 5 19 12 12 19"/>
            </svg>
          </button>
          <button
            @click="router.push('/')"
            style="display: flex; align-items: center; justify-content: center; padding: 12px 24px; background: transparent; border: 1px solid var(--color-border); border-radius: var(--radius-md); font-family: var(--font-heading); font-weight: 700; font-size: 14px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--color-text-secondary); cursor: pointer; width: 100%;"
          >
            Back to feed
          </button>
        </div>
      </div>
    </div>

  </main>
</template>

<style scoped>
@media (max-width: 760px) {
  .upload-meta-grid {
    grid-template-columns: 1fr !important;
  }
}
</style>
