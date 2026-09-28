// Talks to the ArchLens API (src/ArchLens.Api). In dev, Vite proxies /api to it.

export type AnalysisStatus = 'queued' | 'cloning' | 'analyzing' | 'done' | 'failed'

export interface Analysis {
  id: string
  repoUrl: string
  status: AnalysisStatus
  message: string
  commit?: string
  error?: string
  cached: boolean
  elapsedMs: number
}

// Raised when the API answered with an error message (as opposed to not answering at all).
export class ApiError extends Error {}

// Raised when nothing answered: the API process is not running or the proxy has no target.
export class ApiDownError extends Error {
  constructor() {
    super("The analyzer API isn't running. Start it with: dotnet run --project src/ArchLens.Api")
  }
}

// Accepts "github.com/owner/repo", with or without https://, www., a trailing slash or ".git".
// Returns the canonical "github.com/owner/repo", or null for anything else, so we can
// reject typos before they cost a round trip.
export function normalizeRepoUrl(input: string): string | null {
  const match = /^(?:https?:\/\/)?(?:www\.)?github\.com\/([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+?)(?:\.git)?\/?$/.exec(
    input.trim(),
  )
  if (!match) return null
  return `github.com/${match[1]}/${match[2]}`
}

export const STEPS: { status: AnalysisStatus; label: string }[] = [
  { status: 'queued', label: 'Queued' },
  { status: 'cloning', label: 'Cloning' },
  { status: 'analyzing', label: 'Analyzing' },
  { status: 'done', label: 'Done' },
]

// For the progress card: each step is finished, running, or still to come.
// A failed run has no position of its own, so the caller passes the last status it saw.
export function stepState(current: AnalysisStatus, step: AnalysisStatus): 'done' | 'active' | 'todo' {
  const order = STEPS.map((s) => s.status)
  const at = order.indexOf(current)
  const index = order.indexOf(step)
  if (current === 'done' || index < at) return 'done'
  return index === at ? 'active' : 'todo'
}

export function formatElapsed(ms: number): string {
  const seconds = Math.floor(ms / 1000)
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response
  try {
    res = await fetch(path, init)
  } catch {
    // fetch only throws when no HTTP answer came back at all.
    throw new ApiDownError()
  }
  // The Vite proxy answers 502/504 when the API is down, which is the same situation.
  if (res.status === 502 || res.status === 503 || res.status === 504) throw new ApiDownError()
  const body = await res.json().catch(() => ({}))
  if (!res.ok) throw new ApiError(body.error ?? `HTTP ${res.status}`)
  return body as T
}

export const startAnalysis = (repoUrl: string) =>
  request<{ id: string; status: AnalysisStatus }>('/api/analyses', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ repoUrl }),
  })

export const getAnalysis = (id: string) => request<Analysis>(`/api/analyses/${encodeURIComponent(id)}`)

export const getGraph = (id: string) => request<unknown>(`/api/analyses/${encodeURIComponent(id)}/graph`)

export async function apiIsUp(): Promise<boolean> {
  try {
    await request<{ ok: boolean }>('/api/health')
    return true
  } catch {
    return false
  }
}
