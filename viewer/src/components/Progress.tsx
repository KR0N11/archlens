import { useEffect, useState } from 'react'
import { formatElapsed, STEPS, stepState } from '../lib/api'
import type { RunState } from './useAnalysis'

interface Props {
  state: Exclude<RunState, { phase: 'idle' }>
  onCancel: () => void
  onRetry: () => void
}

// The card shown while the server clones and analyzes the repo, or after it failed.
export function Progress({ state, onCancel, onRetry }: Props) {
  const [now, setNow] = useState(() => Date.now())
  const running = state.phase === 'running'

  // Ticks the elapsed clock between polls, so it doesn't jump in 700 ms steps.
  useEffect(() => {
    if (!running) return
    const timer = setInterval(() => setNow(Date.now()), 250)
    return () => clearInterval(timer)
  }, [running])

  const analysis = state.phase === 'running' ? state.analysis : undefined
  const status = analysis?.status ?? 'queued'
  const elapsed = state.phase === 'running' ? now - state.startedAt : 0

  return (
    <div className="landing">
      <div className="progress-card">
        <div className="progress-repo">
          <span className="muted small">{running ? 'Analyzing' : "Couldn't analyze"}</span>
          <strong>{state.repo}</strong>
        </div>

        {state.phase === 'running' ? (
          <>
            <ol className="steps">
              {STEPS.map((s) => (
                <li key={s.status} className={`step ${stepState(status, s.status)}`}>
                  <span className="step-dot" />
                  {s.label}
                </li>
              ))}
            </ol>
            <p className="progress-message">{analysis?.message ?? 'Sending the request…'}</p>
            <p className="muted small">
              {formatElapsed(elapsed)} elapsed{analysis?.cached ? ' · from cache' : ''}
            </p>
            <button onClick={onCancel}>Cancel</button>
          </>
        ) : (
          <>
            <p className={state.apiDown ? 'api-down' : 'form-error'}>{state.error}</p>
            <div className="row">
              <button className="primary" onClick={onRetry}>
                Try again
              </button>
              <button onClick={onCancel}>Back</button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
