import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { ref, nextTick } from 'vue'

const useInstance = vi.hoisted(() => vi.fn())
const runQuery = vi.hoisted(() => vi.fn())

vi.mock('../api/instances', () => ({ useInstance, runQuery }))

// Lightweight CodeMirror stubs — enough for component logic tests
vi.mock('codemirror', () => ({
  EditorView: class {
    _doc = ''
    _parent: Element | null = null
    constructor({ parent, doc }: { parent?: Element; doc?: string } = {}) {
      this._doc = doc ?? ''
      this._parent = parent ?? null
      if (parent instanceof Element) {
        const el = document.createElement('div')
        el.className = 'cm-content'
        el.textContent = this._doc
        parent.appendChild(el)
      }
    }
    get state() {
      return { doc: { toString: () => this._doc, length: this._doc.length } }
    }
    dispatch(spec: { changes?: { insert?: string } }) {
      if (spec?.changes?.insert !== undefined) this._doc = spec.changes.insert
    }
    destroy() {}
  },
  basicSetup: [],
}))
vi.mock('@codemirror/view', () => ({ keymap: { of: () => [] } }))
vi.mock('@codemirror/lang-sql', () => ({ sql: () => [], PostgreSQL: {} }))
vi.mock('@codemirror/lang-javascript', () => ({ javascript: () => [] }))

import ConsoleView from './ConsoleView.vue'
import type { QueryResultDto } from '../api/types'

const mongoResult: QueryResultDto = {
  success: true,
  output: '[{"_id":"abc","name":"test"}]',
  columns: [], rows: [], rowCount: 1, durationMs: 5, message: '', error: null,
}

const sqlResult: QueryResultDto = {
  success: true, output: undefined,
  columns: ['n'], rows: [[1]], rowCount: 1, durationMs: 5, message: '1 row(s)', error: null,
}

describe('ConsoleView', () => {
  beforeEach(() => { useInstance.mockReset(); runQuery.mockReset() })

  it('mongo: mounts JS editor (doc contains db.) and renders mongo-output pre', async () => {
    const instData = ref<Record<string, string> | undefined>(undefined)
    useInstance.mockReturnValue({ data: instData })
    runQuery.mockResolvedValue(mongoResult)

    const w = mount(ConsoleView, { props: { token: 'tok-mongo' } })

    // Simulate async instance load
    instData.value = { engine: 'mongo', token: 'tok-mongo' }
    await nextTick()

    // JS editor default doc contains 'db.'
    expect(w.text()).toContain('db.')

    // Run query
    await w.find('.btn.primary').trigger('click')
    await flushPromises()

    // Mongo output pane — no tabular results
    const pre = w.find('pre.mongo-output')
    expect(pre.exists()).toBe(true)
    expect(pre.text()).toContain('"_id"')
    expect(w.find('table').exists()).toBe(false)

    w.unmount()
  })

  it('postgres: mounts SQL editor (doc contains SELECT) and renders QueryResults', async () => {
    const instData = ref<Record<string, string> | undefined>(undefined)
    useInstance.mockReturnValue({ data: instData })
    runQuery.mockResolvedValue(sqlResult)

    const w = mount(ConsoleView, { props: { token: 'tok-pg' } })

    instData.value = { engine: 'postgres', token: 'tok-pg' }
    await nextTick()

    // SQL editor default doc
    expect(w.text()).toContain('SELECT')

    // Run query
    await w.find('.btn.primary').trigger('click')
    await flushPromises()

    // QueryResults table visible; no mongo-output
    expect(w.find('table').exists()).toBe(true)
    expect(w.find('pre.mongo-output').exists()).toBe(false)

    w.unmount()
  })

  it('mongo: shows result.error when query fails', async () => {
    const instData = ref<Record<string, string> | undefined>(undefined)
    useInstance.mockReturnValue({ data: instData })
    runQuery.mockResolvedValue({
      success: false, output: undefined,
      columns: [], rows: [], rowCount: 0, durationMs: 2, message: '', error: 'bad query',
    })

    const w = mount(ConsoleView, { props: { token: 'tok-err' } })
    instData.value = { engine: 'mongo', token: 'tok-err' }
    await nextTick()

    await w.find('.btn.primary').trigger('click')
    await flushPromises()

    const pre = w.find('pre.mongo-output')
    expect(pre.exists()).toBe(true)
    expect(pre.text()).toContain('bad query')

    w.unmount()
  })

  it('mongo: shows truncated note when result.truncated is true', async () => {
    const instData = ref<Record<string, string> | undefined>(undefined)
    useInstance.mockReturnValue({ data: instData })
    runQuery.mockResolvedValue({ ...mongoResult, truncated: true })

    const w = mount(ConsoleView, { props: { token: 'tok-trunc' } })
    instData.value = { engine: 'mongo', token: 'tok-trunc' }
    await nextTick()

    await w.find('.btn.primary').trigger('click')
    await flushPromises()

    expect(w.find('.truncated-note').exists()).toBe(true)
    expect(w.find('.truncated-note').text()).toContain('truncated')

    w.unmount()
  })

  it('mongo: builds editor even when instance is cached (engine known before ref bind)', async () => {
    // Simulate warm cache: instance data is already populated when mount() runs
    const instData = ref({ engine: 'mongo', token: 'tok-cached' })
    useInstance.mockReturnValue({ data: instData })

    const w = mount(ConsoleView, { props: { token: 'tok-cached' } })
    // No instData.value assignment here; it's already set above

    await nextTick()

    // Editor should have been built despite engine being known before editorEl bound
    // The JS editor's default doc contains 'db.'
    expect(w.text()).toContain('db.')

    w.unmount()
  })
})
