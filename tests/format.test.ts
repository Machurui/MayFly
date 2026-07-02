import { describe, it, expect } from 'vitest'
import { formatBytes, timeUntil } from '../src/lib/format'

describe('formatBytes', () => {
  it('formats MB and GB', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(256 * 1024 * 1024)).toBe('256.0 MB')
    expect(formatBytes(2 * 1024 ** 3)).toBe('2.0 GB')
  })
})

describe('timeUntil', () => {
  it('returns expired for past dates', () => {
    expect(timeUntil(new Date(Date.now() - 1000).toISOString())).toBe('expired')
  })
  it('returns h/m for future dates', () => {
    expect(timeUntil(new Date(Date.now() + 2 * 3600_000 + 60_000).toISOString())).toMatch(/^2h/)
  })
})
