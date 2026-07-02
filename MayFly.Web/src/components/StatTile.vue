<script setup lang="ts">
defineProps<{ label: string; value: string; sub?: string }>()
</script>

<template>
  <div class="stat-tile">
    <div class="label">{{ label }}</div>
    <div class="value num">{{ value }}</div>
    <div v-if="sub" class="sub">{{ sub }}</div>
  </div>
</template>
