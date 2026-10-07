import { useState } from 'react'
import type { ScanGroupDto, ImportGroupPayload } from '@/types'
import styles from './ScanGroupCard.module.css'

interface Props {
  group: ScanGroupDto
  checked: boolean
  onToggle: (groupKey: string) => void
  /** When given, a group that looks like another media type offers a one-click switch and rescan. */
  onSwitchType?: (mediaTypeId: number) => void
}

interface ChildProps {
  group: ScanGroupDto
  depth?: number
}

function confidenceClass(score: number): string {
  if (score >= 80) return 'green'
  if (score >= 50) return 'amber'
  return 'red'
}

function childCount(g: ScanGroupDto): number {
  if (g.children.length === 0) return g.files.length
  return g.children.reduce((sum, c) => sum + childCount(c), 0)
}

export function groupToPayload(g: ScanGroupDto): ImportGroupPayload {
  return {
    name: g.name,
    year: g.year,
    number: g.number,
    posterPath: g.posterPath,
    children: g.children.map(groupToPayload),
    files: g.files,
    folderPath: g.folderPath,
    relatedFiles: g.relatedFiles ?? [],
  }
}

/** Total related files under a group and everything below it. */
export function relatedFileCount(g: ScanGroupDto): number {
  return (g.relatedFiles?.length ?? 0) + g.children.reduce((sum, c) => sum + relatedFileCount(c), 0)
}

/** Recursive child row — renders seasons, albums, episodes, tracks etc. */
function ScanGroupChild({ group, depth = 0 }: ChildProps) {
  const [expanded, setExpanded] = useState(false)
  const count = childCount(group)
  const cc = confidenceClass(group.confidenceScore)

  return (
    <div style={depth > 0 ? { paddingLeft: `${depth * 16}px` } : undefined}>
      <div className={styles.childRow}>
        <span className={styles.childName}>{group.name}</span>
        {group.year && <span className={styles.childYear}>({group.year})</span>}
        <span className={styles.childCount}>{count} items</span>
        <span className={`${styles.childConfidence} ${styles[cc]}`}>
          {group.confidenceScore}%
        </span>
        {group.children.length > 0 && (
          <button
            className={styles.expandBtn}
            onClick={() => setExpanded(e => !e)}
            aria-label={expanded ? 'Collapse' : 'Expand'}
          >
            {expanded ? '▲' : '▼'}
          </button>
        )}
      </div>

      {expanded && group.children.length > 0 && (
        <div className={styles.children}>
          {group.children.map(child => (
            <ScanGroupChild key={child.groupKey} group={child} depth={0} />
          ))}
        </div>
      )}
    </div>
  )
}

export default function ScanGroupCard({ group, checked, onToggle, onSwitchType }: Props) {
  const [expanded, setExpanded] = useState(false)
  const cc = confidenceClass(group.confidenceScore)
  const totalItems = childCount(group)
  const related = relatedFileCount(group)

  return (
    <div className={`${styles.card} ${!checked ? styles.cardUnchecked : ''}`}>
      <div className={styles.row}>
        <input
          type="checkbox"
          checked={checked}
          onChange={() => onToggle(group.groupKey)}
          className={styles.check}
        />
        <div className={styles.info}>
          <span className={styles.name}>{group.name}</span>
          {group.year && <span className={styles.year}>({group.year})</span>}
          {group.author && <span className={styles.author}>by {group.author}</span>}
          {group.series && <span className={styles.series}>{group.series}</span>}
          <span className={styles.itemCount}>{totalItems} items</span>
          {related > 0 && (
            <span className={styles.itemCount} title="Subtitles, artwork and extras found with this item">
              +{related} related file{related !== 1 ? 's' : ''}
            </span>
          )}
          {group.suggestedMediaTypeId != null && (
            <span className={styles.mismatchBadge} title={group.suggestedMediaTypeReason ?? undefined}>
              ⚠ looks like {group.suggestedMediaTypeName}
              {onSwitchType && (
                <button
                  type="button"
                  className={styles.mismatchBtn}
                  onClick={() => onSwitchType(group.suggestedMediaTypeId as number)}
                >
                  Switch to {group.suggestedMediaTypeName} and rescan
                </button>
              )}
            </span>
          )}
          {group.hasConflicts && (
            <span className={styles.conflictBadge} title="Signal sources disagree on this group">
              ⚠ conflict
            </span>
          )}
        </div>
        <div className={styles.right}>
          <span
            className={`${styles.confidence} ${styles[cc]}`}
            title={`Signals: ${group.signalSources.join(', ')}`}
          >
            {group.confidenceScore}%
          </span>
          {group.children.length > 0 && (
            <button
              className={styles.expandBtn}
              onClick={() => setExpanded(e => !e)}
              aria-label={expanded ? 'Collapse' : 'Expand'}
            >
              {expanded ? '▲' : '▼'}
            </button>
          )}
        </div>
      </div>

      {expanded && group.children.length > 0 && (
        <div className={styles.children}>
          {group.children.map(child => (
            <ScanGroupChild key={child.groupKey} group={child} />
          ))}
        </div>
      )}
    </div>
  )
}
