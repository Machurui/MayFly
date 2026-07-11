import { useQuery } from '@tanstack/vue-query'
import { api } from './client'
import type { InstanceDto, CreateInstanceDto, QueryResultDto, DashboardSummary } from './types'

export const createInstance = (dto: CreateInstanceDto) => api.post<InstanceDto>('/api/instances', dto)
export const getInstance = (token: string) => api.get<InstanceDto>(`/api/instances/${token}`)
export const listMine = () => api.get<InstanceDto[]>('/api/instances')
export const destroyInstance = (token: string) => api.del(`/api/instances/${token}`)
export const runQuery = (token: string, query: string) =>
  api.post<QueryResultDto>(`/api/instances/${token}/query`, { query })
export const getDashboard = () => api.get<DashboardSummary>('/api/dashboard')

export const useMyInstances = () =>
  useQuery({ queryKey: ['instances'], queryFn: listMine, refetchInterval: 10000 })
export const useDashboard = () =>
  useQuery({ queryKey: ['dashboard'], queryFn: getDashboard, refetchInterval: 10000 })
export const useInstance = (token: string) =>
  useQuery({ queryKey: ['instance', token], queryFn: () => getInstance(token), refetchInterval: 10000 })
