import { describe, expect, it } from 'vitest'
import { formatElapsed, normalizeRepoUrl, stepState } from './api'

describe('normalizeRepoUrl', () => {
  // Every common way of pasting a repo link ends up as the same canonical form.
  it('accepts the usual shapes', () => {
    for (const input of [
      'github.com/dotnet/eShop',
      'https://github.com/dotnet/eShop',
      'http://www.github.com/dotnet/eShop/',
      'https://github.com/dotnet/eShop.git',
      '  github.com/dotnet/eShop  ',
    ]) {
      expect(normalizeRepoUrl(input)).toBe('github.com/dotnet/eShop')
    }
  })

  // Deep links, other hosts and bare names are refused before any request is sent.
  it('rejects anything that is not owner/repo on github.com', () => {
    for (const input of [
      '',
      'dotnet/eShop',
      'https://gitlab.com/dotnet/eShop',
      'https://github.com/dotnet',
      'https://github.com/dotnet/eShop/tree/main/src',
      'github.com/dotnet/eShop?tab=readme',
    ]) {
      expect(normalizeRepoUrl(input)).toBeNull()
    }
  })
})

describe('stepState', () => {
  // While cloning: queued is finished, cloning runs, the rest wait.
  it('marks steps around the current one', () => {
    expect(stepState('cloning', 'queued')).toBe('done')
    expect(stepState('cloning', 'cloning')).toBe('active')
    expect(stepState('cloning', 'analyzing')).toBe('todo')
  })

  // Once done, every step shows as finished, including Done itself.
  it('finishes every step when done', () => {
    expect(stepState('done', 'done')).toBe('done')
    expect(stepState('done', 'queued')).toBe('done')
  })
})

describe('formatElapsed', () => {
  // Seconds under a minute, minutes and seconds above.
  it('formats short and long runs', () => {
    expect(formatElapsed(4200)).toBe('4s')
    expect(formatElapsed(75_000)).toBe('1m 15s')
  })
})
