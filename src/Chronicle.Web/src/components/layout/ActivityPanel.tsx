import { useBackgroundActivity, type Job } from '@/contexts/BackgroundActivityContext'
import styles from './ActivityPanel.module.css'

// ── Icons (inline SVG) ────────────────────────────────────────────────────────

function SpinnerIcon() {
  return (
    <svg className={styles.spinner} width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
      <circle cx="7" cy="7" r="5.5" stroke="currentColor" strokeWidth="1.5" strokeDasharray="20" strokeDashoffset="10" strokeLinecap="round" />
    </svg>
  )
}

function CheckIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
      <path d="M2.5 7L5.5 10L11.5 4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function XIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 14 14" fill="none" aria-hidden="true">
      <path d="M3 3L11 11M11 3L3 11" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
    </svg>
  )
}

// ── Job row ───────────────────────────────────────────────────────────────────

function JobRow({ job }: { job: Job }) {
  const statusClass =
    job.status === 'running' ? styles.running :
    job.status === 'done'    ? styles.done :
    styles.failed

  return (
    <div className={`${styles.job} ${statusClass}`}>
      <span className={styles.icon}>
        {job.status === 'running' && <SpinnerIcon />}
        {job.status === 'done'    && <CheckIcon />}
        {job.status === 'failed'  && <XIcon />}
      </span>
      <span className={styles.jobText}>
        <span className={styles.label}>{job.label}</span>
        {job.detail && <span className={styles.detail}>{job.detail}</span>}
      </span>
    </div>
  )
}

// ── Panel ─────────────────────────────────────────────────────────────────────

export default function ActivityPanel() {
  const { jobs } = useBackgroundActivity()

  if (jobs.length === 0) return null

  return (
    <div className={styles.panel}>
      {jobs.map(job => <JobRow key={job.id} job={job} />)}
    </div>
  )
}
