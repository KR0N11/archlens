import { useMemo } from 'react'
import { displayName, indexById } from '../lib/graph'
import { groupFindings, scoreBand } from '../lib/score'
import type { ArchGraph } from '../lib/types'

interface Props {
  graph: ArchGraph
  onSelect: (id: string) => void
}

const RING_RADIUS = 52
const RING_LENGTH = 2 * Math.PI * RING_RADIUS

// The total as a ring, then findings grouped by check. The analyzer computed all of this;
// this view only arranges it.
export function ScoreView({ graph, onSelect }: Props) {
  const byId = useMemo(() => indexById(graph), [graph])
  const groups = useMemo(() => groupFindings(graph.score.checks), [graph])
  const total = graph.score.total
  const band = scoreBand(total)

  return (
    <div className="score">
      <div className="score-hero">
        <svg className={`score-ring ${band}`} width="132" height="132" viewBox="0 0 132 132">
          <circle cx="66" cy="66" r={RING_RADIUS} className="ring-track" />
          <circle
            cx="66"
            cy="66"
            r={RING_RADIUS}
            className="ring-value"
            strokeDasharray={`${(total / 100) * RING_LENGTH} ${RING_LENGTH}`}
            transform="rotate(-90 66 66)"
          />
          <text x="66" y="64" className="ring-number">
            {total}
          </text>
          <text x="66" y="86" className="ring-sub">
            / 100
          </text>
        </svg>
        <div>
          <h2>Architecture score</h2>
          <p className="muted">
            100 minus penalties from fixed graph checks. Each check is capped, so one kind of problem can't sink the
            score on its own. Click a box name to open it.
          </p>
          <div className="chips">
            {groups.map((g) => (
              <span key={g.check} className="penalty-chip">
                {g.check} <b>−{g.penalty}</b>
              </span>
            ))}
          </div>
        </div>
      </div>

      {groups.length === 0 && <p className="muted">No findings.</p>}
      {groups.map((g) => (
        <section key={g.check} className="finding-group">
          <h3>
            {g.check}
            <span className="muted small">
              {' '}
              {g.findings.length} finding{g.findings.length === 1 ? '' : 's'}
            </span>
            <span className="penalty-chip">−{g.penalty}</span>
          </h3>
          <ul className="findings">
            {g.findings.map((f, i) => (
              <li key={`${f.check}-${i}`}>
                <div className="finding-message">
                  {f.message}
                  {/* Past a check's cap, a finding is still listed but costs nothing. */}
                  <span className={f.penalty > 0 ? 'penalty' : 'muted small'}>{f.penalty > 0 ? `−${f.penalty}` : 'capped'}</span>
                </div>
                <div className="chips">
                  {f.elements.map((id) => {
                    const el = byId.get(id)
                    return (
                      <button key={id} className="chip" onClick={() => onSelect(id)} disabled={!el}>
                        {el ? displayName(el, byId) : id}
                      </button>
                    )
                  })}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  )
}
