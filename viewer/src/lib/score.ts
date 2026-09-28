import type { Finding } from './types'

export interface FindingGroup {
  check: string
  penalty: number
  findings: Finding[]
}

// One group per check, biggest total penalty first, so the worst problem is read first.
export function groupFindings(findings: Finding[]): FindingGroup[] {
  const groups = new Map<string, FindingGroup>()
  for (const f of findings) {
    const group = groups.get(f.check) ?? { check: f.check, penalty: 0, findings: [] }
    group.penalty += f.penalty
    group.findings.push(f)
    groups.set(f.check, group)
  }
  return [...groups.values()].sort((a, b) => b.penalty - a.penalty || a.check.localeCompare(b.check))
}

// Color band for the big number. The cut-offs are for display only; the score itself comes from the analyzer.
export function scoreBand(total: number): 'good' | 'fair' | 'poor' {
  if (total >= 80) return 'good'
  if (total >= 50) return 'fair'
  return 'poor'
}
