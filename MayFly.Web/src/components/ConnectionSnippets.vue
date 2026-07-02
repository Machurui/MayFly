<script setup lang="ts">
import { ref, computed } from 'vue'
import type { InstanceDto } from '../api/types'
import { buildSnippets } from '../lib/snippets'

const props = defineProps<{ instance: InstanceDto }>()

const langs = ['bash', 'python', 'node', 'go', 'dotnet'] as const
type Lang = typeof langs[number]

const labels: Record<Lang, string> = {
  bash: 'bash', python: 'python', node: 'node', go: 'go', dotnet: '.net',
}

const active = ref<Lang>('bash')
const snippets = computed(() => buildSnippets(props.instance))

const copied = ref(false)
async function copy() {
  await navigator.clipboard.writeText(snippets.value[active.value])
  copied.value = true
  setTimeout(() => { copied.value = false }, 1500)
}
</script>

<template>
  <div>
    <div class="tabs-row">
      <button
        v-for="l in langs"
        :key="l"
        class="btn sm tab-btn"
        :class="{ 'tab-active': active === l }"
        @click="active = l"
      >{{ labels[l] }}</button>
    </div>
    <div class="snippet-wrap">
      <button class="btn sm copy-btn" @click="copy">{{ copied ? 'copied' : 'copy' }}</button>
      <pre class="code">{{ snippets[active] }}</pre>
    </div>
  </div>
</template>

<style scoped>
.tabs-row {
  display: flex;
  gap: 4px;
  padding: var(--pad-2);
  border-bottom: 1px solid var(--border);
  background: var(--surface-2);
}
.tab-btn {
  background: transparent;
  border-color: transparent;
  color: var(--text-3);
}
.tab-btn:hover {
  color: var(--text-2);
  background: var(--surface-2);
  border-color: var(--border);
}
.tab-active {
  background: var(--surface-3) !important;
  border-color: var(--border-3) !important;
  color: var(--text) !important;
}
.snippet-wrap {
  position: relative;
}
.copy-btn {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 1;
}
.code {
  margin: 0;
}
</style>
