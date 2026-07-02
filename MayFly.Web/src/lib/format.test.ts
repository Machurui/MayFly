import { describe, it, expect, vi, afterEach } from 'vitest'
import { formatBytes, timeUntil } from './format'

describe('formatBytes', () => {
  it('formats MB and GB', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(256 * 1024 * 1024)).toBe('256.0 MB')
    expect(formatBytes(2 * 1024 ** 3)).toBe('2.0 GB')
  })
})

describe('timeUntil', () => {
  afterEach(() => vi.useRealTimers())
  it('returns expired for past dates', () => {
    vi.useFakeTimers(); vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
    expect(timeUntil('2025-12-31T23:59:59Z')).toBe('expired')
  })
  it('returns h/m for future dates', () => {
    vi.useFakeTimers(); vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
    expect(timeUntil('2026-01-01T02:01:00Z')).toBe('2h 1m')
  })
})
