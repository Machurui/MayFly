import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import InitialDataPicker from './InitialDataPicker.vue'

function mountPicker(engine: string, modelValue = 'blank') {
  return mount(InitialDataPicker, {
    props: { modelValue, engine },
  })
}

const ENGINES = ['postgres', 'mysql', 'mariadb', 'mssql', 'mongo'] as const
const TEMPLATES = [
  { id: 'northwind', label: 'Northwind' },
  { id: 'ecommerce', label: 'E-commerce' },
  { id: 'blog',      label: 'Blog' },
  { id: 'iot',       label: 'IoT' },
] as const

describe('InitialDataPicker', () => {
  // ── All four seed templates enabled on every engine ─────────────────────────
  for (const engine of ENGINES) {
    for (const { id, label } of TEMPLATES) {
      it(`${id} is enabled on ${engine}`, () => {
        const w = mountPicker(engine)
        const opts = w.findAll('.opt')
        const card = opts.find(o => o.text().includes(label))!
        expect(card.attributes('style')).not.toContain('pointer-events: none')
      })

      it(`clicking ${id} on ${engine} emits "${id}"`, async () => {
        const w = mountPicker(engine)
        const opts = w.findAll('.opt')
        const card = opts.find(o => o.text().includes(label))!
        await card.trigger('click')
        expect(w.emitted('update:modelValue')?.[0]).toEqual([id])
      })
    }
  }

  // ── blank is always selectable ───────────────────────────────────────────────
  for (const engine of ENGINES) {
    it(`blank is selectable on ${engine}`, async () => {
      // Mount with northwind so clicking blank actually triggers an emit
      const w = mountPicker(engine, 'northwind')
      const opts = w.findAll('.opt')
      const blank = opts.find(o => o.text().includes('Blank'))!
      expect(blank.attributes('style')).not.toContain('pointer-events: none')
      await blank.trigger('click')
      expect(w.emitted('update:modelValue')?.[0]).toEqual(['blank'])
    })
  }

  // ── dump is enabled and selectable ──────────────────────────────────────────
  for (const engine of ENGINES) {
    it(`dump is enabled on ${engine} — clicking emits "dump"`, async () => {
      const w = mountPicker(engine)
      const opts = w.findAll('.opt')
      const dump = opts.find(o => o.text().includes('Import dump'))!
      expect(dump.attributes('style')).not.toContain('pointer-events: none')
      await dump.trigger('click')
      expect(w.emitted('update:modelValue')?.[0]).toEqual(['dump'])
    })
  }

  // ── file input appears when dump is selected ─────────────────────────────────
  it('renders a file input when dump is selected', () => {
    const w = mountPicker('postgres', 'dump')
    expect(w.find('input[type="file"]').exists()).toBe(true)
  })

  it('does NOT render a file input when blank is selected', () => {
    const w = mountPicker('postgres', 'blank')
    expect(w.find('input[type="file"]').exists()).toBe(false)
  })

  // ── selecting a file emits it ────────────────────────────────────────────────
  it('selecting a valid file emits it via update:file', async () => {
    const w = mountPicker('postgres', 'dump')
    const file = new File(['SELECT 1'], 'dump.sql', { type: 'text/plain' })
    const input = w.find('input[type="file"]').element as HTMLInputElement
    Object.defineProperty(input, 'files', { value: [file], configurable: true })
    await w.find('input[type="file"]').trigger('change')
    expect(w.emitted('update:file')?.[0]).toEqual([file])
  })

  // ── 16 MiB cap ───────────────────────────────────────────────────────────────
  it('rejects a file > 16 MiB — does not emit and shows an error message', async () => {
    const w = mountPicker('postgres', 'dump')
    const bigFile = new File(['x'], 'big.sql')
    Object.defineProperty(bigFile, 'size', { value: 17 * 1024 * 1024, configurable: true })
    const input = w.find('input[type="file"]').element as HTMLInputElement
    Object.defineProperty(input, 'files', { value: [bigFile], configurable: true })
    await w.find('input[type="file"]').trigger('change')
    expect(w.emitted('update:file')).toBeFalsy()
    await nextTick()
    expect(w.text()).toMatch(/16 MiB|exceeds|limit/i)
  })

  // ── Watch: templates stay enabled across engine changes — no reset ───────────
  it('does NOT reset when engine changes from postgres to mysql and northwind was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mysql' })
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('does NOT reset when engine changes from postgres to mssql and northwind was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mssql' })
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('does NOT reset when engine changes from postgres to mongo and blog was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'blog', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mongo' })
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('does NOT reset when engine changes from postgres to mysql and blank was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'blank', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mysql' })
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  // ── Regression: blank card not disabled even when a different seed is selected ─
  it('blank is not disabled when engine is mssql and model is northwind', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'mssql' },
    })
    const opts = w.findAll('.opt')
    const blank = opts.find(o => o.text().includes('Blank'))!
    expect(blank.attributes('style')).not.toContain('pointer-events: none')
    await blank.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['blank'])
  })
})
