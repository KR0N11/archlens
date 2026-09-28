import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import {
  callFlowViews,
  componentOf,
  crudMatrix,
  biggestFlow,
  dataEntitiesSubgraph,
  displayName,
  githubLink,
  indexById,
  liftEdges,
  overviewModel,
  parseGraph,
  searchElements,
  viewSubgraph,
} from './graph'
import type { ArchElement, ArchGraph, Relationship } from './types'

const el = (id: string, type: ArchElement['type'], name: string, parent?: string, extra: Partial<ArchElement> = {}): ArchElement => ({
  id, type, name, parent, layer: 'Application', confidence: 1, origin: 'Rule', ...extra,
})
let n = 0
const rel = (type: Relationship['type'], from: string, to: string, extra: Partial<Relationship> = {}): Relationship => ({
  id: `rel:${++n}`, type, from, to, evidence: { file: 'a.cs', line: 1 }, confidence: 1, origin: 'Static', ...extra,
})

// Two components (Api, Data), an external system, and calls that cross them.
function tinyGraph(): ArchGraph {
  n = 0
  return {
    repo: 'github.com/owner/shop',
    commit: 'abc123',
    elements: [
      el('cmp:Api', 'ApplicationComponent', 'Api'),
      el('cmp:Data', 'ApplicationComponent', 'Data'),
      el('cls:Api.OrdersController', 'ApplicationInterface', 'OrdersController', 'cmp:Api'),
      el('api:GET orders', 'ApplicationInterface', 'Get', 'cls:Api.OrdersController', { route: 'GET orders' }),
      el('cls:Api.OrderService', 'ApplicationService', 'OrderService', 'cmp:Api'),
      el('fn:Api.OrderService.Load', 'ApplicationFunction', 'Load', 'cls:Api.OrderService'),
      el('fn:Api.OrderService.Save', 'ApplicationFunction', 'Save', 'cls:Api.OrderService'),
      el('cls:Data.Repo', 'ApplicationService', 'Repo', 'cmp:Data'),
      el('fn:Data.Repo.Get', 'ApplicationFunction', 'Get', 'cls:Data.Repo'),
      el('do:Data.Order', 'DataObject', 'Order', 'cmp:Data', { fields: [{ name: 'Id', type: 'int' }] }),
      el('do:Data.Line', 'DataObject', 'Line', 'cmp:Data'),
      el('ext:pay.example.com', 'ExternalSystem', 'pay.example.com'),
    ],
    relationships: [
      rel('Serving', 'api:GET orders', 'fn:Api.OrderService.Load'),
      rel('Serving', 'fn:Api.OrderService.Load', 'fn:Data.Repo.Get'),
      rel('Serving', 'fn:Api.OrderService.Save', 'fn:Data.Repo.Get'),
      rel('Access', 'fn:Data.Repo.Get', 'do:Data.Order', { access: 'Read' }),
      rel('Access', 'fn:Api.OrderService.Save', 'do:Data.Order', { access: 'Write' }),
      rel('Access', 'fn:Api.OrderService.Load', 'do:Data.Order', { access: 'Read' }),
      rel('Flow', 'fn:Api.OrderService.Save', 'ext:pay.example.com'),
      rel('Aggregation', 'do:Data.Order', 'do:Data.Line'),
    ],
    views: {
      'callFlow:api:GET orders': {
        elements: ['api:GET orders', 'fn:Api.OrderService.Load', 'fn:Data.Repo.Get', 'missing:id'],
        relationships: ['rel:1', 'rel:2', 'rel:3'],
      },
      'overview-something': { elements: [], relationships: [] },
    },
    score: { total: 100, checks: [] },
  }
}

describe('parseGraph', () => {
  // A file without the two arrays is rejected with a message instead of drawing nothing.
  it('rejects JSON that is not a graph', () => {
    expect(() => parseGraph({ hello: 1 })).toThrow(/elements/)
    expect(() => parseGraph(null)).toThrow()
  })

  // The C# side omits confidence only when it is the default, so missing means 1.
  it('fills defaults for optional parts', () => {
    const g = parseGraph({ elements: [{ id: 'a', type: 'Node', name: 'a' }], relationships: [] })
    expect(g.elements[0].confidence).toBe(1)
    expect(g.views).toEqual({})
    expect(g.score.checks).toEqual([])
  })

  // The bundled example must always load, since it is what a first-time visitor sees.
  it('accepts the bundled sample', () => {
    const raw = JSON.parse(readFileSync('public/examples/sample.json', 'utf8'))
    const g = parseGraph(raw)
    expect(g.elements.length).toBeGreaterThan(10)
    expect(callFlowViews(g).length).toBeGreaterThanOrEqual(2)
  })
})

describe('componentOf', () => {
  // A function belongs to the component two levels up (function -> class -> component).
  it('walks the parent chain', () => {
    const byId = indexById(tinyGraph())
    expect(componentOf('fn:Data.Repo.Get', byId)).toBe('cmp:Data')
    expect(componentOf('ext:pay.example.com', byId)).toBeUndefined()
  })

  // A broken file with a parent loop must not hang the page.
  it('stops on a parent loop', () => {
    const byId = new Map([
      ['a', el('a', 'ApplicationService', 'a', 'b')],
      ['b', el('b', 'ApplicationService', 'b', 'a')],
    ])
    expect(componentOf('a', byId)).toBeUndefined()
  })
})

describe('liftEdges', () => {
  // Two calls (Load->Get, Save->Get) plus two data accesses (Load reads Order, Save writes it)
  // all go from Api to Data, so they become one arrow labeled 4.
  it('merges arrows between the same two boxes and counts them', () => {
    const g = tinyGraph()
    const byId = indexById(g)
    const edges = liftEdges(g.relationships, (id) => componentOf(id, byId) ?? id)
    const apiToData = edges.find((e) => e.from === 'cmp:Api' && e.to === 'cmp:Data')
    expect(apiToData?.count).toBe(4)
  })

  // Arrows inside one component would be a loop on the same box, so they are dropped.
  it('drops arrows that start and end in the same box', () => {
    const g = tinyGraph()
    const byId = indexById(g)
    const edges = liftEdges(g.relationships, (id) => componentOf(id, byId) ?? id)
    expect(edges.every((e) => e.from !== e.to)).toBe(true)
  })

  // An arrow whose end is not on screen at all is skipped rather than drawn to nowhere.
  it('skips arrows whose end has no visible box', () => {
    const g = tinyGraph()
    const edges = liftEdges(g.relationships, (id) => (id.startsWith('fn:') ? id : undefined))
    expect(edges.map((e) => e.id)).toEqual(['fn:Api.OrderService.Load->fn:Data.Repo.Get', 'fn:Api.OrderService.Save->fn:Data.Repo.Get'])
  })
})

describe('overviewModel', () => {
  // Collapsed: only components and parentless boxes (the external system) are shown.
  it('shows components and external systems when nothing is expanded', () => {
    const { nodes, edges } = overviewModel(tinyGraph(), new Set())
    expect(nodes.map((x) => x.element.id).sort()).toEqual(['cmp:Api', 'cmp:Data', 'ext:pay.example.com'])
    expect(edges.map((e) => e.id).sort()).toEqual(['cmp:Api->cmp:Data', 'cmp:Api->ext:pay.example.com'])
  })

  // Expanding Api shows its classes inside it, and arrows now start at those classes.
  it('nests children of an expanded component and re-routes arrows to them', () => {
    const { nodes, edges } = overviewModel(tinyGraph(), new Set(['cmp:Api']))
    const children = nodes.filter((x) => x.parent === 'cmp:Api').map((x) => x.element.id).sort()
    expect(children).toEqual(['cls:Api.OrderService', 'cls:Api.OrdersController'])
    const ids = edges.map((e) => e.id)
    expect(ids).toContain('cls:Api.OrdersController->cls:Api.OrderService')
    expect(ids).toContain('cls:Api.OrderService->cmp:Data')
    expect(ids).not.toContain('cmp:Api->cmp:Data')
  })
})

describe('crudMatrix', () => {
  // Api both reads and writes Order, Data only reads it; Line is never touched.
  it('combines reads and writes per component', () => {
    const { entities, components, cells } = crudMatrix(tinyGraph())
    expect(entities.map((e) => e.name)).toEqual(['Order'])
    expect(components.map((c) => c.name)).toEqual(['Api', 'Data'])
    expect(cells['do:Data.Order']).toEqual({ 'cmp:Api': 'RW', 'cmp:Data': 'R' })
    expect(cells['do:Data.Line']).toBeUndefined()
  })
})

describe('call flow views', () => {
  // The dropdown shows the route, not the raw id.
  it('labels entries by route', () => {
    expect(callFlowViews(tinyGraph())).toEqual([
      { key: 'callFlow:api:GET orders', entryId: 'api:GET orders', label: 'GET orders', size: 4 },
    ])
  })

  // Unknown ids and arrows to boxes outside the view are dropped.
  it('resolves a view to elements and arrows that exist', () => {
    const g = tinyGraph()
    const sub = viewSubgraph(g, g.views['callFlow:api:GET orders'])
    expect(sub.elements.map((e) => e.id)).toEqual(['api:GET orders', 'fn:Api.OrderService.Load', 'fn:Data.Repo.Get'])
    expect(sub.relationships.map((r) => r.id)).toEqual(['rel:1', 'rel:2'])
  })

  // Data view keeps only data objects and the "has many" arrows between them.
  it('builds the data entities subgraph', () => {
    const sub = dataEntitiesSubgraph(tinyGraph())
    expect(sub.elements.map((e) => e.id)).toEqual(['do:Data.Order', 'do:Data.Line'])
    expect(sub.relationships.map((r) => r.type)).toEqual(['Aggregation'])
  })

  // An unsure entity (confidence 0.5) is hidden until the user asks for it.
  it('hides unsure entities unless asked', () => {
    const graph = tinyGraph()
    graph.elements.find((e) => e.id === 'do:Data.Line')!.confidence = 0.5
    expect(dataEntitiesSubgraph(graph).elements.map((e) => e.id)).toEqual(['do:Data.Order'])
    expect(dataEntitiesSubgraph(graph).relationships).toEqual([])
    expect(dataEntitiesSubgraph(graph, true).elements.map((e) => e.id)).toEqual(['do:Data.Order', 'do:Data.Line'])
  })
})

describe('biggestFlow', () => {
  // The call flow tab opens on the entry point whose traced path has the most boxes.
  it('picks the longest flow', () => {
    const options = [
      { key: 'a', entryId: 'a', label: 'a', size: 2 },
      { key: 'b', entryId: 'b', label: 'b', size: 7 },
      { key: 'c', entryId: 'c', label: 'c', size: 3 },
    ]
    expect(biggestFlow(options)?.key).toBe('b')
    expect(biggestFlow([])).toBeUndefined()
  })
})

describe('displayName', () => {
  // A method shows its class so "Get" is not ambiguous; a class shows its own name.
  it('prefixes members with their class', () => {
    const byId = indexById(tinyGraph())
    expect(displayName(byId.get('fn:Data.Repo.Get')!, byId)).toBe('Repo.Get')
    expect(displayName(byId.get('cls:Data.Repo')!, byId)).toBe('Repo')
    const named = { ...byId.get('fn:Data.Repo.Get')!, name: 'Repo.Get' }
    expect(displayName(named, byId)).toBe('Repo.Get')
  })
})

describe('githubLink', () => {
  // Repo in either form, with or without https and .git, points at the exact commit and line.
  it('builds a blob link for GitHub repos', () => {
    const src = { file: 'src/Orders Api/Order.cs', line: 42 }
    const want = 'https://github.com/owner/shop/blob/abc123/src/Orders%20Api/Order.cs#L42'
    expect(githubLink('github.com/owner/shop', 'abc123', src)).toBe(want)
    expect(githubLink('https://github.com/owner/shop.git', 'abc123', src)).toBe(want)
  })

  // A local path or a missing commit has no stable URL, so no link is shown.
  it('returns nothing when there is no GitHub repo or commit', () => {
    const src = { file: 'a.cs', line: 1 }
    expect(githubLink('samples/SampleShop', 'abc', src)).toBeUndefined()
    expect(githubLink('github.com/owner/shop', undefined, src)).toBeUndefined()
  })
})

describe('searchElements', () => {
  // Exact name first, then prefix matches; empty query returns nothing.
  it('ranks exact matches before partial ones', () => {
    const names = searchElements(tinyGraph(), 'order').map((e) => e.name)
    expect(names[0]).toBe('Order')
    expect(names).toContain('OrderService')
    expect(searchElements(tinyGraph(), '  ')).toEqual([])
  })
})
