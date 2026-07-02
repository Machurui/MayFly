<script setup lang="ts">
const model = defineModel<number>({ required: true })

const sizes = [
  { mb: 256,  label: '256 MB', desc: 'hobby · quick poc' },
  { mb: 512,  label: '512 MB', desc: 'small dataset' },
  { mb: 1024, label: '1 GB',   desc: 'medium project' },
  { mb: 2048, label: '2 GB',   desc: 'large dataset' },
]
</script>

<template>
  <div class="opt-row">
    <div
      v-for="s in sizes"
      :key="s.mb"
      :class="['opt', model === s.mb && 'selected']"
      style="padding: 12px; text-align: left;"
      @click="model = s.mb"
    >
      <span style="font-size: 16px; font-weight: 500;">{{ s.label }}</span>
      <div class="dim" style="font-size: 11px;">{{ s.desc }}</div>
    </div>
  </div>
</template>
