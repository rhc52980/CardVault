/**
 * Brief confirmations that something happened.
 *
 * A module-level list rather than a React context: a toast is fired from event
 * handlers deep in dialogs that are about to unmount themselves, and threading a
 * provider through every one of them to say "added" would be more plumbing than the
 * message is worth.
 *
 * Deliberately for things that worked. A failure needs to stay on screen next to the
 * thing that failed, where you can act on it — the add dialog already does that with
 * its own error line, and replacing it with something that fades after three seconds
 * would be worse.
 */
export interface Toast {
  id: number
  message: string
  /** A second line, for the detail that identifies what was acted on. */
  detail?: string
}

type Listener = (toasts: Toast[]) => void

let toasts: Toast[] = []
let nextId = 1
const listeners = new Set<Listener>()

function emit() {
  for (const l of listeners) l(toasts)
}

export function subscribe(listener: Listener) {
  listeners.add(listener)
  listener(toasts)
  return () => {
    listeners.delete(listener)
  }
}

export function dismissToast(id: number) {
  toasts = toasts.filter((t) => t.id !== id)
  emit()
}

/**
 * Says something worked. Auto-clears, because a confirmation you have to dismiss is
 * a worse interruption than the one it's confirming.
 */
export function toast(message: string, detail?: string) {
  const id = nextId++
  // Capped so a bulk action firing many at once can't bury the screen.
  toasts = [...toasts, { id, message, detail }].slice(-3)
  emit()
  setTimeout(() => dismissToast(id), 3200)
}
