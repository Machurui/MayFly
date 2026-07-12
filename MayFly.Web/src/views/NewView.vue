<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { createInstance } from '../api/instances'
import { engineLabel } from '../lib/engineLabels'
import EnginePicker from '../components/EnginePicker.vue'
import TtlPicker from '../components/TtlPicker.vue'
import StoragePicker from '../components/StoragePicker.vue'
import InitialDataPicker from '../components/InitialDataPicker.vue'

const router = useRouter()

const engine      = ref('postgres')
const ttlHours    = ref(3)
const storageMb   = ref(256)
const initialData = ref('blank')
const name        = ref('')
const error       = ref('')
const busy        = ref(false)

const engineMeta: Record<string, { driver: string; version: string }> = {
  postgres: { driver: 'postgresql', version: '17.5' },
  mysql:    { driver: 'mysql',      version: '8.4'  },
  mariadb:  { driver: 'mariadb',    version: '11.4' },
  mongo:    { driver: 'mongodb',    version: '7.0'  },
  mssql:    { driver: 'mssql',      version: '22'   },
}

const storageLabelMap: Record<number, string> = { 256: '256 MB', 512: '512 MB', 1024: '1 GB', 2048: '2 GB' }

const summary = computed(() => {
  const em = engineMeta[engine.value] ?? { driver: engine.value, version: '?' }
  const stLbl = storageLabelMap[storageMb.value] ?? `${storageMb.value} MB`
  const nm = name.value.trim() || 'swift-otter'
  return `> ${engineLabel(engine.value)} ${em.version}  storage ${stLbl}
> ttl    ${ttlHours.value}h         — destroyed in ${ttlHours.value}h
> seed   ${initialData.value}
> name   ${nm}`
})

async function create() {
  busy.value = true
  error.value = ''
  try {
    const inst = await createInstance({
      engine: engine.value,
      ttlHours: ttlHours.value,
      storageMb: storageMb.value,
      initialData: initialData.value,
    })
    router.push('/instance/' + inst.token)
  } catch (e: unknown) {
    const err = e as { status?: number; message?: string }
    error.value = err?.status === 429
      ? 'You reached the quota of 3 active databases for your IP.'
      : (err?.message ?? 'Creation failed.')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="page">
    <div class="container narrow">

      <!-- Hero -->
      <div class="row between" style="margin-bottom: 28px;">
        <div>
          <div class="upper dim" style="font-size: 11px; margin-bottom: 6px;">$ mayfly init</div>
          <h1 style="margin: 0; font-size: 28px; font-weight: 500; letter-spacing: -.02em;">
            Spin up a database<span class="accent">.</span>
          </h1>
          <div class="dim" style="margin-top: 8px; font-size: 13px;">
            Real engines. Real connection string. Self-destructs after the timer hits zero.<br />
            No account. No card. Just a URL you can ship into your code right now.
          </div>
        </div>
        <div class="col g-2" style="align-items: flex-end;">
          <span class="badge accent">
            <span class="dot pulse"></span>
            region fra-1 — online
          </span>
          <div class="dimmer" style="font-size: 11px;">~3 s to provision</div>
        </div>
      </div>

      <!-- Privacy callout frame -->
      <div class="frame" style="margin-bottom: 28px; padding: 14px 16px; border-color: var(--accent-line); background: var(--accent-dim);">
        <span class="crn-bl"></span><span class="crn-br"></span>
        <div class="dim" style="font-size: 11.5px; line-height: 1.8;">
          ◊ Your data is yours
          &nbsp;·&nbsp; ✓ not used for training/ads
          &nbsp;·&nbsp; ✓ not shared
          &nbsp;·&nbsp; ✓ wiped from disk on expiry
          &nbsp;·&nbsp; ✓ snapshots deleted after 7 days
        </div>
      </div>

      <!-- Section 01 — Engine -->
      <section style="margin-bottom: 24px;">
        <div class="row between" style="margin-bottom: 10px;">
          <div class="row g-3">
            <span class="dimmer upper" style="font-size: 11px; letter-spacing: .1em;">01</span>
            <span style="font-weight: 500;">Engine</span>
          </div>
          <div class="hr flex-1" style="margin-left: 12px;"></div>
        </div>
        <EnginePicker v-model="engine" />
      </section>

      <!-- Section 02 — Time-to-live -->
      <section style="margin-bottom: 24px;">
        <div class="row between" style="margin-bottom: 10px;">
          <div class="row g-3">
            <span class="dimmer upper" style="font-size: 11px; letter-spacing: .1em;">02</span>
            <span style="font-weight: 500;">Time-to-live</span>
          </div>
          <div class="hr flex-1" style="margin-left: 12px;"></div>
        </div>
        <TtlPicker v-model="ttlHours" />
        <div class="dim" style="font-size: 11px; margin-top: 8px;">
          ↪ Database will be destroyed at expiry. You can extend once, up to 24h total.
        </div>
      </section>

      <!-- Section 03 — Storage quota -->
      <section style="margin-bottom: 24px;">
        <div class="row between" style="margin-bottom: 10px;">
          <div class="row g-3">
            <span class="dimmer upper" style="font-size: 11px; letter-spacing: .1em;">03</span>
            <span style="font-weight: 500;">Storage quota</span>
          </div>
          <div class="hr flex-1" style="margin-left: 12px;"></div>
        </div>
        <StoragePicker v-model="storageMb" />
        <div class="dim" style="font-size: 11px; margin-top: 8px;">
          ↪ Hard cap. Writes return ENOSPC past the limit. Compute shared/elastic.
        </div>
      </section>

      <!-- Section 04 — Initial data -->
      <section style="margin-bottom: 24px;">
        <div class="row between" style="margin-bottom: 10px;">
          <div class="row g-3">
            <span class="dimmer upper" style="font-size: 11px; letter-spacing: .1em;">04</span>
            <span style="font-weight: 500;">Initial data</span>
            <span class="dimmer" style="font-size: 11px;">(optional)</span>
          </div>
          <div class="hr flex-1" style="margin-left: 12px;"></div>
        </div>
        <InitialDataPicker v-model="initialData" :engine="engine" />
      </section>

      <!-- Section 05 — Name -->
      <section style="margin-bottom: 24px;">
        <div class="row between" style="margin-bottom: 10px;">
          <div class="row g-3">
            <span class="dimmer upper" style="font-size: 11px; letter-spacing: .1em;">05</span>
            <span style="font-weight: 500;">Name</span>
            <span class="dimmer" style="font-size: 11px;">(optional)</span>
          </div>
          <div class="hr flex-1" style="margin-left: 12px;"></div>
        </div>
        <input class="input" v-model="name" placeholder="swift-otter" style="max-width: 320px;" />
        <div class="dim" style="font-size: 11px; margin-top: 6px;">Cosmetic label — not sent to the backend in slice-1.</div>
      </section>

      <!-- Error message -->
      <p v-if="error" class="danger" style="margin-bottom: 12px; font-size: 12.5px;">{{ error }}</p>

      <!-- Summary frame -->
      <div class="frame" style="margin-top: 8px; margin-bottom: 60px;">
        <span class="crn-bl"></span><span class="crn-br"></span>
        <div class="row between" style="align-items: flex-start; gap: 24px;">
          <pre class="code" style="background: transparent; border: none; padding: 0; flex: 1; margin: 0;">{{ summary }}</pre>
          <div class="col g-2" style="align-items: flex-end;">
            <button
              class="btn primary lg"
              data-test="create"
              :disabled="busy"
              @click="create"
            >
              {{ busy ? '▸ provisioning…' : '▸ provision now' }}
              <span class="dimmer" style="font-size: 11px;">⏎</span>
            </button>
            <div class="dimmer" style="font-size: 11px;">or press <kbd>⌘⏎</kbd></div>
          </div>
        </div>
      </div>

      <!-- Footer -->
      <div class="dimmer" style="font-size: 11px; margin-bottom: 32px; border-top: 1px solid var(--border); padding-top: 12px;">
        fair use: 12h max · 3 active per IP · no PII stored
      </div>

    </div>
  </div>
</template>
