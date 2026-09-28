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

export const NODE_WIDTH = 240

// Box height grows with the number of fields when fields are shown (Data Entities view).
export function nodeHeight(el: ArchElement, showFields: boolean): number {
  if (showFields && el.type === 'DataObject') return 62 + 19 * Math.max(1, el.fields?.length ?? 0)
  return el.route ? 76 : 62
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
    const box = boxes.get(it.id) ?? { x: 0, y: 0, width: NODE_WIDTH, height: 62 }
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

// Arrow colors, one per relationship type. Plain hex (not CSS variables) because React Flow
// copies the color into an SVG <marker> for the arrowhead.
export const EDGE_COLORS = {
  serving: '#475569',
  maybe: '#94a3b8',
  read: '#0d9488',
  write: '#ea580c',
  flow: '#2563eb',
  aggregation: '#7c3aed',
}

// One rule per arrow type, kept in one place so the legend and the canvas agree.
export function edgeLook(r: Pick<Relationship, 'type' | 'confidence' | 'access'>): EdgeLook {
  const guessed = r.confidence < 1
  if (r.type === 'Access') {
    return r.access === 'Write'
      ? { dash: '2 4', color: EDGE_COLORS.write, label: 'write' }
      : { dash: '2 4', color: EDGE_COLORS.read, label: 'read' }
  }
  if (r.type === 'Flow') return { dash: '8 4', color: EDGE_COLORS.flow, label: guessed ? 'http ?' : 'http' }
  if (r.type === 'Aggregation') return { color: EDGE_COLORS.aggregation, label: 'has many' }
  // A call through an interface with several implementations: one of these is real, not all.
  if (guessed) return { dash: '6 4', color: EDGE_COLORS.maybe, label: 'maybe' }
  return { color: EDGE_COLORS.serving }
}

// Labels sit on a small pill so they stay readable where arrows cross.
const LABEL_PROPS = {
  labelStyle: { fontSize: 11, fontWeight: 500, fill: 'var(--text-muted)' },
  labelBgStyle: { fill: 'var(--surface)' },
  labelBgPadding: [4, 2] as [number, number],
  labelBgBorderRadius: 4,
}

export function toFlowEdges(relationships: Relationship[]): Edge[] {
  return relationships.map((r) => {
    const look = edgeLook(r)
    return {
      id: r.id,
      source: r.from,
      target: r.to,
      type: 'smoothstep',
      label: look.label,
      ...LABEL_PROPS,
      style: { stroke: look.color, strokeDasharray: look.dash, strokeWidth: 1.6 },
      markerEnd: { type: 'arrowclosed', color: look.color, width: 14, height: 14 },
    }
  })
}

const countWidth = (count: number) => Math.min(1.4 + Math.log2(count) * 0.4, 3)

export function toCountEdges(edges: { id: string; from: string; to: string; count: number }[]): Edge[] {
  return edges.map((e) => ({
    id: e.id,
    source: e.from,
    target: e.to,
    type: 'smoothstep',
    label: String(e.count),
    ...LABEL_PROPS,
    // Thicker line for more calls. Arrowheads are sized in stroke widths, so they shrink as the line grows.
    style: { stroke: EDGE_COLORS.serving, strokeWidth: countWidth(e.count) },
    markerEnd: { type: 'arrowclosed', color: EDGE_COLORS.serving, width: 20 / countWidth(e.count), height: 20 / countWidth(e.count) },
  }))
}
