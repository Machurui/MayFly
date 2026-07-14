<script setup lang="ts">
import { watch } from 'vue'

const model = defineModel<string>({ required: true })
const props = defineProps<{ engine: string }>()

const seeds = [
  { id: 'blank',     label: 'Blank',       desc: 'empty schema, ready to go',    alwaysEnabled: true },
  { id: 'northwind', label: 'Northwind',   desc: 'classic sample ERP dataset',   alwaysEnabled: false },
  { id: 'ecommerce', label: 'E-commerce',  desc: 'products, orders, customers',  alwaysEnabled: false },
  { id: 'blog',      label: 'Blog',        desc: 'posts, tags, comments',        alwaysEnabled: false },
  { id: 'iot',       label: 'IoT',         desc: 'time-series sensor data',      alwaysEnabled: false },
  { id: 'dump',      label: '↑ Import dump', desc: 'restore from your .sql file', alwaysEnabled: false },
]

function isEnabled(seed: typeof seeds[number]): boolean {
  if (seed.alwaysEnabled) return true
  if (seed.id === 'dump') return false
  // northwind / ecommerce / blog / iot are enabled on all engines
  return true
}

// Reset to 'blank' if the currently-selected option becomes disabled due to an engine change.
// With all four templates enabled on every engine this never fires for templates — harmless.
watch(() => props.engine, () => {
  const current = seeds.find(s => s.id === model.value)
  if (current && !isEnabled(current)) {
    model.value = 'blank'
  }
})
</script>

<template>
  <div class="grid-3" style="gap: 6px;">
    <div
      v-for="s in seeds"
      :key="s.id"
      :class="['opt', model === s.id && 'selected']"
      :style="!isEnabled(s) ? 'opacity: 0.35; pointer-events: none;' : 'padding: 10px 12px; text-align: left;'"
      style="padding: 10px 12px; text-align: left;"
      @click="isEnabled(s) && (model = s.id)"
    >
      <div style="font-size: 12px; font-weight: 500;">{{ s.label }}</div>
      <div class="dim" style="font-size: 10.5px;">{{ s.desc }}</div>
    </div>
  </div>
</template>
