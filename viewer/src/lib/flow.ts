import type { Edge, Node } from '@xyflow/react'
import type { Box, LayoutNode } from './layout'
import type { ArchElement, Relationship } from './types'

export interface ElementNodeData extends Record<string, unknown> {
  element: ArchElement
  label: string
  showFields: boolean
  // Only set on components in the Overview, so the node can show a +/- toggle.
  expandable?: boolean
  expanded?: boolean
}

export type ElementNode = Node<ElementNodeData, 'element' | 'frame'>

export const NODE_WIDTH = 220

// Box height grows with the number of fields when fields are shown (Data Entities view).
export function nodeHeight(el: ArchElement, showFields: boolean): number {
  if (showFields && el.type === 'DataObject') return 42 + 18 * Math.max(1, el.fields?.length ?? 0)
  return el.route ? 64 : 52
}

export interface FlowItem {
  id: string
  parent?: string
  isGroup: boolean
  data: ElementNodeData
}

export function toLayoutNodes(items: FlowItem[]): LayoutNode[] {
  return items.map((it) => ({
    id: it.id,
    parent: it.parent,
    width: NODE_WIDTH,
    height: nodeHeight(it.data.element, it.data.showFields),
  }))
}

// React Flow requires a parent node to come before its children in the array,
// otherwise the children are placed as if they had no parent.
export function toFlowNodes(items: FlowItem[], boxes: Map<string, Box>): ElementNode[] {
  const ordered = [...items.filter((i) => !i.parent), ...items.filter((i) => i.parent)]
  return ordered.map((it) => {
    const box = boxes.get(it.id) ?? { x: 0, y: 0, width: NODE_WIDTH, height: 52 }
    return {
      id: it.id,
      type: it.isGroup ? 'frame' : 'element',
      position: { x: box.x, y: box.y },
      parentId: it.parent,
      extent: it.parent ? 'parent' : undefined,
      width: box.width,
      height: box.height,
      style: { width: box.width, height: box.height },
      data: it.data,
    }
  })
}

export interface EdgeLook {
  dash?: string
  color: string
  label?: string
}

// One rule per arrow type, kept in one place so the legend and the canvas agree.
export function edgeLook(r: Pick<Relationship, 'type' | 'confidence' | 'access'>): EdgeLook {
  const guessed = r.confidence < 1
  if (r.type === 'Access') {
    return { dash: '2 4', color: '#7a5c00', label: r.access === 'Write' ? 'write' : 'read' }
  }
  if (r.type === 'Flow') return { dash: '8 4', color: '#1f5fbf', label: guessed ? 'flow ?' : 'flow' }
  if (r.type === 'Aggregation') return { color: '#555', label: 'has many' }
  // A call through an interface with several implementations: one of these is real, not all.
  if (guessed) return { dash: '6 4', color: '#999', label: 'maybe' }
  return { color: '#444' }
}

export function toFlowEdges(relationships: Relationship[]): Edge[] {
  return relationships.map((r) => {
    const look = edgeLook(r)
    return {
      id: r.id,
      source: r.from,
      target: r.to,
      label: look.label,
      style: { stroke: look.color, strokeDasharray: look.dash, strokeWidth: 1.5 },
      markerEnd: { type: 'arrowclosed', color: look.color },
    }
  })
}

export function toCountEdges(edges: { id: string; from: string; to: string; count: number }[]): Edge[] {
  return edges.map((e) => ({
    id: e.id,
    source: e.from,
    target: e.to,
    label: String(e.count),
    style: { stroke: '#444', strokeWidth: 1.5 },
    markerEnd: { type: 'arrowclosed', color: '#444' },
  }))
}
