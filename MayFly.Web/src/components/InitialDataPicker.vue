<script setup lang="ts">
const model = defineModel<string>({ required: true })

const seeds = [
  { id: 'blank',     label: 'Blank',       desc: 'empty schema, ready to go',    enabled: true },
  { id: 'northwind', label: 'Northwind',   desc: 'classic sample ERP dataset',   enabled: true },
  { id: 'ecommerce', label: 'E-commerce',  desc: 'products, orders, customers',  enabled: false },
  { id: 'blog',      label: 'Blog',        desc: 'posts, tags, comments',        enabled: false },
  { id: 'iot',       label: 'IoT',         desc: 'time-series sensor data',      enabled: false },
  { id: 'dump',      label: '↑ Import dump', desc: 'restore from your .sql file', enabled: false },
]
</script>

<template>
  <div class="grid-3" style="gap: 6px;">
    <div
      v-for="s in seeds"
      :key="s.id"
      :class="['opt', model === s.id && 'selected']"
      :style="!s.enabled ? 'opacity: 0.35; pointer-events: none;' : 'padding: 10px 12px; text-align: left;'"
      style="padding: 10px 12px; text-align: left;"
      @click="s.enabled && (model = s.id)"
    >
      <div style="font-size: 12px; font-weight: 500;">{{ s.label }}</div>
      <div class="dim" style="font-size: 10.5px;">{{ s.desc }}</div>
    </div>
  </div>
</template>
