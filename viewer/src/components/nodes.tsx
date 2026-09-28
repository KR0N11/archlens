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

// Below this the rules (or the LLM) were unsure, so the box gets a dashed "check me" border.

export function nodeClass(type: ElementType, confidence: number): string {
  return `node t-${type}${confidence < LOW_CONFIDENCE ? ' guess' : ''}`
}

function ElementNodeView({ data, selected }: NodeProps<ElementNode>) {
  const el = data.element
  const kind = el.entityKind ? ` · ${el.entityKind}` : ''
  return (
    <div className={`${nodeClass(el.type, el.confidence)}${selected ? ' selected' : ''}`}>
      <Handle type="target" position={Position.Top} />
      <div className="node-head">
        {el.type === 'ApplicationInterface' && <span className="iface-dot" />}
        <span className="node-type">{TYPE_LABEL[el.type]}{kind}</span>
        {data.expandable && <span className="node-toggle">+</span>}
      </div>
      <div className="node-label" title={data.label}>{data.label}</div>
      {el.route && <div className="node-route">{el.route}</div>}
      {data.showFields && el.fields && (
        <ul className="node-fields">
          {el.fields.map((f) => (
            <li key={f.name}>
              {f.name}: <span>{f.type}</span>
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
        <span>{data.label}</span>
        <span className="node-toggle">−</span>
      </div>
      <Handle type="source" position={Position.Bottom} />
    </div>
  )
}

// Defined once outside any component: React Flow warns and re-mounts nodes if this object changes.
// Not named 'group': React Flow has a built-in 'group' type with its own border and background.
export const nodeTypes = { element: ElementNodeView, frame: GroupNodeView }
