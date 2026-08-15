import { useEffect, useMemo, useState } from 'react'
import { api, cardImage, money } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { CollectionItem, ImportBatchSummary } from '../types'
import { CardDetail } from './CardDetail'
import { CardTile } from './CardTile'
import { ConfirmButton } from './ConfirmButton'
import { ManualEntryDialog } from './ManualEntryDialog'
import { SoldView } from './SoldView'
import { WantedView } from './WantedView'

type SortKey = 'value' | 'name' | 'set' | 'added' | 'rarity'

const SORTS: { key: SortKey; label: string }[] = [
  { key: 'value', label: 'Value' },
  { key: 'name', label: 'Name' },
  { key: 'set', label: 'Set' },
  { key: 'added', label: 'Recently added' },
  { key: 'rarity', label: 'Rarity' },
]

const control =
  'rounded-lg border border-edge bg-surface px-3 py-2 text-sm text-bright focus:border-arc focus:ring-1 focus:ring-arc focus:outline-none'

export function CollectionView({
  items,
  loading,
  onChanged,
  onGoToSearch,
}: {
  items: CollectionItem[]
  loading: boolean
  onChanged: () => void
  onGoToSearch: () => void
}) {
  const [filter, setFilter] = useState('')
  const [setFilter_, setSetFilter] = useState('')
  const [sort, setSort] = useState<SortKey>('value')
  const [detailCardId, setDetailCardId] = useState<string | null>(null)
  const [manualOpen, setManualOpen] = useState(false)
  // Owned and sold are two views of the same collection, so they share a screen
  // rather than eating another slot in the top nav.
  const [pane, setPane] = useState<'owned' | 'wanted' | 'sold'>('owned')
  const [locationFilter, setLocationFilter] = useState('')
  const [batchFilter, setBatchFilter] = useState('')
  const [batches, setBatches] = useState<ImportBatchSummary[]>([])

  // Reloaded whenever the collection is, which covers an import having just added
  // cards without needing to be told about it separately.
  useEffect(() => {
    let live = true
    api.importBatches()
      .then((b) => live && setBatches(b))
      .catch(() => live && setBatches([]))
    return () => {
      live = false
    }
  }, [items])

  const unreviewed = useMemo(() => batches.filter((b) => !b.acknowledgedAt), [batches])
  const unreviewedIds = useMemo(() => new Set(unreviewed.map((b) => b.id)), [unreviewed])

  async function acknowledge(id: string) {
    await api.acknowledgeImport(id)
    setBatches(await api.importBatches())
  }

  async function removeBatch(id: string) {
    await api.removeImport(id)
    if (batchFilter === id) setBatchFilter('')
    // The cards are gone, so the collection itself has to be re-read; that in turn
    // re-runs the effect above and refreshes the batch list.
    onChanged()
  }

  /**
   * Whether hand-entered things share the grid with the cards.
   *
   * Sealed boxes, slabs and one-off promos are a different kind of object from a
   * card — you browse them for different reasons, and a handful of them scattered
   * through hundreds of singles is mostly noise. Cards-only is the default for
   * that reason; the choice is remembered, because it is a preference rather than
   * something to re-pick on every visit.
   */
  const [kind, setKind] = useState<'cards' | 'hand' | 'all'>(
    () => (localStorage.getItem('vault.kind') as 'cards' | 'hand' | 'all') ?? 'cards',
  )

  useEffect(() => {
    localStorage.setItem('vault.kind', kind)
  }, [kind])

  const counts = useMemo(
    () => ({
      cards: items.filter((i) => !i.isCustom).length,
      hand: items.filter((i) => i.isCustom).length,
      all: items.length,
    }),
    [items],
  )

  const sets = useMemo(() => {
    const seen = new Map<string, string>()
    for (const i of items) if (i.setId && i.setName) seen.set(i.setId, i.setName)
    return [...seen.entries()].sort((a, b) => a[1].localeCompare(b[1]))
  }, [items])

  const locations = useMemo(
    () => [...new Set(items.map((i) => i.location).filter((l): l is string => !!l))].sort(),
    [items],
  )

  const visible = useMemo(() => {
    const term = filter.trim().toLowerCase()
    let out = items

    if (term) {
      out = out.filter(
        (i) =>
          i.name.toLowerCase().includes(term) ||
          i.setName?.toLowerCase().includes(term) ||
          i.rarity?.toLowerCase().includes(term) ||
          i.number?.toLowerCase().includes(term) ||
          i.location?.toLowerCase().includes(term),
      )
    }

    if (kind === 'cards') out = out.filter((i) => !i.isCustom)
    else if (kind === 'hand') out = out.filter((i) => i.isCustom)

    if (setFilter_) out = out.filter((i) => i.setId === setFilter_)
    if (batchFilter) out = out.filter((i) => i.importBatch === batchFilter)
    if (locationFilter) {
      out =
        locationFilter === '__none__'
          ? out.filter((i) => !i.location)
          : out.filter((i) => i.location === locationFilter)
    }

    const sorted = [...out]
    sorted.sort((a, b) => {
      switch (sort) {
        case 'name':
          return a.name.localeCompare(b.name)
        case 'set':
          return (a.setName ?? '').localeCompare(b.setName ?? '') || (a.number ?? '').localeCompare(b.number ?? '')
        case 'added':
          return b.addedAt.localeCompare(a.addedAt)
        case 'rarity':
          return (a.rarity ?? 'zzz').localeCompare(b.rarity ?? 'zzz')
        case 'value':
        default:
          return (b.lineValue ?? 0) - (a.lineValue ?? 0)
      }
    })
    return sorted
  }, [items, filter, setFilter_, locationFilter, batchFilter, sort, kind])

  const ownedForDetail = detailCardId ? items.filter((i) => i.cardId === detailCardId) : []
  const visibleValue = visible.reduce((sum, i) => sum + (i.lineValue ?? 0), 0)

  if (loading) {
    return (
      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
        {Array.from({ length: 12 }).map((_, i) => (
          <div key={i} className="skeleton aspect-[245/342] rounded-xl" />
        ))}
      </div>
    )
  }

  // Owned/sold switch and export sit above everything, so sold history stays
  // reachable even after you've sold the last card in your vault.
  const header = (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="flex rounded-lg border border-edge bg-surface p-1 text-sm">
        {(
          [
            ['owned', 'Owned'],
            ['wanted', 'Wanted'],
            ['sold', 'Sold'],
          ] as const
        ).map(([key, text]) => (
          <button
            key={key}
            onClick={() => setPane(key)}
            className={`rounded-md px-4 py-1.5 transition ${
              pane === key ? 'bg-arc text-white' : 'text-mute hover:text-bright'
            }`}
          >
            {text}
          </button>
        ))}
      </div>

      <div className="flex items-center gap-2 text-sm">
        <span className="text-xs text-mute">Export</span>
        <a
          href="/api/export/collection.csv"
          download
          className="rounded-lg border border-edge px-3 py-2 text-mute transition hover:border-arc/60 hover:text-bright"
          title="Spreadsheet-friendly, and can be imported straight back in"
        >
          Collection CSV
        </a>
        <a
          href="/api/export/sales.csv"
          download
          className="rounded-lg border border-edge px-3 py-2 text-mute transition hover:border-arc/60 hover:text-bright"
        >
          Sales CSV
        </a>
        <a
          href="/api/export/vault.json"
          download
          className="rounded-lg border border-edge px-3 py-2 text-mute transition hover:border-arc/60 hover:text-bright"
          title="Everything, including hand-entered items and sales history"
        >
          JSON
        </a>
        <a
          href="/api/export/deck-inventory.json"
          download
          className="rounded-lg border border-edge px-3 py-2 text-mute transition hover:border-arc/60 hover:text-bright"
          title="Your playable cards with energy costs, evolution lines and format legality — the file to hand an AI for deck building"
        >
          Deck inventory
        </a>
      </div>
    </div>
  )

  if (pane === 'sold') {
    return (
      <div className="space-y-5">
        {header}
        <SoldView onChanged={onChanged} />
      </div>
    )
  }

  if (pane === 'wanted') {
    return (
      <div className="space-y-5">
        {header}
        <WantedView onChanged={onChanged} onGoToSearch={onGoToSearch} />
      </div>
    )
  }

  if (items.length === 0) {
    return (
      <div className="space-y-5">
        {header}
        <div className="panel rounded-2xl px-6 py-20 text-center">
          <p className="text-xl font-medium text-bright">Your vault is empty</p>
          <p className="mx-auto mt-2 max-w-md text-sm text-mute">
            Search the Pokémon TCG catalogue to add your first card. Artwork, set details and market
            prices are pulled in automatically.
          </p>
          <div className="mt-6 flex flex-wrap justify-center gap-3">
            <button
              onClick={onGoToSearch}
              className="rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
            >
              Find your first card
            </button>
            <button
              onClick={() => setManualOpen(true)}
              className="rounded-lg border border-edge px-5 py-2.5 text-sm text-mute transition hover:text-bright"
            >
              Add sealed or graded by hand
            </button>
          </div>

          {manualOpen && <ManualEntryDialog onClose={() => setManualOpen(false)} onAdded={onChanged} />}
        </div>
      </div>
    )
  }

  return (
    <div className="space-y-5">
      {header}

      {/* Imports you haven't looked over yet. Stays until acknowledged rather than
          fading on a timer: the point is to survive closing the tab and still be
          here tomorrow, so a batch can't be quietly forgotten half-checked. */}
      {unreviewed.length > 0 && (
        <div className="panel space-y-2 rounded-xl px-4 py-3">
          {unreviewed.map((batch) => (
            <div key={batch.id} className="flex flex-wrap items-center justify-between gap-3">
              <p className="text-sm">
                <span className="text-bright">
                  {batch.cards.toLocaleString()} {batch.cards === 1 ? 'card' : 'cards'}
                </span>
                <span className="text-mute">
                  {' '}imported {new Date(batch.createdAt).toLocaleString()} — not checked yet
                </span>
                {batch.modified > 0 && (
                  <span className="text-mute">
                    {' '}· {batch.modified} edited or partly sold since
                  </span>
                )}
              </p>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setBatchFilter(batchFilter === batch.id ? '' : batch.id)}
                  className="rounded-md px-2 py-1 text-xs text-mute transition hover:bg-white/5 hover:text-bright"
                >
                  {batchFilter === batch.id ? 'Show everything' : 'Show only these'}
                </button>
                <button
                  onClick={() => void acknowledge(batch.id)}
                  className="rounded-md bg-arc px-2.5 py-1 text-xs font-medium text-white transition hover:brightness-110"
                >
                  Looks right
                </button>
                <ConfirmButton
                  onConfirm={() => removeBatch(batch.id)}
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

          {unreviewed.length > 1 && (
            <button
              onClick={async () => {
                await api.acknowledgeAllImports()
                setBatches(await api.importBatches())
              }}
              className="text-xs text-mute underline-offset-2 transition hover:text-bright hover:underline"
            >
              Mark all {unreviewed.length} as checked
            </button>
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        {/* Only worth showing once there is something hand-entered to separate
            out — on a collection of pure singles it would be three buttons that
            never change anything. */}
        {counts.hand > 0 && (
          <div className="flex shrink-0 items-center rounded-lg border border-edge p-0.5 text-sm">
            {(
              [
                ['cards', 'Cards', counts.cards],
                ['hand', 'Sealed & slabs', counts.hand],
                ['all', 'All', counts.all],
              ] as const
            ).map(([key, text, n]) => (
              <button
                key={key}
                onClick={() => setKind(key)}
                title={
                  key === 'hand'
                    ? 'Sealed product, graded slabs and anything else entered by hand'
                    : undefined
                }
                className={`rounded-md px-3 py-1.5 transition ${
                  kind === key ? 'bg-arc text-white' : 'text-mute hover:text-bright'
                }`}
              >
                {text} <span className="tabular-nums opacity-70">{n}</span>
              </button>
            ))}
          </div>
        )}

        <input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Filter your collection…"
          className={`${control} min-w-[200px] flex-1`}
        />

        <select value={setFilter_} onChange={(e) => setSetFilter(e.target.value)} className={control}>
          <option value="">All sets</option>
          {sets.map(([id, name]) => (
            <option key={id} value={id}>
              {name}
            </option>
          ))}
        </select>

        {locations.length > 0 && (
          <select
            value={locationFilter}
            onChange={(e) => setLocationFilter(e.target.value)}
            className={control}
          >
            <option value="">Anywhere</option>
            {locations.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
            <option value="__none__">No location set</option>
          </select>
        )}

        {batches.length > 0 && (
          <select
            value={batchFilter}
            onChange={(e) => setBatchFilter(e.target.value)}
            className={control}
            title="Show only the cards a particular CSV import added"
          >
            <option value="">Any import</option>
            {batches.map((b) => (
              <option key={b.id} value={b.id}>
                {new Date(b.createdAt).toLocaleDateString()}{' '}
                {new Date(b.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                {' '}— {b.cards}
                {b.acknowledgedAt ? '' : ' (unchecked)'}
              </option>
            ))}
          </select>
        )}

        <select value={sort} onChange={(e) => setSort(e.target.value as SortKey)} className={control}>
          {SORTS.map((s) => (
            <option key={s.key} value={s.key}>
              Sort: {s.label}
            </option>
          ))}
        </select>

        <button
          onClick={() => setManualOpen(true)}
          className="rounded-lg border border-edge px-3 py-2 text-sm text-mute transition hover:border-arc/60 hover:text-bright"
          title="Add sealed product, a slab, or anything else the catalogue doesn't list"
        >
          + By hand
        </button>
      </div>

      <p className="text-sm text-mute">
        {visible.length.toLocaleString()} of {items.length.toLocaleString()} entries ·{' '}
        <span className="text-gold tabular-nums">{money(visibleValue)}</span> shown
      </p>

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
        {visible.map((item) => (
          <CardTile
            key={item.id}
            highlight={!!item.importBatch && unreviewedIds.has(item.importBatch)}
            image={item.imageSmall ? cardImage(item.cardId, 'small') : null}
            fallbackImage={item.imageSmall}
            name={item.name}
            subtitle={
              item.isCustom ? (
                <>
                  {item.setName}
                  {item.grade && <span className="ml-1 text-gold">· {item.grade}</span>}
                </>
              ) : (
                <>
                  {item.setName} · #{item.number}
                  {item.rarity && <span className={`ml-1 ${rarityClass(item.rarity)}`}>· {item.rarity}</span>}
                </>
              )
            }
            badge={
              <div className="flex flex-col items-start gap-1">
                {item.quantity > 1 && (
                  <span className="rounded-full bg-black/75 px-2 py-0.5 text-[11px] font-semibold text-bright backdrop-blur">
                    ×{item.quantity}
                  </span>
                )}
                {item.isCustom && (
                  <span className="rounded-full bg-gold/90 px-2 py-0.5 text-[11px] font-semibold text-black shadow">
                    By hand
                  </span>
                )}
              </div>
            }
            price={item.lineValue != null ? money(item.lineValue) : null}
            note={(() => {
              // What it cost and what that's done, without needing to hover. Your
              // own valuation wins, so a slab compares against what it's really
              // worth rather than the raw card's market price.
              if (item.purchasePrice == null) return null

              const cost = item.purchasePrice * item.quantity
              const worth = item.manualValue ?? item.marketPrice
              const delta = worth == null ? null : worth * item.quantity - cost

              return (
                <>
                  <span className="text-mute">{money(cost)} paid</span>
                  {delta != null && (
                    <span className={delta >= 0 ? 'text-mint' : 'text-rose'}>
                      {' · '}
                      {delta >= 0 ? '+' : '−'}
                      {money(Math.abs(delta))}
                    </span>
                  )}
                </>
              )
            })()}
            footer={
              <div className="text-[11px] text-white/85">
                <div className="truncate">
                  {item.condition}
                  {item.grade ? ` · ${item.grade}` : ''}
                </div>
                {item.location && <div className="truncate text-white/70">📍 {item.location}</div>}
              </div>
            }
            onClick={() => setDetailCardId(item.cardId)}
          />
        ))}
      </div>

      {detailCardId && (
        <CardDetail
          cardId={detailCardId}
          owned={ownedForDetail}
          onClose={() => setDetailCardId(null)}
          onChanged={onChanged}
        />
      )}

      {manualOpen && <ManualEntryDialog onClose={() => setManualOpen(false)} onAdded={onChanged} />}
    </div>
  )
}
