<script setup lang="ts">
import { ref, computed, onUnmounted, watch, shallowRef } from 'vue'
import { EditorView, basicSetup } from 'codemirror'
import { sql, PostgreSQL } from '@codemirror/lang-sql'
import { javascript } from '@codemirror/lang-javascript'
import { keymap } from '@codemirror/view'
import { useInstance, runQuery } from '../api/instances'
import type { QueryResultDto } from '../api/types'
import QueryResults from '../components/QueryResults.vue'

const props = defineProps<{ token: string }>()

const { data: inst } = useInstance(props.token)
const isMongo = computed(() => inst.value?.engine === 'mongo')

const editorEl = ref<HTMLDivElement>()
const view = shallowRef<EditorView>()
const result = ref<QueryResultDto | null>(null)
const busy = ref(false)

async function run() {
  if (busy.value || !view.value) return
  busy.value = true
  try {
    result.value = await runQuery(props.token, view.value.state.doc.toString())
  } finally {
    busy.value = false
  }
}

function clearEditor() {
  if (!view.value) return
  view.value.dispatch({ changes: { from: 0, to: view.value.state.doc.length, insert: '' } })
  result.value = null
}

function buildEditor(engine: string) {
  if (!editorEl.value) return
  const mongo = engine === 'mongo'
  view.value = new EditorView({
    parent: editorEl.value,
    doc: mongo ? 'db.getCollection("items").find()' : 'SELECT 1;',
    extensions: [
      basicSetup,
      mongo ? javascript() : sql({ dialect: PostgreSQL }),
      keymap.of([{
        key: 'Mod-Enter',
        run() { run(); return true },
      }]),
    ],
  })
}

watch(() => inst.value?.engine, (engine) => {
  if (!engine || view.value) return
  buildEditor(engine)
}, { immediate: true })

onUnmounted(() => {
  view.value?.destroy()
})
</script>

<template>
  <div class="console-grid">
    <!-- toolbar -->
    <div class="console-toolbar">
      <span class="dim" style="font-size: 11px; text-transform: uppercase; letter-spacing: .08em;">
        <span style="color: var(--accent); margin-right: 6px;">⬡</span>
        {{ token }}
      </span>
      <div class="vr"></div>
      <button class="btn primary" :disabled="busy" @click="run">
        {{ busy ? 'running…' : '▸ run' }}
        <kbd>⌘⏎</kbd>
      </button>
      <button class="btn sm ghost" @click="clearEditor">clear</button>
    </div>

    <!-- editor -->
    <div style="background: var(--bg-2); overflow: auto; border-bottom: 1px solid var(--border);">
      <div ref="editorEl" style="height: 100%;"></div>
    </div>

    <!-- results -->
    <template v-if="isMongo">
      <pre v-if="result" class="mongo-output">{{ result.success ? result.output : result.error }}</pre>
      <p v-if="result?.truncated" class="truncated-note">output truncated</p>
    </template>
    <QueryResults v-else :result="result" />
  </div>
</template>
