// Session wiring: the shell navigates WebView2 to the served app with
// ?token=... (and ?apiBase=... when pointing at the Vite dev server). The
// token is consumed once, held in memory only, and stripped from the URL.

let token = ''
let apiBase = ''

export function initSession() {
  const params = new URLSearchParams(window.location.search)
  const t = params.get('token')
  const base = params.get('apiBase')
  if (t) {
    token = t
    sessionStorage.setItem('pt-token', t) // survives SPA reloads inside one WebView2 session
  } else {
    token = sessionStorage.getItem('pt-token') ?? ''
  }
  if (base) {
    apiBase = base.replace(/\/$/, '')
    sessionStorage.setItem('pt-api-base', apiBase)
  } else {
    apiBase = sessionStorage.getItem('pt-api-base') ?? ''
  }
  if (t || base) {
    params.delete('token')
    params.delete('apiBase')
    const rest = params.toString()
    window.history.replaceState(null, '', window.location.pathname + (rest ? `?${rest}` : ''))
  }
}

export function getToken() {
  return token
}

export function getApiBase() {
  return apiBase
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function request<T>(
  method: string,
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const headers: Record<string, string> = { Authorization: `Bearer ${token}` }
  let payload: BodyInit | undefined
  if (body instanceof FormData) {
    payload = body
  } else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }
  // aborting matters for rate-limited endpoints: a cancelled request releases
  // its queued slot on the server's Finnhub rate gate
  const resp = await fetch(`${apiBase}${path}`, { method, headers, body: payload, signal })
  if (!resp.ok) {
    let message = `${resp.status} ${resp.statusText}`
    try {
      const problem = await resp.json()
      if (problem?.error) message = problem.error
    } catch {
      /* non-JSON error body */
    }
    throw new ApiError(resp.status, message)
  }
  if (resp.status === 204) return undefined as T
  return (await resp.json()) as T
}

export const api = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>('GET', path, undefined, signal),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
  patch: <T>(path: string, body?: unknown) => request<T>('PATCH', path, body),
  delete: <T>(path: string) => request<T>('DELETE', path),
}

/// For file downloads (8949 CSV): fetch with auth, hand back a blob URL.
export async function downloadFile(path: string, filename: string) {
  const resp = await fetch(`${apiBase}${path}`, {
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!resp.ok) throw new ApiError(resp.status, resp.statusText)
  const blob = await resp.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}
