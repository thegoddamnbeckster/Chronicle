import { describe, it, expect } from 'vitest'
import { childrenLabel, isKnownVerb, neutralStatusLabel, pluralise, statusLabel } from './typeWording'
import type { LibraryStatus, MediaTypeOption } from '@/types'

const type = (over: Partial<MediaTypeOption>): MediaTypeOption => ({ id: 1, name: 'x', displayName: 'X', hierarchyLevels: 3, ...over })

describe('statusLabel', () => {
  it.each([
    ['watched', 'Plan to Watch', 'Watching', 'Rewatching', 'Unwatched'],
    ['listened', 'Plan to Listen', 'Listening', 'Re-listening', 'Unlistened'],
    ['read', 'Plan to Read', 'Reading', 'Re-reading', 'Unread'],
    ['played', 'Plan to Play', 'Playing', 'Replaying', 'Unplayed'],
  ])('%s', (verb, plan, doing, again, not) => {
    expect(statusLabel('PlanToWatch', verb)).toBe(plan)
    expect(statusLabel('Watching', verb)).toBe(doing)
    expect(statusLabel('Rewatching', verb)).toBe(again)
    expect(statusLabel('Unwatched', verb)).toBe(not)
  })

  it('is driven by the verb, never by what the type is called', () => {
    // A user-made type that chose "listened" reads exactly like music does.
    expect(statusLabel('Watching', 'listened')).toBe('Listening')
  })

  it('defaults to watching wording when the type says nothing', () => {
    expect(statusLabel('Watching', undefined)).toBe('Watching')
  })

  it('gives neutral wording for a verb the interface has no grammar for', () => {
    expect(statusLabel('PlanToWatch', 'tasted')).toBe('Planned')
    expect(statusLabel('Watching', 'tasted')).toBe('In progress')
    expect(statusLabel('Rewatching', 'tasted')).toBe('Repeating')
    expect(statusLabel('Unwatched', 'tasted')).toBe('Not yet tasted')
  })

  it('leaves the verb-free statuses alone', () => {
    expect(statusLabel('Completed', 'read')).toBe('Completed')
    expect(statusLabel('Dropped', 'read')).toBe('Dropped')
    expect(statusLabel('OnHold', 'read')).toBe('On Hold')
  })

  it('is case-insensitive about the verb', () => {
    expect(statusLabel('Watching', 'Listened')).toBe('Listening')
    expect(isKnownVerb('READ')).toBe(true)
    expect(isKnownVerb('tasted')).toBe(false)
    expect(isKnownVerb(undefined)).toBe(false)
  })
})

describe('neutralStatusLabel', () => {
  it('never mentions watching, listening, reading or playing', () => {
    const all: LibraryStatus[] = ['Unwatched', 'Watching', 'PlanToWatch', 'Completed', 'Dropped', 'OnHold', 'Rewatching']
    for (const s of all) expect(neutralStatusLabel(s)).not.toMatch(/watch|listen|read|play/i)
  })
  it('passes an unknown status through', () => {
    expect(neutralStatusLabel('Mystery')).toBe('Mystery')
  })
})

describe('pluralise', () => {
  it.each([
    ['Season', 'Seasons'], ['Episode', 'Episodes'], ['Category', 'Categories'], ['Series', 'Series'],
    ['Box', 'Boxes'], ['Match', 'Matches'], ['Day', 'Days'], ['Species', 'Species'], ['', ''],
  ])('%s -> %s', (a, b) => expect(pluralise(a)).toBe(b))
})

describe('childrenLabel', () => {
  const tv = type({ hierarchyLabels: ['Show', 'Season', 'Episode'] })

  it('names the next level down for the item', () => {
    expect(childrenLabel(tv, 0)).toBe('Seasons')
    expect(childrenLabel(tv, 1)).toBe('Episodes')
  })

  it('says Items below the last level, or when the type is unknown', () => {
    expect(childrenLabel(tv, 2)).toBe('Items')
    expect(childrenLabel(undefined, 0)).toBe('Items')
    expect(childrenLabel(type({ hierarchyLabels: [] }), 0)).toBe('Items')
  })

  it('names the children of a bucket type after its own top level', () => {
    expect(childrenLabel(type({ hierarchyLevels: 1, hierarchyLabels: ['Movie'], supportsCollections: true }), 0)).toBe('Movies')
  })

  it('works for a type nobody wrote code for', () => {
    expect(childrenLabel(type({ hierarchyLabels: ['Series', 'Volume', 'Issue'] }), 0)).toBe('Volumes')
  })
})
