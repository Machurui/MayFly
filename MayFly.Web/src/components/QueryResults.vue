<script setup lang="ts">
import { ref } from 'vue'
import type { QueryResultDto } from '../api/types'

const props = defineProps<{ result: QueryResultDto | null }>()
const tab = ref<'results' | 'messages'>('results')

function isNumeric(val: unknown): boolean {
  if (typeof val === 'number') return true
  if (typeof val === 'string' && val.trim() !== '') return !isNaN(Number(val))
  return false
}
</script>

<template>
  <div class="console-results col">
    <!-- tab bar -->
    <div class="row" style="border-bottom: 1px solid var(--border); background: var(--surface-2);">
      <button
        class="btn ghost"
        :class="{ accent: tab === 'results' }"
        :style="tab === 'results' ? 'border-bottom: 2px solid var(--accent); border-radius: 0;' : 'border-bottom: 2px solid transparent;'"
        @click="tab = 'results'"
      >▸ results</button>
      <button
        class="btn ghost"
        :class="{ accent: tab === 'messages' }"
        :style="tab === 'messages' ? 'border-bottom: 2px solid var(--accent); border-radius: 0;' : 'border-bottom: 2px solid transparent;'"
        @click="tab = 'messages'"
      >▸ messages</button>
    </div>

    <!-- results tab -->
    <div v-if="tab === 'results'" style="overflow: auto; flex: 1;">
      <template v-if="!result">
        <div class="empty">no results yet — hit ⌘⏎ to run</div>
      </template>
      <template v-else-if="!result.success">
        <p class="danger" style="padding: var(--pad-3);">{{ result.error ?? result.message ?? '(query failed)' }}</p>
      </template>
      <template v-else>
        <table class="tbl">
          <thead>
            <tr>
              <th v-for="col in result.columns" :key="col">{{ col }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, ri) in result.rows" :key="ri">
              <td
                v-for="(cell, ci) in row"
                :key="ci"
                :class="{ num: isNumeric(cell) }"
              >{{ cell }}</td>
            </tr>
          </tbody>
        </table>
      </template>
    </div>

    <!-- messages tab -->
    <div v-else style="padding: var(--pad-3); flex: 1; overflow: auto;">
      <template v-if="!result">
        <div class="empty">no results yet — hit ⌘⏎ to run</div>
      </template>
      <template v-else>
        <p :class="{ danger: !result.success }">{{ result.error ?? result.message }}</p>
        <p class="dim">{{ result.rowCount }} row(s) · {{ result.durationMs }} ms</p>
      </template>
    </div>
  </div>
</template>
