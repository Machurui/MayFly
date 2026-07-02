import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import QueryResults from './QueryResults.vue'
import type { QueryResultDto } from '../api/types'

const ok: QueryResultDto = {
  success: true, columns: ['n', 'name'], rows: [[1, 'a'], [2, 'b']],
  rowCount: 2, durationMs: 5, message: '2 row(s)', error: null,
}

describe('QueryResults', () => {
  it('renders columns and rows', () => {
    const w = mount(QueryResults, { props: { result: ok } })
    expect(w.findAll('thead th').map(t => t.text())).toEqual(['n', 'name'])
    expect(w.findAll('tbody tr')).toHaveLength(2)
    expect(w.text()).toContain('a')
  })

  it('shows error on failure', () => {
    const w = mount(QueryResults, { props: { result: {
      ...ok, success: false, columns: [], rows: [], rowCount: 0, error: 'relation does not exist' } } })
    expect(w.text()).toContain('relation does not exist')
  })
})
