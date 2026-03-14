import { useState } from 'react'
import FolderPickerModal from './FolderPickerModal'
import styles from './PathInput.module.css'

interface PathInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  id?: string
  className?: string
}

export default function PathInput({
  value,
  onChange,
  placeholder,
  id,
  className,
}: PathInputProps) {
  const [showPicker, setShowPicker] = useState(false)

  return (
    <>
      <div className={styles.wrapper}>
        <input
          id={id}
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          placeholder={placeholder}
          className={[className, styles.input].filter(Boolean).join(' ')}
        />
        <button
          type="button"
          className={styles.browseBtn}
          onClick={() => setShowPicker(true)}
          aria-label="Browse for folder"
        >
          📁 Browse
        </button>
      </div>

      {showPicker && (
        <FolderPickerModal
          initialPath={value}
          onSelect={(path) => onChange(path)}
          onClose={() => setShowPicker(false)}
        />
      )}
    </>
  )
}
