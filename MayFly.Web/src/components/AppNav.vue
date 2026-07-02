<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

const route = useRoute()

const screenTitle = computed<string>(() => {
  switch (route.name) {
    case 'new':      return 'create'
    case 'instance': return 'instance'
    case 'console':  return 'sql console'
    case 'all':
    default:         return 'instances'
  }
})
</script>

<template>
  <div class="topbar">
    <!-- Logo: mark + "mayfly / <title>" -->
    <div class="logo">
      <div class="mark">≈</div>
      <span>mayfly</span>
      <span style="color: var(--text-3)">/</span>
      <span style="color: var(--text-2)">{{ screenTitle }}</span>
    </div>

    <!-- Nav -->
    <nav class="nav">
      <RouterLink to="/new" exact-active-class="active">▸ new</RouterLink>
      <!-- instance / console: dim placeholders — no token context in slice-1 -->
      <a class="dim" style="pointer-events: none; opacity: 0.45">▸ instance</a>
      <a class="dim" style="pointer-events: none; opacity: 0.45">▸ console</a>
      <RouterLink to="/" exact-active-class="active">▸ all</RouterLink>
    </nav>

    <!-- Spacer -->
    <div style="flex: 1" />

    <!-- Right: region / status -->
    <div class="row g-3" style="font-size: 11px; color: var(--text-3)">
      <span>region <span style="color: var(--text-2)">fra-1</span></span>
      <span>·</span>
      <span>status <span style="color: var(--accent)">● online</span></span>
    </div>
  </div>
</template>
