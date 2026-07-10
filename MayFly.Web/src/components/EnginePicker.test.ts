import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import EnginePicker from './EnginePicker.vue'

function mountPicker(modelValue = 'postgres') {
  return mount(EnginePicker, {
    props: { modelValue },
    global: {},
  })
}

describe('EnginePicker', () => {
  it('renders all five engine cards', () => {
    const w = mountPicker()
    const cards = w.findAll('.engine-card')
    expect(cards.length).toBe(5)
  })

  it('postgres is selected by default', () => {
    const w = mountPicker('postgres')
    const selected = w.findAll('.engine-card.selected')
    expect(selected.length).toBe(1)
    expect(selected[0].text()).toContain('PostgreSQL')
  })

  it('clicking mysql emits mssql-independent model value "mysql"', async () => {
    const w = mountPicker('postgres')
    const cards = w.findAll('.engine-card')
    const mysqlCard = cards.find(c => c.text().includes('MySQL'))!
    await mysqlCard.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['mysql'])
  })

  it('clicking mariadb emits "mariadb"', async () => {
    const w = mountPicker('postgres')
    const cards = w.findAll('.engine-card')
    const card = cards.find(c => c.text().includes('MariaDB'))!
    await card.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['mariadb'])
  })

  it('clicking SQL Server emits "mssql" (not "sqlserver")', async () => {
    const w = mountPicker('postgres')
    const cards = w.findAll('.engine-card')
    const card = cards.find(c => c.text().includes('SQL Server'))!
    await card.trigger('click')
    expect(w.emitted('update:modelValue')?.[0]).toEqual(['mssql'])
  })

  it('mongodb card is disabled and clicking it emits nothing', async () => {
    const w = mountPicker('postgres')
    const cards = w.findAll('.engine-card')
    const mongoCard = cards.find(c => c.text().includes('MongoDB'))!
    expect(mongoCard.classes()).toContain('disabled')
    await mongoCard.trigger('click')
    expect(w.emitted('update:modelValue')).toBeFalsy()
  })

  it('mysql, mariadb and mssql cards are NOT disabled', () => {
    const w = mountPicker('postgres')
    const cards = w.findAll('.engine-card')
    for (const label of ['MySQL', 'MariaDB', 'SQL Server']) {
      const card = cards.find(c => c.text().includes(label))!
      expect(card.classes()).not.toContain('disabled')
    }
  })
})
