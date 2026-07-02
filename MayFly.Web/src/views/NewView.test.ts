import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { VueQueryPlugin } from '@tanstack/vue-query'

const push = vi.hoisted(() => vi.fn())
const createInstance = vi.hoisted(() => vi.fn())

vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))
vi.mock('../api/instances', () => ({ createInstance }))

import NewView from './NewView.vue'

describe('NewView', () => {
  beforeEach(() => { push.mockReset(); createInstance.mockReset() })

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
})
