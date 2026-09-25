import styles from './ArtTypeTag.module.css'

/**
 * A discreet corner tag naming which kind of art an image is (Poster, Logo, Banner ...), so the
 * right image can be picked for the right slot when changing art. Positioned absolutely in the
 * bottom-right corner of its nearest positioned ancestor -- the image wrapper.
 */
export function ArtTypeTag({ label }: { label: string }) {
  return <span className={styles.tag} aria-hidden="true">{label}</span>
}
