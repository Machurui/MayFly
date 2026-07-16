export interface InstanceDto {
  token: string; engine: string; state: string; ttlHours: number; storageQuotaMb: number;
  lastSizeBytes: number; initialData: string; createdAt: string; expiresAt: string;
  connectionString: string; publicPort: number; dbName: string; dbUser: string;
}
export interface CreateInstanceDto { engine: string; ttlHours: number; storageMb: number; initialData: string; }
export interface QueryResultDto {
  success: boolean; columns: string[]; rows: unknown[][]; rowCount: number;
  durationMs: number; message: string; error: string | null;
  output?: string; truncated?: boolean;
}
export interface DashboardSummary {
  aliveCount: number; maxAlive: number; queriesToday: number;
  storageUsedBytes: number; nextExpiry: string | null;
}
export interface ImportResultDto {
  success: boolean; output: string; error?: string | null; truncated: boolean; ms: number;
}
