import { useEffect, useRef, useState } from 'react'
import { api, cardImage, money } from '../api'
import {
  CONDITIONS,
  CONDITION_LABELS,
  LANGUAGES,
  LANGUAGE_LABELS,
  prettyVariant,
} from '../lib/cardStyles'
import { Modal } from './Modal'
import type {
  AddEntryRequest,
  CommitOutcome,
  CommitResult,
  ImportJob,
  ImportRow,
  ImportStatus,
} from '../types'

const control =
  'rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'

const STATUS_STYLE: Record<ImportStatus, { label: string; className: string }> = {
  Matched: { label: 'Matched', className: 'bg-mint/15 text-mint ring-mint/30' },
  Ambiguous: { label: 'Needs a choice', className: 'bg-gold/15 text-gold ring-gold/30' },
  Mismatch: { label: 'Check this one', className: 'bg-orange-500/15 text-orange-200 ring-orange-400/40' },
  NotFound: { label: 'Not found', className: 'bg-rose/15 text-rose ring-rose/30' },
  LookupFailed: { label: 'Lookup failed', className: 'bg-orange-500/15 text-orange-300 ring-orange-400/30' },
  Invalid: { label: 'Incomplete', className: 'bg-white/10 text-mute ring-white/20' },
}

/** Statuses that carry a card id and so can end up in the vault. */
const RESOLVABLE: ImportStatus[] = ['Matched', 'Ambiguous', 'Mismatch']

/** Statuses that offer a list to choose from rather than a single answer. */
const CHOOSABLE: ImportStatus[] = ['Ambiguous', 'Mismatch']

/**
 * How big the artwork is in the review list.
 *
 * This screen exists to be looked at — it is where a scan gets checked against the
 * card it resolved to — and a thumbnail too small to read the name off defeats the
 * purpose. Large is the default for that reason; small is there for skimming a long
 * file once you already trust the matching.
 *
 * Large is deliberately the size a card is drawn at in the vault's own grid (188x262
 * on a desktop screen), so a card being reviewed and the same card already owned are
 * the same object at the same size rather than two different-looking things.
 */
type CardSize = 'small' | 'medium' | 'large'

const CARD_SIZES: { key: CardSize; label: string; className: string }[] = [
  { key: 'small', label: 'Small', className: 'h-16' },
  { key: 'medium', label: 'Medium', className: 'h-40' },
  { key: 'large', label: 'Large', className: 'h-64' },
]

/** Local editing state layered over what the server resolved. */
interface RowEdit {
  include: boolean
  cardId: string | null
  quantity: number
  variant: string
  condition: string
  language: string
  variants: string[]
}

export function ImportView({ onImported }: { onImported: () => void }) {
  const [csv, setCsv] = useState('')
  const [job, setJob] = useState<ImportJob | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const [edits, setEdits] = useState<Record<number, RowEdit>>({})
  const [error, setError] = useState<string | null>(null)
  const [committing, setCommitting] = useState(false)
  const [results, setResults] = useState<Record<number, CommitOutcome>>({})
  const [lastCommit, setLastCommit] = useState<CommitResult | null>(null)
  const [filter, setFilter] = useState<ImportStatus | null>(null)
  const [dragging, setDragging] = useState(false)
  const [zoomed, setZoomed] = useState<{ cardId: string; name: string } | null>(null)

  // Remembered, like the vault's own view preferences: how closely you want to look
  // at a batch is a habit rather than something to re-pick on every import.
  const [cardSize, setCardSize] = useState<CardSize>(
    () => (localStorage.getItem('import.cardSize') as CardSize) ?? 'large',
  )

  useEffect(() => {
    localStorage.setItem('import.cardSize', cardSize)
  }, [cardSize])
  const fileRef = useRef<HTMLInputElement>(null)

  // Poll while the server resolves rows against the catalogue.
  useEffect(() => {
    if (!jobId) return
    let active = true

    const tick = async () => {
      try {
        const next = await api.importJob(jobId)
        if (!active) return
        setJob(next)
        if (next.state === 'resolving') setTimeout(tick, 700)
      } catch (e) {
        if (active) setError(e instanceof Error ? e.message : 'Lost track of the import')
      }
    }

    void tick()
    return () => {
      active = false
    }
  }, [jobId])

  // Seed the editable state once resolution finishes.
  useEffect(() => {
    if (job?.state !== 'ready') return
    setEdits((prev) => {
      if (Object.keys(prev).length > 0) return prev
      const seeded: Record<number, RowEdit> = {}
      for (const row of job.rows) {
        seeded[row.index] = {
          include: row.status === 'Matched',
          cardId: row.cardId ?? null,
          quantity: row.quantity,
          variant: row.variant,
          condition: row.condition,
          language: row.language,
          variants: row.variants ?? [],
        }
      }
      return seeded
    })
  }, [job])

  async function start(text: string) {
    setError(null)
    setResults({})
    setLastCommit(null)
    setFilter(null)
    setEdits({})
    setJob(null)
    try {
      const res = await api.startImport(text)
      setJobId(res.jobId)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not read that file')
    }
  }

  function readFile(file: File) {
    const reader = new FileReader()
    reader.onload = () => {
      const text = String(reader.result ?? '')
      setCsv(text)
      void start(text)
    }
    reader.readAsText(file)
  }

  function update(index: number, patch: Partial<RowEdit>) {
    setEdits((prev) => ({ ...prev, [index]: { ...prev[index], ...patch } }))
  }

  function pickCandidate(row: ImportRow, cardId: string) {
    const candidate = row.candidates.find((c) => c.cardId === cardId)
    if (!candidate) return
    update(row.index, {
      cardId,
      include: true,
      variants: candidate.variants,
      variant: candidate.variants.includes(edits[row.index]?.variant ?? '')
        ? edits[row.index].variant
        : (candidate.variants[0] ?? 'normal'),
    })
  }

  async function commit() {
    if (!jobId || !job) return
    setCommitting(true)
    setError(null)
    try {
      const rows: Array<AddEntryRequest & { index: number }> = job.rows
        .map((row) => ({ row, edit: edits[row.index] }))
        .filter(({ edit }) => edit?.include && edit.cardId)
        .map(({ row, edit }) => ({
          index: row.index,
          cardId: edit.cardId!,
          quantity: edit.quantity,
          variant: edit.variant,
          condition: edit.condition,
          language: edit.language,
          grade: row.grade ?? null,
          purchasePrice: row.purchasePrice ?? null,
          purchaseDate: row.purchaseDate ?? null,
          notes: row.notes ?? null,
        }))

      const res = await api.commitImport(jobId, rows)

      setResults((prev) => {
        const next = { ...prev }
        for (const outcome of res.rows) next[outcome.index] = outcome
        return next
      })

      // Untick whatever landed. The list deliberately stays on screen so the rows
      // that didn't can be worked through and committed again, and without this that
      // second press would add every successful card a second time.
      setEdits((prev) => {
        const next = { ...prev }
        for (const outcome of res.rows)
          if (outcome.added && next[outcome.index])
            next[outcome.index] = { ...next[outcome.index], include: false }
        return next
      })

      setLastCommit(res)
      onImported()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Import failed')
    } finally {
      setCommitting(false)
    }
  }

  function reset() {
    setCsv('')
    setJob(null)
    setJobId(null)
    setEdits({})
    setResults({})
    setLastCommit(null)
    setFilter(null)
    setError(null)
  }

  /**
   * The rows that never made it in, as a CSV to fix and feed back through.
   *
   * Emits what was transcribed rather than what we resolved to: for a mismatched row
   * the resolved card is the thing under suspicion, so handing it back would launder
   * the bad guess into the next attempt. The number is rebuilt as it was printed —
   * "45/094" — because the denominator is what identifies the set on re-import.
   */
  function downloadUnresolved() {
    if (!job) return

    const cell = (value: string | number | null | undefined) => {
      const text = value === null || value === undefined ? '' : String(value)
      return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
    }

    const lines = ['Name,Set,Number,Quantity,Condition,Language,Notes']
    for (const row of job.rows) {
      if (results[row.index]?.added) continue

      const number = row.claimedNumber ?? row.number ?? ''
      const printed = row.printedTotal ? `${number}/${row.printedTotal}` : number

      lines.push(
        [
          cell(row.claimedName ?? row.name),
          // Blanked when we overwrote it with the suspect card's set.
          cell(row.status === 'Mismatch' ? '' : row.setName),
          cell(printed),
          cell(row.quantity),
          cell(row.condition),
          cell(row.language),
          cell(results[row.index]?.reason ?? row.message),
        ].join(','),
      )
    }

    const url = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv' }))
    const link = document.createElement('a')
    link.href = url
    link.download = 'cardvault-unresolved.csv'
    link.click()
    URL.revokeObjectURL(url)
  }

  const selectedCount = job?.rows.filter((r) => edits[r.index]?.include && edits[r.index]?.cardId).length ?? 0
  const counts = job?.rows.reduce<Record<string, number>>((acc, r) => {
    acc[r.status] = (acc[r.status] ?? 0) + 1
    return acc
  }, {})
  const visibleRows = job?.rows.filter((r) => filter === null || r.status === filter) ?? []
  const unresolvedCount = job?.rows.filter((r) => !results[r.index]?.added).length ?? 0

  // ------------------------------------------------------------------- resolving

  if (job?.state === 'resolving') {
    const pct = job.total === 0 ? 0 : Math.round((job.processed / job.total) * 100)
    return (
      <div className="panel rounded-2xl px-6 py-16 text-center">
        <p className="text-lg text-bright">Matching your cards against the catalogue…</p>
        <p className="mt-1 text-sm text-mute">
          {job.processed} of {job.total} rows
        </p>
        <div className="mx-auto mt-6 h-2 w-full max-w-md overflow-hidden rounded-full bg-abyss">
          <div className="h-full rounded-full bg-arc transition-[width] duration-300" style={{ width: `${pct}%` }} />
        </div>
        <p className="mt-6 text-xs text-mute">
          Rows resolve instantly from your offline catalogue and from cards you already hold.
          Anything neither knows about is looked up one at a time, to stay inside the API's
          rate limit.
        </p>
      </div>
    )
  }

  // ----------------------------------------------------------------------- review

  if (job && (job.state === 'ready' || job.state === 'failed') && job.rows.length > 0) {
    return (
      <div className="space-y-5">
        <div className="panel flex flex-wrap items-center justify-between gap-4 rounded-xl px-4 py-3">
          <div className="flex flex-wrap items-center gap-2 text-sm">
            <button
              onClick={() => setFilter(null)}
              className={`rounded-full px-2.5 py-1 text-xs ring-1 transition ${
                filter === null ? 'bg-white/10 text-bright ring-white/25' : 'text-mute ring-transparent hover:text-bright'
              }`}
            >
              All {job.rows.length}
            </button>
            {counts &&
              Object.entries(counts).map(([status, n]) => (
                <button
                  key={status}
                  onClick={() => setFilter(filter === status ? null : (status as ImportStatus))}
                  className={`rounded-full px-2.5 py-1 text-xs ring-1 transition ${
                    STATUS_STYLE[status as ImportStatus].className
                  } ${filter === status ? 'brightness-150' : 'opacity-70 hover:opacity-100'}`}
                >
                  {n} {STATUS_STYLE[status as ImportStatus].label.toLowerCase()}
                </button>
              ))}
          </div>
          <div className="flex items-center gap-2">
            <div className="flex items-center rounded-lg border border-edge p-0.5 text-xs">
              {CARD_SIZES.map((s) => (
                <button
                  key={s.key}
                  onClick={() => setCardSize(s.key)}
                  title={`${s.label} artwork`}
                  className={`rounded-md px-2 py-1 transition ${
                    cardSize === s.key ? 'bg-arc text-white' : 'text-mute hover:text-bright'
                  }`}
                >
                  {s.label}
                </button>
              ))}
            </div>
            <button onClick={reset} className="rounded-lg px-3 py-2 text-sm text-mute transition hover:bg-white/5 hover:text-bright">
              Start over
            </button>
            <button
              onClick={commit}
              disabled={committing || selectedCount === 0}
              className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
            >
              {committing ? 'Adding…' : `Add ${selectedCount} to vault`}
            </button>
          </div>
        </div>

        {lastCommit && (
          <div className="panel flex flex-wrap items-center justify-between gap-3 rounded-xl px-4 py-3">
            <p className="text-sm">
              <span className="font-medium text-mint">
                {lastCommit.added} {lastCommit.added === 1 ? 'card' : 'cards'} added
              </span>
              {unresolvedCount > 0 && (
                <span className="text-mute"> · {unresolvedCount} still to deal with</span>
              )}
            </p>
            <div className="flex items-center gap-2">
              {unresolvedCount > 0 && (
                <button
                  onClick={downloadUnresolved}
                  className="rounded-lg border border-edge px-3 py-1.5 text-xs text-mute transition hover:text-bright"
                >
                  Download the {unresolvedCount} unresolved
                </button>
              )}
              <button
                onClick={reset}
                className="rounded-lg bg-arc px-3 py-1.5 text-xs font-medium text-white transition hover:brightness-110"
              >
                Import another file
              </button>
            </div>
          </div>
        )}

        {job.error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{job.error}</p>}
        {error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>}
        {job.unmappedColumns.length > 0 && (
          <p className="text-xs text-mute">
            Ignored unrecognised columns: {job.unmappedColumns.join(', ')}
          </p>
        )}

        <div className="space-y-2">
          {visibleRows.map((row) => (
            <ImportRowCard
              key={row.index}
              row={row}
              edit={edits[row.index]}
              outcome={results[row.index]}
              cardSize={cardSize}
              onZoom={(cardId, name) => setZoomed({ cardId, name })}
              onChange={(patch) => update(row.index, patch)}
              onPickCandidate={(cardId) => pickCandidate(row, cardId)}
            />
          ))}
          {visibleRows.length === 0 && (
            <p className="panel rounded-xl px-4 py-8 text-center text-sm text-mute">
              No rows with that status.
            </p>
          )}
        </div>

        {/* Full-size art, for when even the large thumbnail leaves it in doubt —
            a wrong art variant of the right card is the one mistake the row text
            cannot tell you about. */}
        {zoomed && (
          <Modal onClose={() => setZoomed(null)}>
            <div className="p-4">
              <img
                src={cardImage(zoomed.cardId, 'large')}
                alt={zoomed.name}
                className="mx-auto w-full rounded-lg"
              />
              <p className="mt-3 text-center text-sm text-bright">{zoomed.name}</p>
              <p className="text-center text-xs text-mute">{zoomed.cardId}</p>
            </div>
          </Modal>
        )}
      </div>
    )
  }

  // -------------------------------------------------------------------- upload

  return (
    <div className="space-y-5">
      <div
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          const file = e.dataTransfer.files[0]
          if (file) readFile(file)
        }}
        className={`panel rounded-2xl border-2 border-dashed px-6 py-14 text-center transition ${
          dragging ? 'border-arc bg-arc/5' : 'border-edge'
        }`}
      >
        <p className="text-lg text-bright">Drop a CSV of your collection here</p>
        <p className="mx-auto mt-2 max-w-lg text-sm text-mute">
          Each row becomes a card in your vault. Nothing is saved until you review the matches.
        </p>
        <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
          <button
            onClick={() => fileRef.current?.click()}
            className="rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
          >
            Choose a file
          </button>
          <a
            href="/api/import/template"
            className="rounded-lg border border-edge px-5 py-2.5 text-sm text-mute transition hover:text-bright"
          >
            Download a template
          </a>
        </div>
        <input
          ref={fileRef}
          type="file"
          accept=".csv,.tsv,.txt,text/csv"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0]
            if (file) readFile(file)
          }}
        />
      </div>

      {error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>}

      <div className="panel rounded-xl p-4">
        <h3 className="text-[11px] tracking-wider text-mute uppercase">Or paste it directly</h3>
        <textarea
          value={csv}
          onChange={(e) => setCsv(e.target.value)}
          rows={6}
          placeholder={'Name,Set,Number,Quantity,Variant,Condition,Purchase Price\nCharizard,Base,4,1,Holofoil,NM,250.00'}
          className="mt-2 w-full rounded-lg border border-edge bg-abyss p-3 font-mono text-xs text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc"
        />
        <button
          onClick={() => csv.trim() && start(csv)}
          disabled={!csv.trim()}
          className="mt-3 rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
        >
          Match these cards
        </button>
      </div>

      <div className="panel rounded-xl p-4 text-sm">
        <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">Recognised columns</h3>
        <p className="text-mute">
          Column names are matched loosely, so most exports work unchanged. Include either a{' '}
          <strong className="text-bright">card name</strong> or a{' '}
          <strong className="text-bright">set and number</strong> — everything else is optional.
        </p>
        <ul className="mt-3 grid gap-x-6 gap-y-1 text-xs text-mute sm:grid-cols-2">
          <li>
            <span className="text-bright">Name</span> — card name, product name
          </li>
          <li>
            <span className="text-bright">Set</span> — set, edition, expansion
          </li>
          <li>
            <span className="text-bright">Number</span> — card number, collector number, #
          </li>
          <li>
            <span className="text-bright">Quantity</span> — qty, count, copies
          </li>
          <li>
            <span className="text-bright">Variant</span> — printing, finish, foil
          </li>
          <li>
            <span className="text-bright">Condition</span> — NM, Lightly Played, Excellent…
          </li>
          <li>
            <span className="text-bright">Language</span> — lang, locale. Left out means English.
          </li>
          <li>
            <span className="text-bright">Purchase Price</span> — paid, cost, buy price
          </li>
          <li>
            <span className="text-bright">Purchase Date</span> — acquired, date added
          </li>
          <li>
            <span className="text-bright">Card ID</span> — a pokemontcg.io id like <code>base1-4</code>.
            Include a name or number alongside it and the two are checked against each other,
            so an id that points at the wrong card gets flagged rather than imported.
          </li>
          <li>
            <span className="text-bright">Notes</span> — note, comment
          </li>
        </ul>
      </div>
    </div>
  )
}

function ImportRowCard({
  row,
  edit,
  outcome,
  cardSize,
  onZoom,
  onChange,
  onPickCandidate,
}: {
  row: ImportRow
  edit?: RowEdit
  outcome?: CommitOutcome
  cardSize: CardSize
  onZoom: (cardId: string, name: string) => void
  onChange: (patch: Partial<RowEdit>) => void
  onPickCandidate: (cardId: string) => void
}) {
  const style = STATUS_STYLE[row.status]
  const resolvable = RESOLVABLE.includes(row.status)
  const variants = edit?.variants?.length ? edit.variants : row.variants
  const added = outcome?.added === true

  // Once a different card has been chosen, the row describes that one. Without this
  // the picture swaps but the heading doesn't, so confirming a mismatched row leaves
  // it still captioned with the name you just rejected.
  const chosen =
    edit?.cardId && edit.cardId !== row.cardId
      ? row.candidates.find((c) => c.cardId === edit.cardId)
      : undefined
  const shownName = chosen?.name ?? row.name
  const shownSetName = chosen?.setName ?? row.setName
  const shownNumber = chosen?.number ?? row.number
  const sizeClass = CARD_SIZES.find((s) => s.key === cardSize)?.className ?? 'h-64'

  return (
    <div
      className={`panel rounded-xl p-3 transition ${added ? 'opacity-50' : edit?.include ? '' : 'opacity-60'} ${
        resolvable ? '' : 'border-dashed'
      } ${row.status === 'Mismatch' && !added ? 'ring-1 ring-orange-400/40' : ''}`}
    >
      <div className="flex flex-wrap items-start gap-3">
        {resolvable && !added && (
          <input
            type="checkbox"
            checked={edit?.include ?? false}
            disabled={!edit?.cardId}
            onChange={(e) => onChange({ include: e.target.checked })}
            aria-label={`Include ${shownName ?? 'row'}`}
            className="mt-1 h-4 w-4 shrink-0 accent-[color:var(--color-arc)]"
          />
        )}

        {edit?.cardId ? (
          <button
            type="button"
            onClick={() => onZoom(edit.cardId!, shownName ?? row.source)}
            title="See the full-size artwork"
            className="shrink-0 rounded focus:ring-2 focus:ring-arc focus:outline-none"
          >
            <img
              src={cardImage(edit.cardId, 'small')}
              alt=""
              className={`${sizeClass} w-auto rounded ring-1 ring-white/10 transition hover:ring-arc/60`}
              onError={(e) => {
                if (row.imageSmall) (e.currentTarget as HTMLImageElement).src = row.imageSmall
              }}
            />
          </button>
        ) : (
          <div className={`${sizeClass} aspect-[245/342] shrink-0 rounded bg-abyss ring-1 ring-white/5`} />
        )}

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={`rounded-full px-2 py-0.5 text-[11px] ring-1 ${
                added ? 'bg-mint/15 text-mint ring-mint/30' : style.className
              }`}
            >
              {added ? 'Added' : style.label}
            </span>
            <span className="truncate font-medium text-bright">{shownName ?? '—'}</span>
            {shownSetName && (
              <span className="truncate text-xs text-mute">
                {shownSetName} · #{shownNumber}
              </span>
            )}
            {row.marketPrice != null && (
              <span className="text-xs text-gold tabular-nums">{money(row.marketPrice)}</span>
            )}
          </div>

          {/*
            Both readings side by side, because the whole point of a mismatch is that
            one of them is wrong and no amount of prose beats seeing them together
            next to the artwork.
          */}
          {row.status === 'Mismatch' && !added && (
            <p className="mt-1 text-xs">
              <span className="text-mute">your file read </span>
              <span className="text-orange-200">
                {row.claimedName ?? '—'}
                {row.claimedNumber ? ` #${row.claimedNumber}` : ''}
              </span>
              <span className="text-mute"> · this id is </span>
              <span className="text-bright">
                {row.name}
                {row.number ? ` #${row.number}` : ''}
              </span>
            </p>
          )}

          {outcome && !outcome.added && outcome.reason && (
            <p className="mt-1 text-xs text-rose">{outcome.reason}</p>
          )}
          {row.message && !added && <p className="mt-1 text-xs text-mute">{row.message}</p>}
          {!resolvable && <p className="mt-1 truncate text-xs text-mute italic">{row.source}</p>}

          {CHOOSABLE.includes(row.status) && !added && (
            <select
              value={edit?.cardId ?? ''}
              onChange={(e) => onPickCandidate(e.target.value)}
              className={`${control} mt-2 w-full max-w-md`}
            >
              <option value="">
                {row.status === 'Mismatch' ? 'Confirm which card this is…' : 'Choose the right card…'}
              </option>
              {row.candidates.map((c) => (
                <option key={c.cardId} value={c.cardId}>
                  {c.name} — {c.setName} #{c.number}
                  {c.rarity ? ` (${c.rarity})` : ''}
                  {c.marketPrice != null ? ` — ${money(c.marketPrice)}` : ''}
                </option>
              ))}
            </select>
          )}

          {resolvable && edit?.cardId && !added && (
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <label className="flex items-center gap-1 text-xs text-mute">
                Qty
                <input
                  type="number"
                  min={1}
                  value={edit.quantity}
                  onChange={(e) => onChange({ quantity: Math.max(1, Number(e.target.value) || 1) })}
                  className={`${control} w-16`}
                />
              </label>

              {variants.length > 0 && (
                <select
                  value={edit.variant}
                  onChange={(e) => onChange({ variant: e.target.value })}
                  className={control}
                >
                  {variants.map((v) => (
                    <option key={v} value={v}>
                      {prettyVariant(v)}
                    </option>
                  ))}
                </select>
              )}

              <select
                value={edit.condition}
                onChange={(e) => onChange({ condition: e.target.value })}
                className={control}
              >
                {CONDITIONS.map((c) => (
                  <option key={c} value={c}>
                    {CONDITION_LABELS[c]}
                  </option>
                ))}
              </select>

              <select
                value={edit.language}
                onChange={(e) => onChange({ language: e.target.value })}
                className={control}
              >
                {LANGUAGES.map((l) => (
                  <option key={l} value={l}>
                    {LANGUAGE_LABELS[l]}
                  </option>
                ))}
              </select>

              {row.purchasePrice != null && (
                <span className="text-xs text-mute">paid {money(row.purchasePrice)}</span>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
