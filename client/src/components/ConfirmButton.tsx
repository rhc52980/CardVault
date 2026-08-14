import { useEffect, useRef, useState } from 'react'

/**
 * A destructive button that asks first.
 *
 * The confirmation happens in place rather than in a dialog: the button turns
 * into the answer to its own question, so the thing being deleted stays on
 * screen and visible while you decide. A modal would cover the card you are
 * looking at, which is the one thing you want to check.
 *
 * Reverts on its own after a few seconds, and on a click anywhere else, so an
 * armed delete is never left sitting under the cursor waiting to be hit.
 */
export function ConfirmButton({
  onConfirm,
  label = 'Remove',
  confirm = 'Really remove',
  title,
  disabled,
  className = '',
}: {
  onConfirm: () => void | Promise<void>
  label?: string
  /** Shown once armed. Say what will happen, not just "Yes". */
  confirm?: string
  title?: string
  disabled?: boolean
  className?: string
}) {
  const [armed, setArmed] = useState(false)
  const wrap = useRef<HTMLSpanElement>(null)

  useEffect(() => {
    if (!armed) return

    const timer = setTimeout(() => setArmed(false), 5000)
    const away = (e: MouseEvent) => {
      if (!wrap.current?.contains(e.target as Node)) setArmed(false)
    }
    const escape = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setArmed(false)
    }

    document.addEventListener('mousedown', away)
    document.addEventListener('keydown', escape)
    return () => {
      clearTimeout(timer)
      document.removeEventListener('mousedown', away)
      document.removeEventListener('keydown', escape)
    }
  }, [armed])

  if (!armed) {
    return (
      <span ref={wrap}>
        <button
          onClick={() => setArmed(true)}
          disabled={disabled}
          title={title}
          className={`rounded-md px-2 py-1 text-xs text-mute transition hover:bg-rose/10 hover:text-rose disabled:opacity-30 ${className}`}
        >
          {label}
        </button>
      </span>
    )
  }

  return (
    <span ref={wrap} className="inline-flex items-center gap-1">
      <button
        onClick={async () => {
          setArmed(false)
          await onConfirm()
        }}
        disabled={disabled}
        className="rounded-md bg-rose px-2 py-1 text-xs font-medium text-white transition hover:brightness-110 disabled:opacity-40"
      >
        {confirm}
      </button>
      <button
        onClick={() => setArmed(false)}
        className="rounded-md px-2 py-1 text-xs text-mute transition hover:text-bright"
      >
        Cancel
      </button>
    </span>
  )
}
