import { useState } from 'react'
import FolderPickerModal from './FolderPickerModal'
import styles from './PathInput.module.css'

interface PathInputProps {
  value: string
  onChange: (value: string) => void
  onBlur?: () => void
  placeholder?: string
  id?: string
  className?: string
  wrapperClassName?: string
  disabled?: boolean
  autoFocus?: boolean
}

export default function PathInput({
  value,
  onChange,
  onBlur,
  placeholder,
  id,
  className,
  wrapperClassName,
  disabled,
  autoFocus,
}: PathInputProps) {
  const [showPicker, setShowPicker] = useState(false)

  return (
    <>
      <div className={[styles.wrapper, wrapperClassName].filter(Boolean).join(' ')}>
        <input
          id={id}
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onBlur={onBlur}
          placeholder={placeholder}
          className={[className, styles.input].filter(Boolean).join(' ')}
          disabled={disabled}
          autoFocus={autoFocus}
        />
        <button
          type="button"
          className={styles.browseBtn}
          onClick={() => setShowPicker(true)}
          aria-label="Browse for folder"
          disabled={disabled}
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
