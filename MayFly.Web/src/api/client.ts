export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function handle<T>(resp: Response): Promise<T> {
  if (!resp.ok) {
    let msg = resp.statusText
    try { const b = await resp.json(); msg = b.error ?? msg } catch { /* non-json */ }
    throw new ApiError(resp.status, msg)
  }
  return resp.status === 204 ? (undefined as T) : await resp.json() as T
}

const jsonHeaders = { 'Content-Type': 'application/json' }
const base: RequestInit = { credentials: 'same-origin' }

export const api = {
  get: <T>(p: string) =>
    fetch(p, { ...base, headers: jsonHeaders }).then(r => handle<T>(r)),
  post: <T>(p: string, body: unknown) => {
    const isForm = body instanceof FormData
    const init: RequestInit = { ...base, method: 'POST', body: isForm ? body : JSON.stringify(body) }
    if (!isForm) init.headers = jsonHeaders
    return fetch(p, init).then(r => handle<T>(r))
  },
  del: (p: string) =>
    fetch(p, { ...base, method: 'DELETE', headers: jsonHeaders }).then(r => handle<void>(r)),
}
