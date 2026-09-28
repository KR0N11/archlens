import { describe, expect, it } from 'vitest'
import { edgeLook, nodeHeight, toCountEdges, toFlowEdges, toFlowNodes, type FlowItem } from './flow'
import { layoutGraph } from './layout'
import type { ArchElement } from './types'

const el = (id: string, type: ArchElement['type'], extra: Partial<ArchElement> = {}): ArchElement => ({
  id, type, name: id, layer: 'Application', confidence: 1, origin: 'Rule', ...extra,
})
const item = (id: string, parent?: string, isGroup = false): FlowItem => ({
  id, parent, isGroup, data: { element: el(id, 'ApplicationService'), label: id, showFields: false },
})

describe('edgeLook', () => {
  // Access arrows say whether they read or write, so the CRUD story is visible on the canvas.
  it('labels access arrows read or write', () => {
    expect(edgeLook({ type: 'Access', confidence: 1, access: 'Write' }).label).toBe('write')
    expect(edgeLook({ type: 'Access', confidence: 1 }).label).toBe('read')
  })

  // A call that might go to one of several implementations is dashed and grey, a sure call is solid.
  it('dashes guessed calls only', () => {
    expect(edgeLook({ type: 'Serving', confidence: 0.5 }).dash).toBeDefined()
    expect(edgeLook({ type: 'Serving', confidence: 1 }).dash).toBeUndefined()
  })
})

describe('toFlowEdges / toCountEdges', () => {
  // Every relationship becomes exactly one canvas edge with the same id and ends.
  it('keeps ids and ends', () => {
    const edges = toFlowEdges([
      { id: 'rel:1', type: 'Serving', from: 'a', to: 'b', evidence: { file: 'x', line: 1 }, confidence: 1, origin: 'Static' },
    ])
    expect(edges).toMatchObject([{ id: 'rel:1', source: 'a', target: 'b' }])
  })

  // Lifted edges show their count as the label.
  it('labels lifted edges with the count', () => {
    expect(toCountEdges([{ id: 'x', from: 'a', to: 'b', count: 7 }])[0].label).toBe('7')
  })
})

describe('nodeHeight', () => {
  // Data objects grow with their field list in the entities view only.
  it('grows with fields when fields are shown', () => {
    const d = el('d', 'DataObject', { fields: [{ name: 'a', type: 'int' }, { name: 'b', type: 'int' }, { name: 'c', type: 'int' }] })
    expect(nodeHeight(d, true)).toBeGreaterThan(nodeHeight(d, false))
  })
})

describe('toFlowNodes', () => {
  // React Flow misplaces a child listed before its parent, so parents must come first.
  it('puts parents before children', () => {
    const nodes = toFlowNodes([item('child', 'group'), item('group', undefined, true)], new Map())
    expect(nodes.map((x) => x.id)).toEqual(['group', 'child'])
    expect(nodes[1]).toMatchObject({ parentId: 'group', extent: 'parent' })
    expect(nodes[0].type).toBe('frame')
  })
})

describe('layoutGraph', () => {
  // Top-down layout: the callee sits below the caller.
  it('places the target below the source when direction is DOWN', async () => {
    const boxes = await layoutGraph(
      [{ id: 'a', width: 100, height: 40 }, { id: 'b', width: 100, height: 40 }],
      [{ id: 'e', source: 'a', target: 'b' }],
      'DOWN',
    )
    expect(boxes.get('b')!.y).toBeGreaterThan(boxes.get('a')!.y)
  })

  // A group is sized by ELK to hold its children, and children fit inside it.
  it('sizes a group around its children', async () => {
    const boxes = await layoutGraph(
      [
        { id: 'g', width: 0, height: 0 },
        { id: 'c1', width: 100, height: 40, parent: 'g' },
        { id: 'c2', width: 100, height: 40, parent: 'g' },
        { id: 'other', width: 100, height: 40 },
      ],
      [{ id: 'e1', source: 'c1', target: 'c2' }, { id: 'e2', source: 'c2', target: 'other' }],
      'DOWN',
    )
    const g = boxes.get('g')!
    for (const id of ['c1', 'c2']) {
      const c = boxes.get(id)!
      expect(c.x + c.width).toBeLessThanOrEqual(g.width)
      expect(c.y + c.height).toBeLessThanOrEqual(g.height)
    }
  })

  // An edge pointing at an unknown box is ignored instead of crashing ELK.
  it('ignores edges to unknown nodes', async () => {
    const boxes = await layoutGraph([{ id: 'a', width: 10, height: 10 }], [{ id: 'e', source: 'a', target: 'zzz' }], 'RIGHT')
    expect(boxes.has('a')).toBe(true)
  })
})
