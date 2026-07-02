<script setup lang="ts">
const model = defineModel<number>({ required: true })

const durations = [
  { hours: 3,  label: '3h',  desc: 'good for a quick demo' },
  { hours: 6,  label: '6h',  desc: 'half a day of hacking' },
  { hours: 12, label: '12h', desc: 'a full work session' },
]
</script>

<template>
  <div class="duration-row">
    <div
      v-for="d in durations"
      :key="d.hours"
      :class="['duration-card', model === d.hours && 'selected']"
      @click="model = d.hours"
    >
      <div style="font-size: 22px; font-weight: 500;">{{ d.label }}</div>
      <div class="dim" style="font-size: 11px; margin-top: 4px;">{{ d.desc }}</div>
    </div>
  </div>
</template>
