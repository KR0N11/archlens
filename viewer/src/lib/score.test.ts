import { describe, expect, it } from 'vitest'
import { groupFindings, scoreBand } from './score'
import type { Finding } from './types'

const f = (check: string, penalty: number): Finding => ({ check, message: check, penalty, elements: [], relationships: [] })

describe('groupFindings', () => {
  // Findings of one check are summed; the check that costs the most comes first.
  it('groups by check and sorts by total penalty', () => {
    const groups = groupFindings([f('Orphan element', 1), f('God class', 3), f('Orphan element', 1), f('Orphan element', 0)])
    expect(groups.map((g) => [g.check, g.penalty, g.findings.length])).toEqual([
      ['God class', 3, 1],
      ['Orphan element', 2, 3],
    ])
  })
})

describe('scoreBand', () => {
  // The band edges belong to the higher band.
  it('bands the total', () => {
    expect(scoreBand(80)).toBe('good')
    expect(scoreBand(79)).toBe('fair')
    expect(scoreBand(50)).toBe('fair')
    expect(scoreBand(49)).toBe('poor')
  })
})
