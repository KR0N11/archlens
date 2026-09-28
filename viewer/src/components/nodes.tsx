import { Handle, Position, type NodeProps } from '@xyflow/react'
import type { ElementNode } from '../lib/flow'
import type { ElementType } from '../lib/types'
import { LOW_CONFIDENCE } from '../lib/graph'

export const TYPE_LABEL: Record<ElementType, string> = {
  BusinessActor: 'Actor',
  ApplicationComponent: 'Component',
  ApplicationInterface: 'Interface',
  ApplicationService: 'Service',
  ApplicationFunction: 'Function',
  ApplicationProcess: 'Process',
  DataObject: 'Data object',
  ExternalSystem: 'External system',
  Node: 'Node',
  TechnologyService: 'Technology service',
}

// Below LOW_CONFIDENCE the rules (or the LLM) were unsure, so the box gets a dashed "check me" border.
export function nodeClass(type: ElementType, confidence: number): string {
  return `node t-${type}${confidence < LOW_CONFIDENCE ? ' guess' : ''}`
}

// Small ArchiMate-style icons, drawn in the box's own accent color (currentColor).
export function TypeGlyph({ type }: { type: ElementType }) {
  const common = { width: 14, height: 14, viewBox: '0 0 16 16', fill: 'none', stroke: 'currentColor', strokeWidth: 1.5 }
  switch (type) {
    case 'ApplicationComponent':
      return (
        <svg {...common}>
          <rect x="4" y="2" width="10" height="12" rx="1" />
          <rect x="2" y="4.5" width="4" height="2.5" fill="var(--surface)" />
          <rect x="2" y="9" width="4" height="2.5" fill="var(--surface)" />
        </svg>
      )
    case 'ApplicationInterface':
      return (
        <svg {...common}>
          <circle cx="10" cy="8" r="4" />
          <line x1="1.5" y1="8" x2="6" y2="8" />
        </svg>
      )
    case 'ApplicationService':
    case 'TechnologyService':
      return (
        <svg {...common}>
          <rect x="1.5" y="4.5" width="13" height="7" rx="3.5" />
        </svg>
      )
    case 'ApplicationFunction':
    case 'ApplicationProcess':
      return (
        <svg {...common}>
          <path d="M3 5 L8 2 L13 5 L13 14 L8 11 L3 14 Z" />
        </svg>
      )
    case 'DataObject':
      return (
        <svg {...common}>
          <rect x="2" y="3" width="12" height="10" rx="1" />
          <line x1="2" y1="6.5" x2="14" y2="6.5" />
        </svg>
      )
    case 'ExternalSystem':
      return (
        <svg {...common} strokeDasharray="2 1.5">
          <rect x="2" y="3" width="12" height="10" rx="1.5" />
        </svg>
      )
    case 'Node':
      return (
        <svg {...common}>
          <path d="M2 5 L5 2 L14 2 L14 11 L11 14 L2 14 Z M2 5 L11 5 L11 14 M11 5 L14 2" />
        </svg>
      )
    case 'BusinessActor':
      return (
        <svg {...common}>
          <circle cx="8" cy="4" r="2.2" />
          <path d="M8 6.5 V11 M4 8.5 H12 M8 11 L5 14.5 M8 11 L11 14.5" />
        </svg>
      )
  }
}

function ElementNodeView({ data, selected }: NodeProps<ElementNode>) {
  const el = data.element
  const kind = el.entityKind ? ` · ${el.entityKind}` : ''
  return (
    <div className={`${nodeClass(el.type, el.confidence)}${selected ? ' selected' : ''}`}>
      <Handle type="target" position={Position.Top} />
      <div className="node-head">
        <span className="node-glyph">
          <TypeGlyph type={el.type} />
        </span>
        <span className="node-type">
          {TYPE_LABEL[el.type]}
          {kind}
        </span>
        {data.expandable && <span className="node-toggle" title="Open">+</span>}
      </div>
      <div className="node-label" title={data.label}>
        {data.label}
      </div>
      {el.route && el.route !== data.label && <div className="node-route">{el.route}</div>}
      {data.showFields && el.fields && el.fields.length > 0 && (
        <ul className="node-fields">
          {el.fields.map((f) => (
            <li key={f.name}>
              {f.name}
              <span>{f.type}</span>
            </li>
          ))}
        </ul>
      )}
      <Handle type="source" position={Position.Bottom} />
    </div>
  )
}

// An expanded component: a titled frame that holds its classes.
function GroupNodeView({ data, selected }: NodeProps<ElementNode>) {
  return (
    <div className={`group-frame${selected ? ' selected' : ''}`}>
      <Handle type="target" position={Position.Top} />
      <div className="group-title">
        <span className="node-glyph">
          <TypeGlyph type="ApplicationComponent" />
        </span>
        <span>{data.label}</span>
        <span className="node-toggle" title="Close">−</span>
      </div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  )
}

// Defined once outside any component: React Flow warns and re-mounts nodes if this object changes.
// Not named 'group': React Flow has a built-in 'group' type with its own border and background.
export const nodeTypes = { element: ElementNodeView, frame: GroupNodeView }
