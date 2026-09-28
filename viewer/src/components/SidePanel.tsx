import { useMemo } from 'react'
import { displayName, githubLink, indexById } from '../lib/graph'
import type { ArchGraph, Relationship } from '../lib/types'
import { TypeGlyph, TYPE_LABEL } from './nodes'

interface Props {
  graph: ArchGraph
  id: string
  onSelect: (id: string) => void
  onClose: () => void
}

const MAX_LINKS = 25

// Everything we know about one box, plus its arrows with the file and line that prove each one.
export function SidePanel({ graph, id, onSelect, onClose }: Props) {
  const byId = useMemo(() => indexById(graph), [graph])
  const el = byId.get(id)
  if (!el) return null

  const outgoing = graph.relationships.filter((r) => r.from === id)
  const incoming = graph.relationships.filter((r) => r.to === id)
  const link = el.source ? githubLink(graph.repo, graph.commit, el.source) : undefined
  const parent = el.parent ? byId.get(el.parent) : undefined

  const arrowList = (title: string, list: Relationship[], other: (r: Relationship) => string) =>
    list.length > 0 && (
      <>
        <h4>
          {title} ({list.length})
        </h4>
        <ul className="arrows">
          {list.slice(0, MAX_LINKS).map((r) => {
            const target = byId.get(other(r))
            const evidence = githubLink(graph.repo, graph.commit, r.evidence)
            return (
              <li key={r.id}>
                <span className="muted">{r.type}{r.access ? ` ${r.access.toLowerCase()}` : ''}{r.confidence < 1 ? ' (maybe)' : ''}</span>{' '}
                <button className="link" onClick={() => onSelect(other(r))}>
                  {target ? displayName(target, byId) : other(r)}
                </button>
                <div className="evidence">
                  {evidence ? (
                    <a href={evidence} target="_blank" rel="noreferrer">
                      {r.evidence.file}:{r.evidence.line}
                    </a>
                  ) : (
                    `${r.evidence.file}:${r.evidence.line}`
                  )}
                </div>
              </li>
            )
          })}
          {list.length > MAX_LINKS && <li className="muted">and {list.length - MAX_LINKS} more</li>}
        </ul>
      </>
    )

  return (
    <aside className="panel">
      <button className="close" onClick={onClose} aria-label="Close">
        ×
      </button>
      <div className={`panel-kind t-${el.type}`}>
        <TypeGlyph type={el.type} />
        {TYPE_LABEL[el.type]} · {el.layer}
      </div>
      <h2>{displayName(el, byId)}</h2>
      {el.route && <div className="route">{el.route}</div>}
      {el.purpose && <p className="purpose">{el.purpose}</p>}
      <dl>
        <dt>Confidence</dt>
        <dd>
          {Math.round(el.confidence * 100)}% ({el.origin === 'Llm' ? 'LLM' : el.origin.toLowerCase()})
        </dd>
        {parent && (
          <>
            <dt>Inside</dt>
            <dd>
              <button className="link" onClick={() => onSelect(parent.id)}>
                {displayName(parent, byId)}
              </button>
            </dd>
          </>
        )}
        {el.entityKind && (
          <>
            <dt>Entity</dt>
            <dd>
              {el.entityKind}
              {el.entityScore !== undefined && `, score ${el.entityScore}`}
            </dd>
          </>
        )}
        {el.source && (
          <>
            <dt>Source</dt>
            <dd>
              {link ? (
                <a className="mono" href={link} target="_blank" rel="noreferrer">
                  {el.source.file}:{el.source.line}
                </a>
              ) : (
                `${el.source.file}:${el.source.line}`
              )}
            </dd>
          </>
        )}
      </dl>
      {el.entitySignals && el.entitySignals.length > 0 && (
        <>
          <h4>Why it is an entity</h4>
          <ul className="signals">
            {el.entitySignals.map((s) => (
              <li key={s}>{s}</li>
            ))}
          </ul>
        </>
      )}
      {el.fields && el.fields.length > 0 && (
        <>
          <h4>Fields</h4>
          <ul className="signals">
            {el.fields.map((f) => (
              <li key={f.name}>
                {f.name}: <span className="muted">{f.type}</span>
              </li>
            ))}
          </ul>
        </>
      )}
      {arrowList('Goes to', outgoing, (r) => r.to)}
      {arrowList('Comes from', incoming, (r) => r.from)}
    </aside>
  )
}
