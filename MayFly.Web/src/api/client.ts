export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function handle<T>(resp: Response): Promise<T> {
  if (!resp.ok) {
    let msg = resp.statusText
    try { const b = await resp.json(); msg = b.error ?? msg } catch { /* non-json */ }
    throw new ApiError(resp.status, msg)
  }
  return resp.status === 204 ? (undefined as T) : await resp.json() as T
}

const opts: RequestInit = { credentials: 'same-origin', headers: { 'Content-Type': 'application/json' } }

export const api = {
  get: <T>(p: string) => fetch(p, opts).then(r => handle<T>(r)),
  post: <T>(p: string, body: unknown) =>
    fetch(p, { ...opts, method: 'POST', body: JSON.stringify(body) }).then(r => handle<T>(r)),
  del: (p: string) => fetch(p, { ...opts, method: 'DELETE' }).then(r => handle<void>(r)),
}
