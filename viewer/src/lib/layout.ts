import ELK from 'elkjs/lib/elk.bundled.js'
import type { ElkExtendedEdge, ElkNode } from 'elkjs/lib/elk-api'

export interface LayoutNode {
  id: string
  width: number
  height: number
  parent?: string
}

export interface LayoutEdge {
  id: string
  source: string
  target: string
}

export interface Box {
  x: number
  y: number
  width: number
  height: number
}

const elk = new ELK()

// Space inside an expanded box: extra room on top for its title.
const GROUP_PADDING = '[top=40,left=16,bottom=16,right=16]'

// Runs ELK's layered algorithm and returns each node's box. Child positions are relative to
// their parent box, which is also what React Flow expects for nodes with a parentId.
export async function layoutGraph(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  direction: 'DOWN' | 'RIGHT',
): Promise<Map<string, Box>> {
  const elkNodes = new Map<string, ElkNode>()
  for (const n of nodes) elkNodes.set(n.id, { id: n.id, width: n.width, height: n.height, children: [] })

  const roots: ElkNode[] = []
  for (const n of nodes) {
    const self = elkNodes.get(n.id)!
    const parent = n.parent ? elkNodes.get(n.parent) : undefined
    if (parent) parent.children!.push(self)
    else roots.push(self)
  }

  // A box that holds children must let ELK size it, otherwise children spill out of it.
  for (const node of elkNodes.values()) {
    if (node.children!.length > 0) {
      delete node.width
      delete node.height
      node.layoutOptions = { 'elk.padding': GROUP_PADDING }
    }
  }

  const known = new Set(nodes.map((n) => n.id))
  const elkEdges: ElkExtendedEdge[] = edges
    .filter((e) => known.has(e.source) && known.has(e.target))
    .map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] }))

  const nested = nodes.some((n) => n.parent)
  const result = await elk.layout({
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': direction,
      'elk.layered.spacing.nodeNodeBetweenLayers': '64',
      'elk.spacing.nodeNode': '32',
      'elk.spacing.componentComponent': '48',
      // INCLUDE_CHILDREN lets one pass route arrows into nested boxes, but it also stops ELK from
      // wrapping unconnected boxes into rows. So it is only on when something is actually nested;
      // otherwise the aspect ratio wraps a 19-project repo into a block instead of one long line.
      ...(nested ? { 'elk.hierarchyHandling': 'INCLUDE_CHILDREN' } : { 'elk.aspectRatio': '1.6' }),
    },
    children: roots,
    edges: elkEdges,
  })

  const boxes = new Map<string, Box>()
  const collect = (list: ElkNode[] | undefined) => {
    for (const n of list ?? []) {
      boxes.set(n.id, { x: n.x ?? 0, y: n.y ?? 0, width: n.width ?? 0, height: n.height ?? 0 })
      collect(n.children)
    }
  }
  collect(result.children)
  return boxes
}
