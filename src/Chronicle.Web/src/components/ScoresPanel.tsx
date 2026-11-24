import { useQuery } from '@tanstack/react-query'
import { listPlugins } from '@/api/plugins'
import type { ItemScore } from '@/types'
import { formatScore, formatVotes, scoreLabel, starFills } from '@/utils/scores'
import styles from './ScoresPanel.module.css'

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

/** The mark: the plugin's own icon when it has one, else a letter badge. */
function Mark({ score, iconUrl }: { score: ItemScore; iconUrl?: string | null }) {
  if (iconUrl) return <img className={styles.mark} src={iconUrl} alt="" />
  return <span className={styles.letter} aria-hidden="true">{score.displayName.slice(0, 1).toUpperCase()}</span>
}

function Badge({ score, iconUrl }: { score: ItemScore; iconUrl?: string | null }) {
  const f = formatScore(score)
  const title = [
    `${score.source}, ${score.display === 'percent' ? `${f.value}%` : `${f.value}${f.suffix}`}`,
    score.votes ? `${score.votes.toLocaleString('en-US')} votes` : null,
    score.alsoFrom.length ? `Also reported by ${score.alsoFrom.join(', ')}` : null,
  ].filter(Boolean).join(' · ')
  const body = (
    <>
      <span className={styles.main}>
      <Mark score={score} iconUrl={iconUrl} />
      {score.star === 'single' && <Star fill={1} />}
      {score.star === 'row' && starFills(score.value, score.scale).map((fill, i) => <Star key={i} fill={fill} />)}
      <span className={styles.score}>
        <strong>{f.value}</strong>
        <span className={styles.suffix}>{f.suffix}</span>
      </span>
      {score.variant ? <span className={styles.votes}>{score.variant}</span> : null}
      {score.votes ? <span className={styles.votes}>{formatVotes(score.votes)}</span> : null}
      </span>
      {score.attribution ? <span className={styles.attribution}>{score.attribution}</span> : null}
    </>
  )
  return score.url ? (
    <a className={styles.badge} href={score.url} target="_blank" rel="noopener noreferrer" title={title} aria-label={scoreLabel(score)}>{body}</a>
  ) : (
    <span className={styles.badge} title={title} aria-label={scoreLabel(score)} role="group">{body}</span>
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
