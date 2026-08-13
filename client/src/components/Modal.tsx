import { useEffect, type ReactNode } from 'react'

export function Modal({
  onClose,
  children,
  wide = false,
}: {
  onClose: () => void
  children: ReactNode
  wide?: boolean
}) {
  // Escape closes, and the page behind shouldn't scroll while we're open.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = previous
    }
  }, [onClose])

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/70 p-4 backdrop-blur-sm sm:items-center"
      onClick={onClose}
    >
      <div
        className={`rise panel my-auto w-full rounded-2xl shadow-2xl ${wide ? 'max-w-4xl' : 'max-w-md'}`}
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
      >
        {children}
      </div>
    </div>
  )
}
