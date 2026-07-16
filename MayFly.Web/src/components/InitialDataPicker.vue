<script setup lang="ts">
import { ref, watch } from 'vue'

const model = defineModel<string>({ required: true })
const fileModel = defineModel<File | null>('file', { default: null })
const props = defineProps<{ engine: string }>()

const fileSizeError = ref('')
const MAX_FILE_BYTES = 16 * 1024 * 1024

const seeds = [
  { id: 'blank',     label: 'Blank',           desc: 'empty schema, ready to go',    alwaysEnabled: true },
  { id: 'northwind', label: 'Northwind',       desc: 'classic sample ERP dataset',   alwaysEnabled: false },
  { id: 'ecommerce', label: 'E-commerce',      desc: 'products, orders, customers',  alwaysEnabled: false },
  { id: 'blog',      label: 'Blog',            desc: 'posts, tags, comments',        alwaysEnabled: false },
  { id: 'iot',       label: 'IoT',             desc: 'time-series sensor data',      alwaysEnabled: false },
  { id: 'dump',      label: '↑ Import dump',   desc: 'restore from your .sql file',  alwaysEnabled: false },
]

function isEnabled(_seed: typeof seeds[number]): boolean {
  return true
}

function onFileChange(e: Event) {
  const input = e.target as HTMLInputElement
  const f = input.files?.[0] ?? null
  fileSizeError.value = ''
  if (!f) return
  if (f.size > MAX_FILE_BYTES) {
    fileSizeError.value = 'File exceeds the 16 MiB limit — choose a smaller dump.'
    input.value = ''
    return
  }
  fileModel.value = f
}

// Reset to 'blank' if the currently-selected option becomes disabled due to an engine change.
// All seeds are currently enabled on all engines — this guard is a future-proofing safeguard.
watch(() => props.engine, () => {
  const current = seeds.find(s => s.id === model.value)
  if (current && !isEnabled(current)) {
    model.value = 'blank'
  }
})

// Clear file-size error when model (seed) changes.
watch(model, () => { fileSizeError.value = '' })
</script>

<template>
  <div>
    <div class="grid-3" style="gap: 6px;">
      <div
        v-for="s in seeds"
        :key="s.id"
        :class="['opt', model === s.id && 'selected']"
        style="padding: 10px 12px; text-align: left;"
        @click="model = s.id"
      >
        <div style="font-size: 12px; font-weight: 500;">{{ s.label }}</div>
        <div class="dim" style="font-size: 10.5px;">{{ s.desc }}</div>
      </div>
    </div>
    <div v-if="model === 'dump'" style="margin-top: 10px;">
      <input type="file" accept=".sql,.js" data-test="dump-file" @change="onFileChange" />
      <p v-if="fileSizeError" style="color: var(--danger, #e05252); font-size: 12px; margin: 4px 0 0;">
        {{ fileSizeError }}
      </p>
    </div>
  </div>
</template>
