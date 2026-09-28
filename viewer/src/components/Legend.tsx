import { edgeLook } from '../lib/flow'
import type { ElementType, Relationship } from '../lib/types'
import { nodeClass, TYPE_LABEL } from './nodes'

const BOXES: ElementType[] = [
  'ApplicationComponent',
  'ApplicationInterface',
  'ApplicationService',
  'ApplicationFunction',
  'DataObject',
  'ExternalSystem',
  'TechnologyService',
]

const ARROWS: { text: string; r: Pick<Relationship, 'type' | 'confidence' | 'access'> }[] = [
  { text: 'calls', r: { type: 'Serving', confidence: 1 } },
  { text: 'maybe calls (one of several implementations)', r: { type: 'Serving', confidence: 0.5 } },
  { text: 'reads / writes data', r: { type: 'Access', confidence: 1 } },
  { text: 'sends to an external system', r: { type: 'Flow', confidence: 1 } },
  { text: 'has many', r: { type: 'Aggregation', confidence: 1 } },
]

// Uses the same class names and edge rules as the canvas, so the legend cannot drift from it.
export function Legend() {
  return (
    <details className="legend">
      <summary>Legend</summary>
      {BOXES.map((t) => (
        <div key={t} className="legend-row">
          <span className={`${nodeClass(t, 1)} swatch`} />
          {TYPE_LABEL[t]}
        </div>
      ))}
      <div className="legend-row">
        <span className={`${nodeClass('ApplicationService', 0.5)} swatch`} />
        low confidence, check it
      </div>
      {ARROWS.map(({ text, r }) => {
        const look = edgeLook(r)
        return (
          <div key={text} className="legend-row">
            <svg width="36" height="10">
              <line x1="0" y1="5" x2="36" y2="5" stroke={look.color} strokeWidth="2" strokeDasharray={look.dash} />
            </svg>
            {text}
          </div>
        )
      })}
    </details>
  )
}
