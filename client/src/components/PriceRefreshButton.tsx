import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import type { PriceRefreshProgress } from '../types'

/**
 * Asks for a price refresh, and shows how it's getting on.
 *
 * Used in two places — the dashboard and the price sources panel — so the polling
 * lives here once rather than in both. The server is the source of truth for
 * whether a run is happening, which matters because the daily timer can start one
 * on its own: without that, the page would show idle mid-sweep and pressing the
 * button would look like it did nothing.
 */
export function PriceRefreshButton({
  onFinished,
  compact = false,
}: {
  /** Called once when a run ends, so the caller can reload whatever it shows. */
  onFinished?: () => void
  /** Drops the trailing explanation, for the dashboard where space is tight. */
  compact?: boolean
}) {
  const [progress, setProgress] = useState<PriceRefreshProgress | null>(null)
  const [busy, setBusy] = useState(false)
  const wasRunning = useRef(false)

  const load = () => api.refreshProgress().then(setProgress).catch(() => {})

  useEffect(() => {
    void load()
  }, [])

  const running = progress?.running ?? false

  useEffect(() => {
    if (!running) return
    const id = setInterval(() => void load(), 1000)
    return () => clearInterval(id)
  }, [running])

  // Fires on the transition out of running, so the collection and its totals
  // refresh themselves the moment new prices land.
  useEffect(() => {
    if (wasRunning.current && !running) onFinished?.()
    wasRunning.current = running
  }, [running, onFinished])

  async function start(onlyMissing = false) {
    setBusy(true)
    try {
      setProgress(await api.refreshPrices(onlyMissing))
    } catch {
      // A 409 means the daily run beat us to it, which is not worth an error.
      await load()
    } finally {
      setBusy(false)
    }
  }

  const unpriced = progress?.unpriced ?? 0

  return (
    <div className="flex flex-wrap items-center gap-3">
      <button
        onClick={() => void start()}
        disabled={busy || running}
        className="rounded-lg border border-edge px-3 py-1.5 text-sm text-bright transition hover:border-arc disabled:opacity-40"
      >
        {running ? (progress?.onlyMissing ? 'Filling in prices…' : 'Refreshing prices…') : 'Refresh prices'}
      </button>

      {/*
        Offered only when there is a gap to fill, and it names the number, so the
        button is its own explanation and takes no room on a collection that is
        fully priced. The full sweep is an API call per card with pacing between
        them — minutes on a few thousand cards — where this is usually the handful
        an import just brought in.
      */}
      {unpriced > 0 && !running && (
        <button
          onClick={() => void start(true)}
          disabled={busy}
          className="rounded-lg border border-edge px-3 py-1.5 text-sm text-mute transition hover:border-arc hover:text-bright disabled:opacity-40"
          title="Only asks about cards with no price yet, rather than re-pricing the whole collection"
        >
          Fill in {unpriced.toLocaleString()} missing
        </button>
      )}

      {running ? (
        <span className="text-xs text-mute tabular-nums">
          {progress!.done.toLocaleString()} / {progress!.total.toLocaleString()} cards
        </span>
      ) : progress?.error ? (
        <span className="text-xs text-rose">{progress.error}</span>
      ) : progress?.finishedAt ? (
        <span className="text-xs text-mute">
          Last run {new Date(progress.finishedAt).toLocaleString()}
          {progress.detail ? ` · ${progress.detail}` : ''}
        </span>
      ) : null}

      {!compact && (
        <span className="text-xs text-mute">
          Prices also refresh on their own once a day.
        </span>
      )}
    </div>
  )
}
