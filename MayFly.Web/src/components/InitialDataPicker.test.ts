import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import InitialDataPicker from './InitialDataPicker.vue'

function mountPicker(engine: string, modelValue = 'blank') {
  return mount(InitialDataPicker, {
    props: { modelValue, engine },
  })
}

describe('InitialDataPicker', () => {
  it('northwind is enabled when engine is postgres', () => {
    const w = mountPicker('postgres')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    // pointer-events:none indicates disabled — should NOT be set for postgres
    expect(nw.attributes('style')).not.toContain('pointer-events: none')
  })

  it('northwind is disabled when engine is mysql', () => {
    const w = mountPicker('mysql')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    expect(nw.attributes('style')).toContain('pointer-events: none')
  })

  it('northwind is disabled when engine is mariadb', () => {
    const w = mountPicker('mariadb')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    expect(nw.attributes('style')).toContain('pointer-events: none')
  })

  it('northwind is disabled when engine is mssql', () => {
    const w = mountPicker('mssql')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    expect(nw.attributes('style')).toContain('pointer-events: none')
  })

  it('clicking northwind when postgres emits "northwind"', async () => {
    const w = mountPicker('postgres')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    await nw.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['northwind'])
  })

  it('clicking northwind when mysql emits nothing', async () => {
    const w = mountPicker('mysql')
    const opts = w.findAll('.opt')
    const nw = opts.find(o => o.text().includes('Northwind'))!
    await nw.trigger('click')
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('resets model to blank when engine changes from postgres to mysql and northwind was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mysql' })
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['blank'])
  })

  it('does NOT reset when engine changes from postgres to mysql and blank was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'blank', engine: 'postgres' },
    })
    await w.setProps({ engine: 'mysql' })
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('resets model to blank when engine changes from postgres to mssql and northwind was selected', async () => {
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'postgres' },
    })
    // Switch to mssql engine — watcher should reset to blank
    await w.setProps({ engine: 'mssql' })
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['blank'])
  })

  it('blank is always selectable when engine is mssql and model is northwind', async () => {
    // Mount with mssql + northwind, confirm blank card is not disabled
    const w = mount(InitialDataPicker, {
      props: { modelValue: 'northwind', engine: 'mssql' },
    })
    const opts = w.findAll('.opt')
    const blank = opts.find(o => o.text().includes('Blank'))!
    expect(blank.attributes('style')).not.toContain('pointer-events: none')
    // Clicking blank (when northwind is currently selected) should emit 'blank'
    await blank.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['blank'])
  })
})
