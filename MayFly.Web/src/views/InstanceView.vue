<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useInstance, destroyInstance } from '../api/instances'
import { formatBytes } from '../lib/format'
import ConnectionSnippets from '../components/ConnectionSnippets.vue'

const props = defineProps<{ token: string }>()
const router = useRouter()
const { data: inst } = useInstance(props.token)

// Live clock for countdown
const now = ref(Date.now())
let ticker: ReturnType<typeof setInterval>
onMounted(() => { ticker = setInterval(() => { now.value = Date.now() }, 1000) })
onUnmounted(() => clearInterval(ticker))

// State badge
const badgeTone = computed(() => {
  const s = (inst.value?.state ?? '').toLowerCase()
  if (s === 'running') return 'accent'
  if (s === 'expired' || s === 'destroyed') return 'danger'
  return ''
})
const badgePulse = computed(() => (inst.value?.state ?? '').toLowerCase() === 'running')

// Countdown
const msLeft = computed(() => {
  if (!inst.value) return 0
  return Math.max(0, new Date(inst.value.expiresAt).getTime() - now.value)
})

function fmtCountdown(ms: number): string {
  if (ms <= 0) return '00:00:00'
  const h = Math.floor(ms / 3_600_000)
  const m = Math.floor((ms % 3_600_000) / 60_000)
  const s = Math.floor((ms % 60_000) / 1_000)
  return [h, m, s].map(n => String(n).padStart(2, '0')).join(':')
}

const lifetimePct = computed(() => {
  if (!inst.value) return 0
  const total = new Date(inst.value.expiresAt).getTime() - new Date(inst.value.createdAt).getTime()
  if (total <= 0) return 0
  return Math.max(0, Math.min(100, (msLeft.value / total) * 100))
})

const progressClass = computed(() => {
  const p = lifetimePct.value
  if (p <= 25) return 'progress danger'
  if (p <= 50) return 'progress warn'
  return 'progress'
})

const autoDestroysAt = computed(() => {
  if (!inst.value) return ''
  return new Date(inst.value.expiresAt).toLocaleString()
})

// Engine glyph
const engineGlyph = computed(() => {
  const e = (inst.value?.engine ?? '').toLowerCase()
  if (e.includes('postgres')) return 'Pg'
  if (e.includes('mysql')) return 'My'
  return 'DB'
})

// Connection string parts
const connParts = computed(() => {
  const cs = inst.value?.connectionString ?? ''
  const m = cs.match(/^([a-z+]+):\/\/([^:]+):([^@]+)@([^:/]+):(\d+)\/(.+)$/i)
  if (!m) return { proto: '', user: '', pass: '', host: '', port: '', db: cs }
  const [, proto, user, pass, host, port, db] = m
  return { proto, user, pass, host, port, db }
})

// Storage progress
const storageQuotaBytes = computed(() => (inst.value?.storageQuotaMb ?? 0) * 1024 * 1024)
const storagePct = computed(() => {
  if (!storageQuotaBytes.value) return 0
  return Math.min(100, ((inst.value?.lastSizeBytes ?? 0) / storageQuotaBytes.value) * 100)
})
const storageProgressClass = computed(() => {
  const p = storagePct.value
  if (p > 80) return 'progress danger'
  if (p > 60) return 'progress warn'
  return 'progress'
})

// Short token
const shortToken = computed(() => props.token.slice(0, 8))

// Copy connection string
const copiedCs = ref(false)
async function copyCs() {
  if (!inst.value) return
  await navigator.clipboard.writeText(inst.value.connectionString)
  copiedCs.value = true
  setTimeout(() => { copiedCs.value = false }, 1500)
}

// Copy password
const copiedPw = ref(false)
async function copyPw() {
  if (!connParts.value.pass) return
  await navigator.clipboard.writeText(connParts.value.pass)
  copiedPw.value = true
  setTimeout(() => { copiedPw.value = false }, 1500)
}

// Destroy
const destroying = ref(false)
async function destroy() {
  destroying.value = true
  try {
    await destroyInstance(props.token)
    router.push('/')
  } finally {
    destroying.value = false
  }
}
</script>

<template>
  <div class="page">
    <div v-if="inst" class="container wide" style="padding-top: 18px;">

      <!-- HERO row -->
      <div class="row between g-4" style="margin-bottom: 18px; flex-wrap: wrap; gap: 12px;">
        <div class="row g-3">
          <div class="engine-logo" style="width: 48px; height: 48px; font-size: 19px; color: var(--info);">
            {{ engineGlyph }}
          </div>
          <div class="col g-1">
            <div class="row g-3" style="flex-wrap: wrap;">
              <span style="font-size: 20px; font-weight: 600;">{{ inst.dbName }}</span>
              <span class="dimmer" style="font-size: 12px; font-variant-numeric: tabular-nums;">{{ shortToken }}</span>
              <span :class="['badge', badgeTone]">
                <span :class="['dot', badgePulse ? 'pulse' : '']"></span>
                {{ inst.state }}
              </span>
            </div>
            <div class="dim" style="font-size: 12px;">
              PostgreSQL · storage {{ inst.storageQuotaMb }} MB · seed {{ inst.initialData }}
            </div>
          </div>
        </div>
        <div class="row g-2">
          <RouterLink :to="'/console/' + token" class="btn primary">▸ sql console</RouterLink>
          <button class="btn danger" :disabled="destroying" @click="destroy">destroy now</button>
        </div>
      </div>

      <!-- COUNTDOWN frame -->
      <div class="frame" style="margin-bottom: 18px;">
        <div class="crn-bl"></div>
        <div class="crn-br"></div>
        <div class="row between g-4" style="flex-wrap: wrap; align-items: flex-start;">
          <div class="col g-2">
            <div class="dimmer" style="font-size: 11px; text-transform: uppercase; letter-spacing: .08em;">
              self-destruct in
            </div>
            <div
              class="big-num num"
              :class="lifetimePct <= 25 ? 'danger' : lifetimePct <= 50 ? 'warn' : 'accent'"
            >
              {{ fmtCountdown(msLeft) }}
            </div>
            <div class="dim" style="font-size: 11px;">auto-destroys at {{ autoDestroysAt }}</div>
          </div>
          <div class="col g-2" style="min-width: 220px;">
            <div class="dim" style="font-size: 11px; text-align: right;">
              {{ Math.round(lifetimePct) }}% lifetime remaining
            </div>
            <div :class="progressClass">
              <i :style="{ width: lifetimePct + '%' }"></i>
            </div>
            <div class="row between dimmer" style="font-size: 10px;">
              <span>0h</span>
              <span>{{ inst.ttlHours }}h</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Two-column grid -->
      <div class="grid-2" style="align-items: start;">

        <!-- LEFT column -->
        <div class="col g-3">

          <!-- Card: connection string -->
          <div class="card">
            <div class="hd">
              <span class="ttl">connection string</span>
              <span class="dimmer" style="font-size: 10px;">private to you</span>
            </div>
            <div class="bd col g-3">
              <!-- ConnString block -->
              <div class="connstr">
                <span class="url">
                  <span style="color: var(--text-3);">{{ connParts.proto }}://</span>
                  <span style="color: var(--info);">{{ connParts.user }}</span>
                  <span style="color: var(--text-3);">:</span>
                  <span style="color: var(--warn);">••••••••</span>
                  <span style="color: var(--text-3);">@</span>
                  <span class="host">{{ connParts.host }}:{{ connParts.port }}/{{ connParts.db }}</span>
                </span>
                <button class="btn sm" @click="copyCs">{{ copiedCs ? '✓ copied' : 'copy' }}</button>
              </div>

              <!-- KV rows -->
              <div class="kv-grid">
                <div class="kv-row">
                  <span class="upper dimmer kv-key">host</span>
                  <span class="kv-val">{{ connParts.host }}</span>
                </div>
                <div class="kv-row">
                  <span class="upper dimmer kv-key">port</span>
                  <span class="kv-val">{{ inst.publicPort }}</span>
                </div>
                <div class="kv-row">
                  <span class="upper dimmer kv-key">database</span>
                  <span class="kv-val">{{ inst.dbName }}</span>
                </div>
                <div class="kv-row">
                  <span class="upper dimmer kv-key">user</span>
                  <span class="kv-val">{{ inst.dbUser }}</span>
                </div>
                <div class="kv-row">
                  <span class="upper dimmer kv-key">password</span>
                  <span class="kv-val row g-2">
                    <span style="letter-spacing: 3px; color: var(--text-3);">••••••</span>
                    <button class="btn sm" @click="copyPw">{{ copiedPw ? '✓' : 'copy' }}</button>
                  </span>
                </div>
                <div class="kv-row" style="border-bottom: none;">
                  <span class="upper dimmer kv-key">ssl</span>
                  <span class="kv-val accent">required</span>
                </div>
              </div>
            </div>
          </div>

          <!-- Card: connect from your code -->
          <div class="card">
            <div class="hd">
              <span class="ttl">connect from your code</span>
            </div>
            <ConnectionSnippets :instance="inst" />
          </div>

        </div>

        <!-- RIGHT column -->
        <div class="col g-3">

          <!-- Storage stat tile -->
          <div class="stat-tile">
            <div class="label">storage</div>
            <div class="value num">{{ formatBytes(inst.lastSizeBytes) }}</div>
            <div class="row between" style="align-items: flex-end;">
              <div class="sub">of {{ inst.storageQuotaMb }} MB quota</div>
            </div>
            <div :class="storageProgressClass" style="margin-top: 4px;">
              <i :style="{ width: storagePct + '%' }"></i>
            </div>
          </div>

          <!-- Deferred metrics note -->
          <div class="dimmer" style="font-size: 11px; text-align: center; padding: 16px;">
            more metrics soon
          </div>

        </div>
      </div>

    </div>
    <div v-else class="container wide">
      <p class="dim" style="padding-top: 40px;">Loading…</p>
    </div>
  </div>
</template>

<style scoped>
.kv-grid {
  display: flex;
  flex-direction: column;
}
.kv-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 0;
  border-bottom: 1px dashed var(--border);
  font-size: 12px;
  gap: 8px;
}
.kv-key {
  font-size: 10.5px;
  min-width: 72px;
}
.kv-val {
  text-align: right;
}
</style>
