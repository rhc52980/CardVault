import { useRef, useState } from 'react'
import { api } from '../api'
import type { ImportBatchSummary, MissingScan, ReconcileReport } from '../types'
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
      <ReconcileCard onChanged={onChanged} />
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

/**
 * Checks the vault against a fresher read of the scans it came from.
 *
 * Scanning improves — OCR gets corrected, a set symbol finally gets identified — and
 * the CSVs move on while the collection stays as it was imported. This says where the
 * two have drifted apart and does nothing else: no card, price, set or quantity is
 * ever changed, only marked.
 *
 * Dry by default, and the button that writes says so, because "compare" and "write to
 * every entry in my collection" should not be the same click.
 */
/**
 * The scanned-but-missing rows, written in the columns the importer already reads.
 *
 * Built here rather than served, because the report is the only place these exist —
 * nothing about them is stored, so there is nothing for an endpoint to fetch. It is
 * the same trick the import review uses for its leftovers.
 */
function downloadMissing(missing: MissingScan[]) {
  const cell = (v: string | null | undefined) => {
    const t = v ?? ''
    return /[",\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t
  }

  const lines = ['Name,Set,Number,Quantity,Notes']
  for (const m of missing) {
    lines.push([
      cell(m.name),
      cell(m.setName),
      cell(m.number),
      '1',
      // Which scan it came from, so a row you can't place is still traceable to
      // the image of the actual card.
      cell(`${m.scanFile} · ${m.file}`),
    ].join(','))
  }

  const url = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv' }))
  const a = document.createElement('a')
  a.href = url
  a.download = 'scanned-but-not-in-vault.csv'
  a.click()
  URL.revokeObjectURL(url)
}

function ReconcileCard({ onChanged }: { onChanged: () => Promise<void> | void }) {
  const input = useRef<HTMLInputElement>(null)
  const [files, setFiles] = useState<File[]>([])
  const [report, setReport] = useState<ReconcileReport | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function run(apply: boolean) {
    if (files.length === 0) return
    setBusy(true)
    setError(null)
    try {
      setReport(await api.reconcile(files, apply))
      if (apply) await onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not compare those files')
    } finally {
      setBusy(false)
    }
  }

  async function clear() {
    setBusy(true)
    try {
      await api.clearFlags()
      setReport(null)
      await onChanged()
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">Check against your scans</h2>
      <p className="mt-1 text-sm text-mute">
        Give it the <code>Batch_N_cards.csv</code> files and it will say where a fresher read
        of your scans disagrees with what was imported. It matches each batch to its file by
        aligning the two in order, so rows that never imported are allowed for.{' '}
        <span className="text-bright">Nothing is corrected</span> — disagreements are marked
        for review and the cards stay exactly as they are.
      </p>

      <input
        ref={input}
        type="file"
        accept=".csv,.tsv,.txt"
        multiple
        className="hidden"
        onChange={(e) => {
          setFiles([...(e.target.files ?? [])])
          setReport(null)
          e.target.value = ''
        }}
      />

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <button
          onClick={() => input.current?.click()}
          disabled={busy}
          className="rounded-lg border border-edge px-3 py-1.5 text-sm text-mute transition hover:border-arc/60 hover:text-bright disabled:opacity-40"
        >
          Choose CSVs
        </button>
        {files.length > 0 && (
          <>
            <span className="text-xs text-mute">{files.length} selected</span>
            <button
              onClick={() => run(false)}
              disabled={busy}
              className="rounded-lg bg-arc px-3 py-1.5 text-sm text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Compare
            </button>
          </>
        )}
      </div>

      {error && <div className="mt-2 text-sm text-rose">{error}</div>}

      {report && (
        <div className="mt-3 space-y-2">
          <div className="rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
            <span className="text-mint">{report.agreed.toLocaleString()} agree</span>
            {' · '}
            <span className={report.disagreed ? 'text-gold' : 'text-mute'}>
              {report.disagreed.toLocaleString()} disagree
            </span>
            {' · '}
            <span className="text-mute">
              {report.neverImported.toLocaleString()} scanned rows never imported
            </span>
            {report.missing.length > 0 && (
              <div className="mt-1.5">
                <button
                  onClick={() => downloadMissing(report.missing)}
                  className="text-xs text-arc transition hover:underline"
                >
                  Download those {report.missing.length.toLocaleString()} as a CSV
                </button>
                <span className="ml-2 text-[11px] text-mute">
                  — cards you scanned that aren't in the vault. Import it to add them.
                </span>
              </div>
            )}
            {report.unmatchedFiles.length > 0 && (
              <div className="mt-1 text-xs text-mute">
                {report.unmatchedFiles.length} file(s) matched no import:{' '}
                {report.unmatchedFiles.slice(0, 3).join(', ')}
                {report.unmatchedFiles.length > 3 && '…'}
              </div>
            )}
          </div>

          {report.applied ? (
            <div className="flex flex-wrap items-center gap-2 text-sm text-mint">
              Marked {report.disagreed.toLocaleString()} for review — find them under{' '}
              <span className="text-bright">Needs review</span> in the vault.
              <ConfirmButton label="Clear all marks" confirm="Really clear" onConfirm={clear} />
            </div>
          ) : (
            report.disagreed > 0 && (
              <button
                onClick={() => run(true)}
                disabled={busy}
                className="rounded-lg bg-gold px-3 py-1.5 text-sm font-medium text-black transition hover:brightness-110 disabled:opacity-40"
              >
                Mark those {report.disagreed.toLocaleString()} for review
              </button>
            )
          )}
        </div>
      )}
    </section>
  )
}
