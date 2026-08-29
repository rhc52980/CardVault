import { useEffect, useState } from 'react'
import { dismissToast, subscribe, type Toast } from '../lib/toast'

/**
 * Where confirmations appear. One of these lives at the app root.
 *
 * Bottom right, out of the way of the search box and the grid — adding cards from a
 * physical stack means typing, and a message over the input would be worse than no
 * message. Clickable, so it can be got rid of early, and it announces itself to
 * screen readers since a card landing in the vault is otherwise silent.
 */
export function Toaster() {
  const [toasts, setToasts] = useState<Toast[]>([])

  useEffect(() => subscribe(setToasts), [])

  if (toasts.length === 0) return null

  return (
    <div
      aria-live="polite"
      className="pointer-events-none fixed right-4 bottom-4 z-50 flex flex-col items-end gap-2"
    >
      {toasts.map((t) => (
        <button
          key={t.id}
          onClick={() => dismissToast(t.id)}
          className="panel pointer-events-auto max-w-xs rounded-xl border border-mint/40 px-4 py-2.5 text-left shadow-lg transition hover:border-mint"
        >
          <div className="text-sm font-medium text-mint">✓ {t.message}</div>
          {t.detail && <div className="mt-0.5 truncate text-xs text-mute">{t.detail}</div>}
        </button>
      ))}
    </div>
  )
}
