import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { listPlugins } from '@/api/plugins'
import type { ItemScore, ScoreTier } from '@/types'
import { fontStack, formatScore, formatVotes, scoreLabel, starFills, tierFor } from '@/utils/scores'
import styles from './ScoresPanel.module.css'
// The open-licence faces a manifest may name (see ItemScores.Fonts and docs/THIRD_PARTY_FONTS.md); Latin only, bundled so nothing is fetched at run time.
import '@fontsource/roboto/latin-400.css'
import '@fontsource/roboto/latin-600.css'
import '@fontsource/roboto/latin-700.css'
import '@fontsource/source-sans-3/latin-400.css'
import '@fontsource/source-sans-3/latin-600.css'
import '@fontsource/source-sans-3/latin-700.css'
import '@fontsource/libre-franklin/latin-400.css'
import '@fontsource/libre-franklin/latin-600.css'
import '@fontsource/libre-franklin/latin-700.css'
import '@fontsource/nunito-sans/latin-400.css'
import '@fontsource/nunito-sans/latin-600.css'
import '@fontsource/nunito-sans/latin-700.css'

const STAR_PATH =
  'M12 17.27l4.15 2.51c.76.46 1.69-.22 1.49-1.08l-1.1-4.72 3.67-3.18c.67-.58.31-1.68-.57-1.75l-4.83-.41-1.89-4.46c-.34-.81-1.5-.81-1.84 0L9.19 8.63l-4.83.41c-.88.07-1.24 1.17-.57 1.75l3.67 3.18-1.1 4.72c-.2.86.73 1.54 1.49 1.08l4.15-2.5z'

function Star({ fill }: { fill: number }) {
  return (
    <span className={styles.star} aria-hidden="true">
      <svg viewBox="0 0 24 24" className={styles.starBase}><path d={STAR_PATH} /></svg>
      <span className={styles.starFill} style={{ width: `${fill * 100}%` }}>
        <svg viewBox="0 0 24 24" className={styles.starOn}><path d={STAR_PATH} /></svg>
      </span>
    </span>
  )
}

/** The mark: the source's own logo when the plugin declared one, else the plugin's icon, else a letter badge. */
function Mark({ score, iconUrl }: { score: ItemScore; iconUrl?: string | null }) {
  const [failed, setFailed] = useState(false)
  const src = score.logoUrl ?? iconUrl
  // An image the server could not fetch must not leave a broken-image glyph: fall back to the letter.
  if (src && !failed) return <img className={styles.mark} src={src} alt="" onError={() => setFailed(true)} />
  return <span className={styles.letter} aria-hidden="true">{score.displayName.slice(0, 1).toUpperCase()}</span>
}

/** The state glyphs a tier can name, drawn here in the tier's colour (never fetched, so a missing image is impossible). */
function StateIcon({ icon, color }: { icon?: ScoreTier['icon']; color: string }) {
  if (!icon) return null
  return (
    <svg className={styles.stateIcon} viewBox="0 0 24 24" aria-hidden="true" data-icon={icon}>
      {icon === 'fresh' && (<><circle cx="12" cy="14" r="8" fill={color} /><path d="M12 6c0-2 2-3 4-3-.3 2-1.6 3.2-4 3zM12 6c-1-1.6-3-2-4.6-1.4 1 1.4 2.600 2 4.600 1.400z" fill="#2E7D32" /></>)}
      {icon === 'certified' && (<><circle cx="12" cy="12" r="10.500" fill="none" stroke={color} strokeWidth="1.500" /><circle cx="12" cy="13.500" r="6.500" fill={color} /><path d="M12 7c0-1.600 1.600-2.500 3.200-2.500-.2 1.600-1.300 2.600-3.200 2.500z" fill="#2E7D32" /></>)}
      {icon === 'rotten' && <path d="M12 3l2.200 3.600 4.100-.9-1 4.100L21 12.600l-3.700 2 .6 4.200-4.100-1L12 21l-1.800-3.200-4.100 1 .6-4.200-3.700-2 3.700-2.800-1-4.100 4.100.9z" fill={color} />}
      {icon === 'bucket-full' && (<><path d="M5 9h14l-1.600 12H6.600z" fill={color} /><path d="M7 9c0-3 2-4.500 5-4.500S17 6 17 9" fill="#fff" stroke={color} strokeWidth="1.500" /></>)}
      {icon === 'bucket-tipped' && <path d="M4 14l12-6 3 6-9 7z" fill={color} />}
    </svg>
  )
}

/** A circular progress ring around the whole-number percent (TMDB's own widget). */
function Ring({ percent, tier }: { percent: number; tier?: ScoreTier }) {
  const r = 18
  const c = 2 * Math.PI * r
  const clamped = Math.max(0, Math.min(100, percent))
  return (
    <span className={styles.ring} aria-hidden="true">
      <svg viewBox="0 0 44 44" className={styles.ringSvg}>
        <circle cx="22" cy="22" r="20" className={styles.ringFill} />
        <circle cx="22" cy="22" r={r} fill="none" strokeWidth="3" stroke={tier?.track ?? 'var(--border)'} />
        <circle cx="22" cy="22" r={r} fill="none" strokeWidth="3" strokeLinecap="round" stroke={tier?.color ?? 'var(--text-muted)'}
          strokeDasharray={`${(c * clamped) / 100} ${c}`} transform="rotate(-90 22 22)" />
      </svg>
      <span className={styles.ringNumber}>{Math.round(clamped)}<sup>%</sup></span>
    </span>
  )
}

function Badge({ score, iconUrl }: { score: ItemScore; iconUrl?: string | null }) {
  const f = formatScore(score)
  const title = [
    `${score.source}, ${score.display === 'percent' ? `${f.value}%` : `${f.value}${f.suffix}`}`,
    score.votes ? `${score.votes.toLocaleString('en-US')} votes` : null,
    score.alsoFrom.length ? `Also reported by ${score.alsoFrom.join(', ')}` : null,
  ].filter(Boolean).join(' · ')
  const look = score.look ?? 'plain'
  const tier = tierFor(score.tiers, score.normalized)
  const votes = score.votes ? <span className={styles.votes}>{formatVotes(score.votes)}</span> : null
  const caption = score.label ?? score.variant

  let main: React.ReactNode
  if (look === 'ring') {
    main = (<><Ring percent={score.normalized} tier={tier} />{caption ? <span className={styles.caption}>{caption}</span> : null}{votes}</>)
  } else if (look === 'badge') {
    main = (
      <>
        <span className={`${styles.block} ${score.shape === 'circle' ? styles.circle : ''}`}
          style={{ background: tier?.color ?? 'var(--text-muted)', color: score.textColor ?? '#262626' }}>{f.value}</span>
        {caption ? <span className={styles.caption}>{caption}</span> : null}{votes}
      </>
    )
  } else if (look === 'icon-percent') {
    main = (
      <>
        <StateIcon icon={score.state ?? tier?.icon} color={tier?.color ?? 'var(--text-muted)'} />
        <span className={styles.big}>{score.display === 'percent' ? `${f.value}%` : f.value}</span>
        {caption ? <span className={styles.caption}>{caption}</span> : null}{votes}
      </>
    )
  } else if (look === 'star-text') {
    main = (
      <>
        <Mark score={score} iconUrl={iconUrl} />
        <Star fill={1} />
        <span className={styles.stack}>
          <span className={styles.score}><strong className={styles.bold}>{f.value}</strong><span className={styles.suffix}>{f.suffix}</span></span>
          {votes}
        </span>
        {caption ? <span className={styles.caption}>{caption}</span> : null}
      </>
    )
  } else {
    main = (
      <>
        <Mark score={score} iconUrl={iconUrl} />
        {score.star === 'single' && <Star fill={1} />}
        {score.star === 'row' && starFills(score.value, score.scale).map((fill, i) => <Star key={i} fill={fill} />)}
        <span className={styles.score}>
          <strong>{f.value}</strong>
          <span className={styles.suffix}>{f.suffix}</span>
        </span>
        {score.variant ? <span className={styles.votes}>{score.variant}</span> : null}
        {votes}
      </>
    )
  }

  const body = (
    <>
      <span className={styles.main} style={{ fontFamily: fontStack(score.fontFamily) }}>{main}</span>
      {score.attribution ? <span className={styles.attribution}>{score.attribution}</span> : null}
    </>
  )
  return score.url ? (
    <a className={styles.badge} data-look={look} href={score.url} target="_blank" rel="noopener noreferrer" title={title} aria-label={scoreLabel(score)}>{body}</a>
  ) : (
    <span className={styles.badge} data-look={look} title={title} aria-label={scoreLabel(score)} role="group">{body}</span>
  )
}

/**
 * Every score the enabled plugins hold for the item, each in its source's own scale and style. Renders nothing when there are none
 * (collections, people, items no plugin has scored), so no empty box is left behind.
 */
export function ScoresPanel({ scores }: { scores?: ItemScore[] | null }) {
  const { data: plugins = [] } = useQuery({ queryKey: ['plugins'], queryFn: listPlugins })
  if (!scores?.length) return null
  return (
    <div className={styles.panel} data-testid="scores-panel">
      <div className={styles.row}>
        {scores.map(s => (
          <Badge key={`${s.key}:${s.variant ?? ''}`} score={s} iconUrl={plugins.find(p => p.pluginId === s.pluginId)?.iconUrl} />
        ))}
      </div>
    </div>
  )
}
