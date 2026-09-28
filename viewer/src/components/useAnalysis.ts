import { useCallback, useRef, useState } from 'react'
import { ApiDownError, getAnalysis, getGraph, startAnalysis, type Analysis } from '../lib/api'

const POLL_MS = 700

export type RunState =
  | { phase: 'idle' }
  | { phase: 'running'; repo: string; analysis?: Analysis; startedAt: number }
  | { phase: 'failed'; repo: string; error: string; apiDown: boolean }

// Starts an analysis, polls its status, and hands the finished graph to onGraph.
// Cancel just stops polling; the server keeps (and caches) whatever it was doing.
export function useAnalysis(onGraph: (raw: unknown) => void) {
  const [state, setState] = useState<RunState>({ phase: 'idle' })
  // Bumped on every start and cancel. A poll loop that sees a newer run number stops,
  // so a cancelled run can never overwrite the screen of a newer one.
  const runId = useRef(0)

  const cancel = useCallback(() => {
    runId.current++
    setState({ phase: 'idle' })
  }, [])

  const start = useCallback(
    async (repo: string) => {
      const run = ++runId.current
      const startedAt = Date.now()
      setState({ phase: 'running', repo, startedAt })
      try {
        const { id } = await startAnalysis(repo)
        while (runId.current === run) {
          const analysis = await getAnalysis(id)
          if (runId.current !== run) return
          setState({ phase: 'running', repo, analysis, startedAt })
          if (analysis.status === 'failed') throw new Error(analysis.error ?? 'The analysis failed.')
          if (analysis.status === 'done') {
            const graph = await getGraph(id)
            if (runId.current !== run) return
            setState({ phase: 'idle' })
            onGraph(graph)
            return
          }
          await new Promise((resolve) => setTimeout(resolve, POLL_MS))
        }
      } catch (e) {
        if (runId.current !== run) return
        setState({
          phase: 'failed',
          repo,
          error: e instanceof Error ? e.message : String(e),
          apiDown: e instanceof ApiDownError,
        })
      }
    },
    [onGraph],
  )

  return { state, start, cancel }
}
