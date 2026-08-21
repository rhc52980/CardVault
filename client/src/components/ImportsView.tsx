import { api } from '../api'
import type { ImportBatchSummary } from '../types'
import { ConfirmButton } from './ConfirmButton'

/**
 * Every import that still has cards in the collection.
 *
 * This used to sit as a stack of banners on top of the vault, which worked for
 * one unchecked import and became a wall for six. It's its own view now, but the
 * vault keeps a one-line pointer while anything is unchecked — the original point
 * was that a batch can't be quietly forgotten half-checked, and hiding it entirely
 * behind a button would give that up.
 */
export function ImportsView({
  batches,
  onChanged,
  onShowOnly,
}: {
  batches: ImportBatchSummary[]
  onChanged: () => Promise<void> | void
  /** Jumps to the vault filtered to one import. */
  onShowOnly: (batchId: string) => void
}) {
  const unreviewed = batches.filter((b) => !b.acknowledgedAt)

  if (batches.length === 0) {
    return (
      <div className="panel rounded-2xl px-6 py-16 text-center">
        <p className="text-lg text-bright">No imports yet</p>
        <p className="mx-auto mt-2 max-w-md text-sm text-mute">
          Cards added by CSV import are grouped into batches here, so you can look over what
          arrived and undo a whole import if it came in wrong.
        </p>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <div className="panel flex flex-wrap items-center gap-x-8 gap-y-2 rounded-xl px-4 py-3">
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Imports</div>
          <div className="text-xl font-semibold tabular-nums text-bright">{batches.length}</div>
        </div>
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Cards added</div>
          <div className="text-xl font-semibold tabular-nums text-bright">
            {batches.reduce((n, b) => n + b.cards, 0).toLocaleString()}
          </div>
        </div>
        {unreviewed.length > 0 && (
          <>
            <div className="rounded-lg bg-gold/10 px-3 py-2 text-sm text-gold">
              {unreviewed.length} not checked yet
            </div>
            {unreviewed.length > 1 && (
              <button
                onClick={async () => {
                  await api.acknowledgeAllImports()
                  await onChanged()
                }}
                className="text-xs text-mute underline-offset-2 transition hover:text-bright hover:underline"
              >
                Mark all {unreviewed.length} as checked
              </button>
            )}
          </>
        )}
      </div>

      <div className="space-y-2">
        {batches.map((batch) => (
          <div
            key={batch.id}
            className={`panel flex flex-wrap items-center justify-between gap-3 rounded-xl p-3 ${
              batch.acknowledgedAt ? '' : 'ring-1 ring-gold/40'
            }`}
          >
            <div className="min-w-0">
              <p className="text-sm">
                <span className="text-bright">
                  {batch.cards.toLocaleString()} {batch.cards === 1 ? 'card' : 'cards'}
                </span>
                <span className="text-mute"> imported {new Date(batch.createdAt).toLocaleString()}</span>
              </p>
              <p className="mt-0.5 text-xs">
                {batch.acknowledgedAt ? (
                  <span className="text-mint">
                    checked {new Date(batch.acknowledgedAt).toLocaleDateString()}
                  </span>
                ) : (
                  <span className="text-gold">not checked yet</span>
                )}
                <span className="text-mute">
                  {' '}· {batch.entries} {batch.entries === 1 ? 'entry' : 'entries'} still here
                </span>
                {batch.modified > 0 && (
                  <span className="text-mute"> · {batch.modified} edited or partly sold since</span>
                )}
              </p>
            </div>

            <div className="flex items-center gap-2">
              <button
                onClick={() => onShowOnly(batch.id)}
                className="rounded-md border border-edge px-2.5 py-1 text-xs text-mute transition hover:text-bright"
              >
                Show these cards
              </button>
              {!batch.acknowledgedAt && (
                <button
                  onClick={async () => {
                    await api.acknowledgeImport(batch.id)
                    await onChanged()
                  }}
                  className="rounded-md bg-arc px-2.5 py-1 text-xs font-medium text-white transition hover:brightness-110"
                >
                  Looks right
                </button>
              )}
              <ConfirmButton
                onConfirm={async () => {
                  await api.removeImport(batch.id)
                  await onChanged()
                }}
                label="Remove these"
                title="Deletes the cards this import added. Your sold ledger and price history are untouched."
                confirm={
                  batch.modified > 0
                    ? `Remove ${batch.entries}, including ${batch.modified} you've changed`
                    : `Remove all ${batch.entries}`
                }
              />
            </div>
          </div>
        ))}
      </div>

      <p className="text-xs text-mute">
        Removing an import deletes only the cards it added, and only the ones still in your
        collection — anything you've since sold stays in the ledger, and price history is left alone.
      </p>
    </div>
  )
}
