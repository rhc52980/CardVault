import { useEffect, useRef, useState } from 'react'
import { api, cardImage, money } from '../api'
import { CONDITIONS, CONDITION_LABELS, prettyVariant } from '../lib/cardStyles'
import type { AddEntryRequest, ImportJob, ImportRow, ImportStatus } from '../types'

const control =
  'rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'

const STATUS_STYLE: Record<ImportStatus, { label: string; className: string }> = {
  Matched: { label: 'Matched', className: 'bg-mint/15 text-mint ring-mint/30' },
  Ambiguous: { label: 'Needs a choice', className: 'bg-gold/15 text-gold ring-gold/30' },
  NotFound: { label: 'Not found', className: 'bg-rose/15 text-rose ring-rose/30' },
  LookupFailed: { label: 'Lookup failed', className: 'bg-orange-500/15 text-orange-300 ring-orange-400/30' },
  Invalid: { label: 'Incomplete', className: 'bg-white/10 text-mute ring-white/20' },
}

/** Local editing state layered over what the server resolved. */
interface RowEdit {
  include: boolean
  cardId: string | null
  quantity: number
  variant: string
  condition: string
  variants: string[]
}

export function ImportView({ onImported }: { onImported: () => void }) {
  const [csv, setCsv] = useState('')
  const [job, setJob] = useState<ImportJob | null>(null)
  const [jobId, setJobId] = useState<string | null>(null)
  const [edits, setEdits] = useState<Record<number, RowEdit>>({})
  const [error, setError] = useState<string | null>(null)
  const [committing, setCommitting] = useState(false)
  const [committed, setCommitted] = useState<number | null>(null)
  const [dragging, setDragging] = useState(false)
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
          variants: row.variants ?? [],
        }
      }
      return seeded
    })
  }, [job])

  async function start(text: string) {
    setError(null)
    setCommitted(null)
    setEdits({})
    setJob(null)
    try {
      const res = await api.startImport(text)
      setJobId(res.jobId)
    } catch (e) {
      const message = e instanceof Error ? e.message : 'Could not read that file'
      try {
        setError(JSON.parse(message).error ?? message)
      } catch {
        setError(message)
      }
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
      const rows: AddEntryRequest[] = job.rows
        .map((row) => ({ row, edit: edits[row.index] }))
        .filter(({ edit }) => edit?.include && edit.cardId)
        .map(({ row, edit }) => ({
          cardId: edit.cardId!,
          quantity: edit.quantity,
          variant: edit.variant,
          condition: edit.condition,
          grade: row.grade ?? null,
          purchasePrice: row.purchasePrice ?? null,
          purchaseDate: row.purchaseDate ?? null,
          notes: row.notes ?? null,
        }))

      const res = await api.commitImport(jobId, rows)
      setCommitted(res.added)
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
    setCommitted(null)
    setError(null)
  }

  const selectedCount = job?.rows.filter((r) => edits[r.index]?.include && edits[r.index]?.cardId).length ?? 0
  const counts = job?.rows.reduce<Record<string, number>>((acc, r) => {
    acc[r.status] = (acc[r.status] ?? 0) + 1
    return acc
  }, {})

  // ------------------------------------------------------------------ committed

  if (committed !== null) {
    return (
      <div className="panel rounded-2xl px-6 py-16 text-center">
        <p className="text-2xl font-semibold text-mint">
          {committed} {committed === 1 ? 'card' : 'cards'} added
        </p>
        <p className="mt-2 text-sm text-mute">Your vault has been updated.</p>
        <button
          onClick={reset}
          className="mt-6 rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
        >
          Import another file
        </button>
      </div>
    )
  }

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
          Rows already in your local cache resolve instantly; the rest are looked up one at a time
          to stay inside the API's rate limit.
        </p>
      </div>
    )
  }

  // ----------------------------------------------------------------------- review

  if (job && (job.state === 'ready' || job.state === 'failed') && job.rows.length > 0) {
    return (
      <div className="space-y-5">
        <div className="panel flex flex-wrap items-center justify-between gap-4 rounded-xl px-4 py-3">
          <div className="flex flex-wrap items-center gap-3 text-sm">
            <span className="text-bright">{job.rows.length} rows</span>
            {counts &&
              Object.entries(counts).map(([status, n]) => (
                <span
                  key={status}
                  className={`rounded-full px-2.5 py-1 text-xs ring-1 ${STATUS_STYLE[status as ImportStatus].className}`}
                >
                  {n} {STATUS_STYLE[status as ImportStatus].label.toLowerCase()}
                </span>
              ))}
          </div>
          <div className="flex items-center gap-2">
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

        {job.error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{job.error}</p>}
        {error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>}
        {job.unmappedColumns.length > 0 && (
          <p className="text-xs text-mute">
            Ignored unrecognised columns: {job.unmappedColumns.join(', ')}
          </p>
        )}

        <div className="space-y-2">
          {job.rows.map((row) => (
            <ImportRowCard
              key={row.index}
              row={row}
              edit={edits[row.index]}
              onChange={(patch) => update(row.index, patch)}
              onPickCandidate={(cardId) => pickCandidate(row, cardId)}
            />
          ))}
        </div>
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
            <span className="text-bright">Purchase Price</span> — paid, cost, buy price
          </li>
          <li>
            <span className="text-bright">Purchase Date</span> — acquired, date added
          </li>
          <li>
            <span className="text-bright">Card ID</span> — a pokemontcg.io id like <code>base1-4</code>
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
  onChange,
  onPickCandidate,
}: {
  row: ImportRow
  edit?: RowEdit
  onChange: (patch: Partial<RowEdit>) => void
  onPickCandidate: (cardId: string) => void
}) {
  const style = STATUS_STYLE[row.status]
  const resolvable = row.status === 'Matched' || row.status === 'Ambiguous'
  const variants = edit?.variants?.length ? edit.variants : row.variants

  return (
    <div
      className={`panel rounded-xl p-3 transition ${edit?.include ? '' : 'opacity-60'} ${
        resolvable ? '' : 'border-dashed'
      }`}
    >
      <div className="flex flex-wrap items-start gap-3">
        {resolvable && (
          <input
            type="checkbox"
            checked={edit?.include ?? false}
            disabled={!edit?.cardId}
            onChange={(e) => onChange({ include: e.target.checked })}
            aria-label={`Include ${row.name ?? 'row'}`}
            className="mt-1 h-4 w-4 shrink-0 accent-[color:var(--color-arc)]"
          />
        )}

        {edit?.cardId ? (
          <img
            src={cardImage(edit.cardId, 'small')}
            alt=""
            className="h-16 w-auto shrink-0 rounded ring-1 ring-white/10"
            onError={(e) => {
              if (row.imageSmall) (e.currentTarget as HTMLImageElement).src = row.imageSmall
            }}
          />
        ) : (
          <div className="h-16 w-[46px] shrink-0 rounded bg-abyss ring-1 ring-white/5" />
        )}

        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className={`rounded-full px-2 py-0.5 text-[11px] ring-1 ${style.className}`}>{style.label}</span>
            <span className="truncate font-medium text-bright">{row.name ?? '—'}</span>
            {row.setName && (
              <span className="truncate text-xs text-mute">
                {row.setName} · #{row.number}
              </span>
            )}
            {row.marketPrice != null && (
              <span className="text-xs text-gold tabular-nums">{money(row.marketPrice)}</span>
            )}
          </div>

          {row.message && <p className="mt-1 text-xs text-mute">{row.message}</p>}
          {!resolvable && <p className="mt-1 truncate text-xs text-mute italic">{row.source}</p>}

          {row.status === 'Ambiguous' && (
            <select
              value={edit?.cardId ?? ''}
              onChange={(e) => onPickCandidate(e.target.value)}
              className={`${control} mt-2 w-full max-w-md`}
            >
              <option value="">Choose the right card…</option>
              {row.candidates.map((c) => (
                <option key={c.cardId} value={c.cardId}>
                  {c.name} — {c.setName} #{c.number}
                  {c.rarity ? ` (${c.rarity})` : ''}
                  {c.marketPrice != null ? ` — ${money(c.marketPrice)}` : ''}
                </option>
              ))}
            </select>
          )}

          {resolvable && edit?.cardId && (
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
