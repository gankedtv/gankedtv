<script setup lang="ts">
import { ref, computed, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { clips } from '@/api/clips'
import type { GameSummary } from '@/api/clips'
import GameSelector from '@/components/GameSelector.vue'
import TagInput from '@/components/TagInput.vue'
import PageHeader from '@/components/PageHeader.vue'
import IconUploadCloud from '@/components/icons/IconUploadCloud.vue'
import IconFile from '@/components/icons/IconFile.vue'
import IconFileText from '@/components/icons/IconFileText.vue'
import IconArrowRight from '@/components/icons/IconArrowRight.vue'
import IconArrowLeft from '@/components/icons/IconArrowLeft.vue'
import IconGlobe from '@/components/icons/IconGlobe.vue'
import IconLink from '@/components/icons/IconLink.vue'

const router = useRouter()

// Guard against a missing/garbage env value: Number('foo') is NaN, and `f.size > NaN`
// is always false — silently disabling the size cap. Fall back to a safe default instead.
const MAX_UPLOAD_MB = (() => {
  const parsed = Number(String(import.meta.env.VITE_MAX_UPLOAD_SIZE_MB ?? '').trim())
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 500
})()
const MAX_UPLOAD_BYTES = MAX_UPLOAD_MB * 1024 * 1024

type Step = 1 | 2 | 3
const step = ref<Step>(1)
const file = ref<File | null>(null)
const title = ref('')
const desc = ref('')
const visibility = ref<'public' | 'unlisted'>('public')
const dragging = ref(false)

const selectedGame = ref<GameSummary | null>(null)
const selectedTags = ref<string[]>([])

// Upload state — granular so the checklist can light up step-by-step.
type UploadStage = 'idle' | 'creating' | 'uploading' | 'completing' | 'done' | 'error'
const stage = ref<UploadStage>('idle')
const uploadPct = ref(0)
const errorMsg = ref<string | null>(null)
const createdClipId = ref<string | null>(null)
let activeXhr: XMLHttpRequest | null = null

onUnmounted(() => {
  if (activeXhr) activeXhr.abort()
})

function pickFile(f: File | null) {
  if (!f) return
  if (!f.type.startsWith('video/')) {
    // Clear any prior valid selection — leaving the old file as the "current"
    // pick alongside an error about a different file is confusing.
    file.value = null
    errorMsg.value = `Unsupported file type "${f.type || 'unknown'}" — pick a video.`
    return
  }
  if (f.size > MAX_UPLOAD_BYTES) {
    file.value = null
    errorMsg.value = `File is ${formatSize(f.size)} — limit is ${MAX_UPLOAD_MB} MB.`
    return
  }
  errorMsg.value = null
  file.value = f
}

function handleFileSelect(e: Event) {
  const input = e.target as HTMLInputElement
  pickFile(input.files?.[0] ?? null)
  if (e.target instanceof HTMLInputElement) e.target.value = ''
}

function handleDrop(e: DragEvent) {
  dragging.value = false
  pickFile(e.dataTransfer?.files?.[0] ?? null)
}

function formatSize(bytes: number): string {
  if (bytes >= 1_073_741_824) return (bytes / 1_073_741_824).toFixed(1) + ' GB'
  if (bytes >= 1_048_576) return (bytes / 1_048_576).toFixed(1) + ' MB'
  return (bytes / 1024).toFixed(1) + ' KB'
}

// Generous ceiling so a 500 MB upload over a slow connection still finishes,
// but bounded so a fully-stalled connection doesn't spin forever.
const UPLOAD_TIMEOUT_MS = 10 * 60 * 1000

function putWithProgress(url: string, body: File, contentType: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    activeXhr = xhr
    xhr.open('PUT', url)
    xhr.timeout = UPLOAD_TIMEOUT_MS
    // Must match the Content-Type the server signed for, NOT the browser-detected
    // file MIME — S3 includes it in the signature and 403s on mismatch.
    xhr.setRequestHeader('Content-Type', contentType)
    xhr.upload.onprogress = (ev) => {
      if (ev.lengthComputable) uploadPct.value = (ev.loaded / ev.total) * 100
    }
    xhr.onload = () => {
      activeXhr = null
      if (xhr.status >= 200 && xhr.status < 300) resolve()
      else reject(new Error(`PUT failed: ${xhr.status}`))
    }
    xhr.onerror = () => {
      activeXhr = null
      reject(new Error('PUT network error'))
    }
    xhr.onabort = () => {
      activeXhr = null
      reject(new Error('PUT aborted'))
    }
    xhr.ontimeout = () => {
      activeXhr = null
      reject(new Error('Upload timed out — check your connection and try again'))
    }
    xhr.send(body)
  })
}

async function startUpload() {
  if (!file.value || !title.value.trim()) return
  step.value = 3
  stage.value = 'creating'
  uploadPct.value = 0
  errorMsg.value = null
  try {
    const created = await clips.create({
      title: title.value.trim(),
      description: desc.value.trim() || null,
      gameId: selectedGame.value?.id ?? null,
      visibility: visibility.value,
      // Omit the field entirely when empty so the server treats it as "no tags"
      // instead of an empty array. Same wire shape as the old POST.
      ...(selectedTags.value.length ? { tags: selectedTags.value } : {}),
    })
    createdClipId.value = created.id

    stage.value = 'uploading'
    const presigned = await clips.getUploadUrl(created.id)
    await putWithProgress(presigned.url, file.value, presigned.contentType)

    stage.value = 'completing'
    await clips.complete(created.id)

    stage.value = 'done'
    uploadPct.value = 100
  } catch (err) {
    stage.value = 'error'
    errorMsg.value = friendlyUploadError(err)
  }
}

function friendlyUploadError(err: unknown): string {
  if (err instanceof ApiError) {
    return `Server error (${err.status}). Please try again.`
  }
  if (err instanceof Error) {
    // Normalize the raw XHR errors from putWithProgress so users see actionable copy
    // instead of `PUT failed: 403`. Pre-formatted messages (e.g. the timeout) are
    // already user-friendly and pass through unchanged.
    const m = err.message
    if (m.startsWith('PUT failed') || m.startsWith('PUT network error')) {
      return 'Upload was interrupted. Please try again.'
    }
    if (m.startsWith('PUT aborted')) {
      return 'Upload cancelled.'
    }
    return m
  }
  return 'Upload failed.'
}

const checklistDone = computed(() => ({
  create: stage.value !== 'idle' && stage.value !== 'creating',
  upload: stage.value === 'completing' || stage.value === 'done',
  complete: stage.value === 'done',
}))

function goBackToDetails() {
  // Drop the half-created clip id so the next attempt creates a fresh draft instead
  // of re-using the failed one. The orphaned draft row is cleaned up server-side
  // by the future scheduled-sweep job (Phase 2 maintenance).
  stage.value = 'idle'
  step.value = 2
  createdClipId.value = null
  uploadPct.value = 0
  errorMsg.value = null
}

const STEPS = [
  { num: '1', label: 'Select file' },
  { num: '2', label: 'Describe' },
  { num: '3', label: 'Upload' },
]
const SOURCES = ['OBS', 'ShadowPlay', 'Medal', 'Xbox', 'PS5', 'Switch']

const inputClass =
  'w-full rounded-md border border-border bg-surface-raised px-3.5 py-3 font-body text-sm text-text-primary outline-none'
const labelClass = 'mb-1.5 block font-mono text-[10px] uppercase tracking-widest text-text-muted'
</script>

<template>
  <main class="mx-auto max-w-225 px-6 pt-8 pb-30">
    <PageHeader title="Upload a clip" class="mb-7">
      <template #caption>
        Any source welcome · OBS, ShadowPlay, Medal, Xbox, consoles — just drop the file
      </template>
    </PageHeader>

    <!-- Stepper -->
    <div class="mb-8 flex overflow-hidden rounded-md border border-border bg-surface-raised">
      <div
        v-for="(s, i) in STEPS"
        :key="s.num"
        :class="[
          'relative flex-1 px-5 py-4 border-b-2',
          i < STEPS.length - 1 ? 'border-r border-r-border' : '',
          step >= Number(s.num) ? 'bg-surface-overlay' : 'bg-transparent',
          step === Number(s.num) ? 'border-b-brand-light' : 'border-b-transparent',
        ]"
      >
        <div
          :class="[
            'mb-1 font-mono text-[10px] uppercase tracking-widest',
            step >= Number(s.num) ? 'text-neon' : 'text-text-muted',
          ]"
        >
          Step {{ s.num }}
        </div>
        <div class="font-heading text-base font-bold uppercase text-text-primary">
          {{ s.label }}
        </div>
      </div>
    </div>

    <!-- Step 1: File picker -->
    <div v-if="step === 1">
      <div
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="handleDrop"
        :class="[
          'flex flex-col items-center gap-4 rounded-lg border-2 border-dashed px-6 py-16 text-center transition-[border-color] duration-200',
          dragging ? 'border-brand-light bg-brand-glow' : 'border-border-strong bg-transparent',
        ]"
      >
        <div
          class="flex h-16 w-16 items-center justify-center rounded-full border border-border-strong bg-surface-overlay text-brand-light"
        >
          <IconUploadCloud :size="28" />
        </div>

        <div>
          <div class="mb-1.5 font-heading text-[22px] font-bold uppercase text-text-primary">
            Drop your clip here
          </div>
          <div class="font-body text-sm text-text-secondary">
            MP4 or video — up to {{ MAX_UPLOAD_MB }} MB
          </div>
        </div>

        <label
          class="inline-flex cursor-pointer items-center gap-2 rounded-md bg-brand px-5.5 py-2.5 font-heading text-sm font-bold uppercase tracking-wider text-white"
        >
          <IconFile :size="16" />
          Choose file
          <input type="file" accept="video/*" class="sr-only" @change="handleFileSelect" />
        </label>

        <div class="mt-2 flex flex-wrap justify-center gap-2">
          <span
            v-for="src in SOURCES"
            :key="src"
            class="rounded-sm border border-border bg-surface-overlay px-2.5 py-1 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
          >
            {{ src }}
          </span>
        </div>
      </div>

      <p
        v-if="errorMsg"
        class="mt-4 rounded-md border border-brand bg-surface-overlay px-4 py-2 font-mono text-[12px] text-brand-light"
      >
        {{ errorMsg }}
      </p>

      <div
        v-if="file"
        class="mt-5 flex items-center gap-4 rounded-md border border-neon bg-neon-dim px-5 py-4 text-neon"
      >
        <IconFileText :size="20" class="shrink-0" />
        <div class="min-w-0 flex-1">
          <div
            class="overflow-hidden font-body text-sm whitespace-nowrap text-ellipsis text-text-primary"
          >
            {{ file.name }}
          </div>
          <div class="mt-0.5 font-mono text-[11px] text-text-muted">
            {{ formatSize(file.size) }}
          </div>
        </div>
        <button
          @click="step = 2"
          class="inline-flex shrink-0 cursor-pointer items-center gap-1.5 rounded-md bg-brand-light px-5 py-2.5 font-heading text-sm font-bold whitespace-nowrap uppercase tracking-wider text-white"
        >
          Continue
          <IconArrowRight :size="14" :stroke-width="2.5" />
        </button>
      </div>
    </div>

    <!-- Step 2: Metadata -->
    <div v-else-if="step === 2">
      <div class="grid gap-8 grid-cols-1 min-[761px]:grid-cols-[1fr_320px]">
        <div class="flex flex-col gap-6">
          <!-- Game picker -->
          <div>
            <label :class="labelClass"
              >Game <span class="text-[9px] text-text-muted">(optional)</span></label
            >
            <GameSelector v-model="selectedGame" />
          </div>

          <!-- Tags -->
          <div>
            <label :class="labelClass"
              >Tags <span class="text-[9px] text-text-muted">(optional, max 5)</span></label
            >
            <TagInput v-model="selectedTags" :input-class="inputClass" />
          </div>

          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'">Title</label>
              <span class="font-mono text-[10px] text-text-muted"> {{ title.length }}/100 </span>
            </div>
            <input
              v-model="title"
              maxlength="100"
              placeholder="What happened in this clip?"
              :class="inputClass"
            />
          </div>

          <div>
            <div class="mb-1.5 flex items-baseline justify-between">
              <label :class="labelClass + ' mb-0'"
                >Description <span class="text-[9px] text-text-muted">(optional)</span></label
              >
              <span class="font-mono text-[10px] text-text-muted"> {{ desc.length }}/500 </span>
            </div>
            <textarea
              v-model="desc"
              maxlength="500"
              rows="4"
              placeholder="Add context, callouts, settings — anything worth knowing"
              :class="inputClass + ' resize-y min-h-24'"
            ></textarea>
          </div>

          <div>
            <label :class="labelClass">Visibility</label>
            <div class="grid grid-cols-2 gap-2.5">
              <button
                v-for="opt in ['public', 'unlisted'] as const"
                :key="opt"
                @click="visibility = opt"
                :class="[
                  'cursor-pointer rounded-md border px-4 py-3.5 text-left transition-all duration-150',
                  visibility === opt
                    ? 'border-brand-light bg-brand-glow text-text-primary'
                    : 'border-border bg-surface-raised text-text-secondary',
                ]"
              >
                <div class="mb-1 flex items-center gap-2">
                  <IconGlobe v-if="opt === 'public'" :size="16" />
                  <IconLink v-else :size="16" />
                  <span class="font-heading text-sm font-bold uppercase">
                    {{ opt === 'public' ? 'Public' : 'Unlisted' }}
                  </span>
                </div>
                <div class="font-body text-xs text-text-muted">
                  {{ opt === 'public' ? 'Visible on feed + search' : 'Only accessible via link' }}
                </div>
              </button>
            </div>
          </div>

          <div class="flex gap-3 pt-2">
            <button
              @click="step = 1"
              class="inline-flex cursor-pointer items-center gap-1.5 rounded-md border border-border bg-surface-overlay px-5 py-3 font-heading text-sm font-bold uppercase text-text-secondary"
            >
              <IconArrowLeft :size="14" :stroke-width="2.5" />
              Back
            </button>
            <button
              :disabled="!title.trim()"
              @click="startUpload"
              :class="[
                'inline-flex flex-1 items-center justify-center gap-2 rounded-md px-5 py-3 font-heading text-[15px] font-bold uppercase tracking-wider transition-all duration-150',
                title.trim()
                  ? 'cursor-pointer border-0 bg-brand-light text-white'
                  : 'cursor-not-allowed border border-border bg-surface-overlay text-text-muted',
              ]"
            >
              Start upload
              <IconArrowRight :size="14" :stroke-width="2.5" />
            </button>
          </div>
        </div>

        <div>
          <label :class="labelClass + ' mb-3'">Preview</label>
          <div class="overflow-hidden rounded-md border border-border bg-surface-raised">
            <div class="relative aspect-video bg-surface-sunken">
              <div
                class="absolute inset-0 flex items-center justify-center font-mono text-[10px] uppercase tracking-widest text-text-muted"
              >
                {{ file?.name ?? 'No file' }}
              </div>
              <div
                class="absolute top-2 right-2 rounded-sm bg-black/60 px-2 py-0.75 font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted"
              >
                {{ visibility }}
              </div>
            </div>

            <div class="p-3.5">
              <div
                :class="[
                  'mb-2.5 font-heading text-[15px] font-bold leading-[1.3]',
                  title.trim() ? 'not-italic text-text-primary' : 'italic text-text-muted',
                ]"
              >
                {{ title.trim() || 'Your clip title will appear here' }}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Step 3: Upload progress -->
    <div v-else-if="step === 3">
      <div class="mx-auto max-w-140">
        <div
          class="mb-8 flex gap-0 overflow-hidden rounded-md border border-border bg-surface-raised"
        >
          <div class="min-w-0 flex-1 p-4">
            <div class="mb-2 font-heading text-base font-bold leading-[1.3] text-text-primary">
              {{ title }}
            </div>
            <div class="font-mono text-[10px] uppercase tracking-[0.08em] text-text-muted">
              {{ visibility }} · {{ file ? formatSize(file.size) : '' }}
            </div>
          </div>
        </div>

        <div class="mb-2 flex items-baseline justify-between">
          <span class="font-mono text-[11px] uppercase tracking-[0.08em] text-text-muted">
            {{ stage === 'uploading' ? 'Uploading' : stage === 'done' ? 'Done' : 'Preparing' }}
          </span>
          <span class="font-mono text-[11px] text-neon"> {{ Math.round(uploadPct) }}% </span>
        </div>
        <div class="mb-7 h-1.5 w-full overflow-hidden rounded-full bg-surface-overlay">
          <div
            class="h-full rounded-full bg-[linear-gradient(90deg,var(--color-brand),var(--color-brand-light))] transition-[width] duration-180 ease"
            :style="{ width: uploadPct + '%' }"
          ></div>
        </div>

        <div class="mb-9 flex flex-col gap-3.5">
          <div
            v-for="item in [
              { label: '1. Create record', done: checklistDone.create },
              { label: '2. Upload video', done: checklistDone.upload },
              { label: '3. Finalize', done: checklistDone.complete },
            ]"
            :key="item.label"
            class="flex items-center gap-3"
          >
            <div
              :class="[
                'h-2 w-2 shrink-0 rounded-full transition-[background,box-shadow] duration-300',
                item.done ? 'bg-neon shadow-[0_0_8px_var(--color-neon)]' : 'bg-border-strong',
              ]"
            ></div>
            <span
              :class="[
                'font-mono text-xs uppercase tracking-[0.08em] transition-colors duration-300',
                item.done ? 'text-text-primary' : 'text-text-muted',
              ]"
            >
              {{ item.label }}
            </span>
          </div>
        </div>

        <div
          v-if="stage === 'error'"
          class="mb-6 rounded-md border border-brand bg-surface-overlay px-4 py-3 font-mono text-[12px] text-brand-light"
        >
          {{ errorMsg }}
          <button
            class="mt-2 block cursor-pointer rounded-sm border border-border bg-surface-raised px-3 py-1.5 text-text-primary"
            @click="goBackToDetails"
          >
            Back to details
          </button>
        </div>

        <div v-if="stage === 'done'" class="flex flex-col gap-2.5">
          <button
            :disabled="!createdClipId"
            @click="createdClipId && router.push(`/clip/${createdClipId}`)"
            class="flex w-full cursor-pointer items-center justify-center gap-2 rounded-md bg-brand-light px-6 py-3.5 font-heading text-base font-bold uppercase tracking-wider text-white disabled:cursor-not-allowed disabled:opacity-50"
          >
            View your clip
            <IconArrowRight :size="16" :stroke-width="2.5" />
          </button>
          <button
            @click="router.push('/')"
            class="flex w-full cursor-pointer items-center justify-center rounded-md border border-border bg-transparent px-6 py-3 font-heading text-sm font-bold uppercase tracking-wider text-text-secondary"
          >
            Back to feed
          </button>
        </div>
      </div>
    </div>
  </main>
</template>
