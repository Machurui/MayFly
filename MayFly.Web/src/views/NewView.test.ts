import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { nextTick } from 'vue'
import { VueQueryPlugin } from '@tanstack/vue-query'

const push = vi.hoisted(() => vi.fn())
const createInstance = vi.hoisted(() => vi.fn())
const importDump = vi.hoisted(() => vi.fn())

vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))
vi.mock('../api/instances', () => ({ createInstance, importDump }))

import NewView from './NewView.vue'

describe('NewView', () => {
  beforeEach(() => { push.mockReset(); createInstance.mockReset(); importDump.mockReset() })

  it('creates with defaults and navigates to the instance', async () => {
    createInstance.mockResolvedValue({ token: 'tok123' })
    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })
    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()
    expect(createInstance).toHaveBeenCalledWith(
      expect.objectContaining({ engine: 'postgres', ttlHours: 3, storageMb: 256, initialData: 'blank' }))
    expect(push).toHaveBeenCalledWith('/instance/tok123')
  })

  it('shows quota message on 429', async () => {
    createInstance.mockRejectedValue({ status: 429, message: 'IP quota of 3 active databases reached' })
    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })
    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()
    expect(w.text()).toContain('quota')
  })

  // ── dump flow ────────────────────────────────────────────────────────────────

  it('submit button is disabled when dump is selected but no file chosen', async () => {
    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })
    const opts = w.findAll('.opt')
    const dumpCard = opts.find(o => o.text().includes('Import dump'))!
    await dumpCard.trigger('click')
    await nextTick()
    const btn = w.find('[data-test="create"]')
    expect(btn.attributes('disabled')).toBeDefined()
  })

  it('with dump + file: calls createInstance(blank) then importDump, navigates on success', async () => {
    createInstance.mockResolvedValue({ token: 'tok456' })
    importDump.mockResolvedValue({ success: true, output: 'Imported 5 tables', truncated: false, ms: 42 })

    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })

    // Select dump
    const opts = w.findAll('.opt')
    const dumpCard = opts.find(o => o.text().includes('Import dump'))!
    await dumpCard.trigger('click')
    await nextTick()

    // Set file
    const file = new File(['INSERT INTO foo VALUES (1)'], 'dump.sql')
    const input = w.find('input[type="file"]').element as HTMLInputElement
    Object.defineProperty(input, 'files', { value: [file], configurable: true })
    await w.find('input[type="file"]').trigger('change')
    await nextTick()

    // Submit
    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()

    expect(createInstance).toHaveBeenCalledWith(
      expect.objectContaining({ initialData: 'blank' }))
    expect(importDump).toHaveBeenCalledWith('tok456', file)
    expect(push).toHaveBeenCalledWith('/instance/tok456')
  })

  it('with dump + file: shows error output on import failure and does not navigate', async () => {
    createInstance.mockResolvedValue({ token: 'tok789' })
    importDump.mockResolvedValue({
      success: false, output: 'ERROR: syntax error at line 3', error: 'syntax error at line 3', truncated: false, ms: 10,
    })

    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })

    const opts = w.findAll('.opt')
    const dumpCard = opts.find(o => o.text().includes('Import dump'))!
    await dumpCard.trigger('click')
    await nextTick()

    const file = new File(['bad sql'], 'bad.sql')
    const input = w.find('input[type="file"]').element as HTMLInputElement
    Object.defineProperty(input, 'files', { value: [file], configurable: true })
    await w.find('input[type="file"]').trigger('change')
    await nextTick()

    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()

    expect(push).not.toHaveBeenCalled()
    expect(w.text()).toMatch(/syntax error/i)
  })

  it('with dump + file: handles importDump rejection and surfaces error', async () => {
    createInstance.mockResolvedValue({ token: 'tok999' })
    importDump.mockRejectedValue({ message: 'Network timeout' })

    const w = mount(NewView, { global: { plugins: [VueQueryPlugin] } })

    const opts = w.findAll('.opt')
    const dumpCard = opts.find(o => o.text().includes('Import dump'))!
    await dumpCard.trigger('click')
    await nextTick()

    const file = new File(['INSERT INTO foo VALUES (1)'], 'dump.sql')
    const input = w.find('input[type="file"]').element as HTMLInputElement
    Object.defineProperty(input, 'files', { value: [file], configurable: true })
    await w.find('input[type="file"]').trigger('change')
    await nextTick()

    await w.find('[data-test="create"]').trigger('click')
    await flushPromises()

    expect(push).not.toHaveBeenCalled()
    expect(w.text()).toContain('Network timeout')
  })
})
