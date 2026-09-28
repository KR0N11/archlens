// Mirrors src/ArchLens.Core/Model/*.cs. The C# side decides what is true; the viewer only draws it.

export type ElementType =
  | 'BusinessActor'
  | 'ApplicationComponent'
  | 'ApplicationInterface'
  | 'ApplicationService'
  | 'ApplicationFunction'
  | 'ApplicationProcess'
  | 'DataObject'
  | 'ExternalSystem'
  | 'Node'
  | 'TechnologyService'

export type RelationshipType =
  | 'Composition'
  | 'Aggregation'
  | 'Assignment'
  | 'Realization'
  | 'Serving'
  | 'Access'
  | 'Flow'
  | 'Triggering'

export interface SourceRef {
  file: string
  line: number
}

export interface Field {
  name: string
  type: string
}

export interface ArchElement {
  id: string
  type: ElementType
  layer: 'Business' | 'Application' | 'Technology'
  name: string
  parent?: string
  source?: SourceRef
  route?: string
  purpose?: string
  confidence: number
  origin: 'Rule' | 'Static' | 'Llm' | 'User'
  entityKind?: 'Stored' | 'Dto' | 'External'
  entityScore?: number
  // Why the entity score is what it is, e.g. "ORM mapping +5".
  entitySignals?: string[]
  fields?: Field[]
}

export interface Relationship {
  id: string
  type: RelationshipType
  from: string
  to: string
  evidence: SourceRef
  access?: 'Read' | 'Write'
  confidence: number
  origin: 'Rule' | 'Static' | 'Llm' | 'User'
}

export interface ViewDef {
  elements: string[]
  relationships: string[]
}

export interface Finding {
  check: string
  message: string
  penalty: number
  elements: string[]
  relationships: string[]
}

export interface ArchGraph {
  repo: string
  commit?: string
  analyzedAt?: string
  elements: ArchElement[]
  relationships: Relationship[]
  views: Record<string, ViewDef>
  score: { total: number; checks: Finding[] }
}
