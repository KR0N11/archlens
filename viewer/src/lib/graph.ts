import type { ArchElement, ArchGraph, Relationship, SourceRef, ViewDef } from './types'

// Checks the file is really a graph before any view tries to draw it, so a wrong file
// shows one clear error instead of a blank canvas.
export function parseGraph(raw: unknown): ArchGraph {
  if (typeof raw !== 'object' || raw === null) throw new Error('File is not a JSON object.')
  const g = raw as Partial<ArchGraph>
  if (!Array.isArray(g.elements) || !Array.isArray(g.relationships)) {
    throw new Error('Not an ArchLens graph: "elements" and "relationships" arrays are required.')
  }
  return {
    repo: g.repo ?? '',
    commit: g.commit,
    analyzedAt: g.analyzedAt,
    // The C# side leaves out a value only when it is null, but confidence 1.0 is its default,
    // so treat a missing confidence as fully confident.
    elements: g.elements.map((e) => ({ ...e, confidence: e.confidence ?? 1 })),
    relationships: g.relationships.map((r) => ({ ...r, confidence: r.confidence ?? 1 })),
    views: g.views ?? {},
    score: g.score ?? { total: 100, checks: [] },
  }
}

export function indexById(graph: ArchGraph): Map<string, ArchElement> {
  return new Map(graph.elements.map((e) => [e.id, e]))
}

// Walks up the parent chain (self included) and returns the first id that passes the test.
// The step cap stops a bad file with a parent loop from freezing the page.
function walkUp(
  id: string,
  byId: Map<string, ArchElement>,
  test: (el: ArchElement) => boolean,
): string | undefined {
  let current = byId.get(id)
  for (let steps = 0; current && steps < 50; steps++) {
    if (test(current)) return current.id
    current = current.parent ? byId.get(current.parent) : undefined
  }
  return undefined
}

export function componentOf(id: string, byId: Map<string, ArchElement>): string | undefined {
  return walkUp(id, byId, (el) => el.type === 'ApplicationComponent')
}

export interface LiftedEdge {
  id: string
  from: string
  to: string
  count: number
}

// Moves every arrow up to the nearest box that is on screen, then merges duplicates into
// one arrow with a count. Arrows that start and end in the same box are dropped.
export function liftEdges(
  relationships: Relationship[],
  mapTo: (id: string) => string | undefined,
): LiftedEdge[] {
  const counts = new Map<string, LiftedEdge>()
  for (const r of relationships) {
    const from = mapTo(r.from)
    const to = mapTo(r.to)
    if (!from || !to || from === to) continue
    const key = `${from}->${to}`
    const existing = counts.get(key)
    if (existing) existing.count++
    else counts.set(key, { id: key, from, to, count: 1 })
  }
  return [...counts.values()].sort((a, b) => a.id.localeCompare(b.id))
}

export interface OverviewNode {
  element: ArchElement
  parent?: string
  expanded: boolean
}

// Components (plus anything with no parent, like external systems) are always shown.
// An expanded component also shows its direct children nested inside it.
export function overviewModel(graph: ArchGraph, expanded: Set<string>) {
  const byId = indexById(graph)
  const nodes: OverviewNode[] = []
  for (const el of graph.elements) {
    if (el.type === 'ApplicationComponent' || !el.parent) {
      nodes.push({ element: el, expanded: expanded.has(el.id) })
    }
  }
  for (const el of graph.elements) {
    if (el.parent && expanded.has(el.parent) && byId.get(el.parent)?.type === 'ApplicationComponent') {
      nodes.push({ element: el, parent: el.parent, expanded: false })
    }
  }
  const visible = new Set(nodes.map((n) => n.element.id))
  const edges = liftEdges(graph.relationships, (id) => walkUp(id, byId, (el) => visible.has(el.id)))
  return { nodes, edges }
}

export type Crud = 'R' | 'W' | 'RW'

// Rows are data objects something reads or writes; columns are the components doing it.
// Untouched entities are left out, or on a real repo the table is mostly empty rows.
export function crudMatrix(graph: ArchGraph) {
  const byId = indexById(graph)
  const cells: Record<string, Record<string, Crud>> = {}
  const usedComponents = new Set<string>()
  for (const r of graph.relationships) {
    if (r.type !== 'Access' || byId.get(r.to)?.type !== 'DataObject') continue
    const cmp = componentOf(r.from, byId)
    if (!cmp) continue
    usedComponents.add(cmp)
    const row = (cells[r.to] ??= {})
    const letter = r.access === 'Write' ? 'W' : 'R'
    const before = row[cmp]
    row[cmp] = !before || before === letter ? letter : 'RW'
  }
  const byName = (a: ArchElement, b: ArchElement) => a.name.localeCompare(b.name)
  const entities = graph.elements.filter((e) => e.type === 'DataObject' && cells[e.id]).sort(byName)
  const components = [...usedComponents].map((id) => byId.get(id)!).sort(byName)
  return { entities, components, cells }
}

// "OrderService.GetOrders" reads better than "GetOrders" when the box is out of context.
export function displayName(el: ArchElement, byId: Map<string, ArchElement>): string {
  const parent = el.parent ? byId.get(el.parent) : undefined
  const isMember = el.type === 'ApplicationFunction' || el.type === 'ApplicationInterface'
  // A minimal API route is named by its route ("POST api/orders"); a class prefix adds nothing.
  if (el.route && el.name === el.route) return el.name
  // The analyzer already names functions "Class.Method"; only add the class when it's missing.
  if (isMember && parent && parent.type !== 'ApplicationComponent' && !el.name.startsWith(`${parent.name}.`))
    return `${parent.name}.${el.name}`
  return el.name
}

export interface CallFlowOption {
  key: string
  entryId: string
  label: string
  size: number
}

// The entry point with the longest traced path: the most interesting one to open on.
export function biggestFlow(options: CallFlowOption[]): CallFlowOption | undefined {
  return options.reduce<CallFlowOption | undefined>((best, o) => (!best || o.size > best.size ? o : best), undefined)
}

export function callFlowViews(graph: ArchGraph): CallFlowOption[] {
  const byId = indexById(graph)
  return Object.keys(graph.views)
    .filter((key) => key.startsWith('callFlow:'))
    .map((key) => {
      const entryId = key.slice('callFlow:'.length)
      const entry = byId.get(entryId)
      const label = entry ? (entry.route ?? displayName(entry, byId)) : entryId
      return { key, entryId, label, size: graph.views[key].elements.length }
    })
    .sort((a, b) => a.label.localeCompare(b.label))
}

// Resolves a view's id lists to real objects. Ids that are missing, and arrows whose
// ends are not both in the view, are dropped so the canvas never points at nothing.
export function viewSubgraph(graph: ArchGraph, view: ViewDef) {
  const byId = indexById(graph)
  const elements = view.elements.map((id) => byId.get(id)).filter((e): e is ArchElement => !!e)
  const ids = new Set(elements.map((e) => e.id))
  const wanted = new Set(view.relationships)
  const relationships = graph.relationships.filter(
    (r) => wanted.has(r.id) && ids.has(r.from) && ids.has(r.to),
  )
  return { elements, relationships }
}

// Below this, a box is drawn dashed ("check me") and unsure entities are hidden by default.
export const LOW_CONFIDENCE = 0.7

// Unsure entities (score 2 to 4, never checked by the LLM) are hidden unless asked for:
// on a real repo they outnumber the confident ones and bury them.
export function dataEntitiesSubgraph(graph: ArchGraph, includeUnsure = false) {
  const elements = graph.elements.filter(
    (e) => e.type === 'DataObject' && (includeUnsure || (e.confidence ?? 1) >= LOW_CONFIDENCE),
  )
  const ids = new Set(elements.map((e) => e.id))
  const relationships = graph.relationships.filter(
    (r) => r.type === 'Aggregation' && ids.has(r.from) && ids.has(r.to),
  )
  return { elements, relationships }
}

const GITHUB_REPO = /^(?:https?:\/\/)?(?:www\.)?github\.com\/([^/]+)\/([^/]+?)(?:\.git)?\/?$/

// The repo page at the analyzed commit (or the default branch when there is no commit).
export function githubRepoUrl(repo: string, commit: string | undefined): string | undefined {
  const match = GITHUB_REPO.exec(repo.trim())
  if (!match) return undefined
  const base = `https://github.com/${match[1]}/${match[2]}`
  return commit ? `${base}/tree/${commit}` : base
}

// Only GitHub repos analyzed at a known commit get a link; a local path has nowhere to point.
export function githubLink(repo: string, commit: string | undefined, source: SourceRef): string | undefined {
  const match = GITHUB_REPO.exec(repo.trim())
  if (!match || !commit) return undefined
  const path = source.file
    .replace(/\\/g, '/')
    .replace(/^\.?\//, '')
    .split('/')
    .map(encodeURIComponent)
    .join('/')
  return `https://github.com/${match[1]}/${match[2]}/blob/${commit}/${path}#L${source.line}`
}

// Exact name matches first, then names that start with the query, then anything containing it.
export function searchElements(graph: ArchGraph, query: string, limit = 20): ArchElement[] {
  const q = query.trim().toLowerCase()
  if (!q) return []
  const rank = (el: ArchElement) => {
    const name = el.name.toLowerCase()
    if (name === q) return 0
    if (name.startsWith(q)) return 1
    if (name.includes(q)) return 2
    if (el.route?.toLowerCase().includes(q)) return 3
    return -1
  }
  return graph.elements
    .map((el) => ({ el, r: rank(el) }))
    .filter((x) => x.r >= 0)
    .sort((a, b) => a.r - b.r || a.el.name.localeCompare(b.el.name))
    .slice(0, limit)
    .map((x) => x.el)
}
