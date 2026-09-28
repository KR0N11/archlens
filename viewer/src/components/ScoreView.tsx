import { useMemo } from 'react'
import { displayName, indexById } from '../lib/graph'
import type { ArchGraph } from '../lib/types'

interface Props {
  graph: ArchGraph
  onSelect: (id: string) => void
}

// The total, then every finding with the boxes that caused it. The analyzer computed all of this.
export function ScoreView({ graph, onSelect }: Props) {
  const byId = useMemo(() => indexById(graph), [graph])
  const findings = [...graph.score.checks].sort((a, b) => b.penalty - a.penalty)

  return (
    <div className="score">
      <div className="score-total">
        <span className="score-number">{graph.score.total}</span>
        <span className="muted"> / 100</span>
      </div>
      {findings.length === 0 && <p className="muted">No findings.</p>}
      <ul className="findings">
        {findings.map((f, i) => (
          <li key={`${f.check}-${i}`}>
            <div>
              <strong>{f.check}</strong> <span className="penalty">−{f.penalty}</span>
            </div>
            <div>{f.message}</div>
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
    </div>
  )
}
