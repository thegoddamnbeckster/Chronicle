import { useState } from 'react'
import { isImageUrl, toLabel } from '@/utils/imageExtractor'
import styles from './JsonTree.module.css'

// ── Helpers ───────────────────────────────────────────────────────────────────

function isIdKey(key: string): boolean {
  const k = key.toLowerCase()
  return k === 'id' || k === 'mbid' || k === 'uuid' || k === 'gid'
    || k.endsWith('id') || k.endsWith('_id') || k.endsWith('uuid')
    || k.endsWith('mbid')
}

function isUrl(value: unknown): value is string {
  return typeof value === 'string' &&
    (value.startsWith('http://') || value.startsWith('https://'))
}

function shouldCollapseArray(arr: unknown[]): boolean {
  if (arr.length > 3) return true
  return arr.some(
    item => typeof item === 'object' && item !== null && Object.keys(item as object).length > 4,
  )
}

function shouldCollapseObject(obj: Record<string, unknown>): boolean {
  return Object.keys(obj).length > 3
}

// ── Public component ──────────────────────────────────────────────────────────

export interface JsonTreeProps {
  data: unknown
  depth?: number
  onImageClick?: (url: string) => void
}

export function JsonTree({ data, depth = 0, onImageClick }: JsonTreeProps) {
  if (data === null || data === undefined) {
    return <span className={styles.valueNull}>—</span>
  }

  if (typeof data === 'boolean') {
    return <span className={data ? styles.boolTrue : styles.boolFalse}>{data ? 'Yes' : 'No'}</span>
  }

  if (typeof data === 'number') {
    return <span className={styles.valueNum}>{data}</span>
  }

  if (typeof data === 'string') {
    if (isImageUrl(data)) {
      return (
        <img
          src={data}
          alt=""
          className={styles.thumbnail}
          onClick={() =>
            onImageClick ? onImageClick(data) : window.open(data, '_blank')
          }
          onError={e => { e.currentTarget.style.display = 'none' }}
        />
      )
    }
    if (isUrl(data)) {
      return (
        <a href={data} target="_blank" rel="noopener noreferrer" className={styles.link}>
          {data}
        </a>
      )
    }
    return <span className={styles.valueStr}>{data}</span>
  }

  if (Array.isArray(data)) {
    return <JsonArray arr={data} depth={depth} onImageClick={onImageClick} />
  }

  if (typeof data === 'object') {
    return (
      <JsonObject
        obj={data as Record<string, unknown>}
        depth={depth}
        onImageClick={onImageClick}
      />
    )
  }

  return <span className={styles.valueStr}>{String(data)}</span>
}

// ── Object ────────────────────────────────────────────────────────────────────

function JsonObject({
  obj, depth, onImageClick,
}: {
  obj: Record<string, unknown>
  depth: number
  onImageClick?: (url: string) => void
}) {
  const [collapsed, setCollapsed] = useState(
    () => depth > 0 && shouldCollapseObject(obj),
  )
  const entries = Object.entries(obj).filter(([, v]) => v !== null && v !== undefined)

  if (entries.length === 0) return <span className={styles.valueNull}>—</span>

  return (
    <div className={styles.node}>
      {depth > 0 && (
        <button className={styles.collapseBtn} onClick={() => setCollapsed(c => !c)}>
          {collapsed ? `▶ ${entries.length} field(s)…` : '▼'}
        </button>
      )}
      {!collapsed && (
        <div className={depth > 0 ? styles.children : styles.tree}>
          {entries.map(([key, value]) => (
            <ObjectRow
              key={key}
              propKey={key}
              value={value}
              depth={depth}
              onImageClick={onImageClick}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function ObjectRow({
  propKey, value, depth, onImageClick,
}: {
  propKey: string
  value: unknown
  depth: number
  onImageClick?: (url: string) => void
}) {
  const isNested = (typeof value === 'object' && value !== null) || Array.isArray(value)
  const leafIsId = isIdKey(propKey) && !isNested

  return (
    <div className={styles.node}>
      <div className={styles.row}>
        <span className={leafIsId ? styles.keyId : styles.key}>
          {toLabel(propKey)}
        </span>
        {!isNested && (
          leafIsId
            ? <span className={styles.idBadge}>{String(value)}</span>
            : <JsonTree data={value} depth={depth + 1} onImageClick={onImageClick} />
        )}
      </div>
      {isNested && (
        <JsonTree data={value} depth={depth + 1} onImageClick={onImageClick} />
      )}
    </div>
  )
}

// ── Array ─────────────────────────────────────────────────────────────────────

function JsonArray({
  arr, depth, onImageClick,
}: {
  arr: unknown[]
  depth: number
  onImageClick?: (url: string) => void
}) {
  const [collapsed, setCollapsed] = useState(
    () => depth > 0 && shouldCollapseArray(arr),
  )

  if (arr.length === 0) return <span className={styles.valueNull}>(empty)</span>

  const allPrimitive = arr.every(
    item => item === null || item === undefined
      || (typeof item !== 'object' && !Array.isArray(item)),
  )
  if (allPrimitive) {
    return <span className={styles.inlineList}>{arr.map(String).join(', ')}</span>
  }

  return (
    <div className={styles.node}>
      <button className={styles.collapseBtn} onClick={() => setCollapsed(c => !c)}>
        {collapsed ? `▶ ${arr.length} item(s)…` : `▼ ${arr.length} items`}
      </button>
      {!collapsed && (
        <div className={styles.children}>
          {arr.map((item, i) => (
            <div key={i} className={styles.row}>
              <span className={styles.arrayIndex}>#{i + 1}</span>
              <div style={{ flex: 1, minWidth: 0 }}>
                <JsonTree data={item} depth={depth + 1} onImageClick={onImageClick} />
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
