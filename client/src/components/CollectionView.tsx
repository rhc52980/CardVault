import { useEffect, useMemo, useRef, useState } from 'react'
import { api, cardImage, money } from '../api'
import {
  LANGUAGES,
  LANGUAGE_LABELS,
  gradeLabel,
  languageName,
  languageTag,
  rarityClass,
} from '../lib/cardStyles'
import type { CollectionItem, ImportBatchSummary } from '../types'
import { ConfirmButton } from './ConfirmButton'
import { ImportsView } from './ImportsView'
import { CardDetail } from './CardDetail'
import { CardTile } from './CardTile'
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
  const [pane, setPane] = useState<'owned' | 'imports' | 'wanted' | 'sold'>('owned')
  const [locationFilter, setLocationFilter] = useState('')
  const [languageFilter, setLanguageFilter] = useState('')
  const [gradedFilter, setGradedFilter] = useState('')
  const [flaggedOnly, setFlaggedOnly] = useState(false)
  const [batchFilter, setBatchFilter] = useState('')
  const [batches, setBatches] = useState<ImportBatchSummary[]>([])
  // Only ever read for the count on the tab. A card reaching your price is the one
  // thing on that list worth interrupting you for, and it's no use only being
  // visible once you've already gone looking for it.
  const [wantsAtPrice, setWantsAtPrice] = useState(0)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  // The tile last clicked, so shift-click knows what to extend from. A ref rather
  // than state: it is read by the very next click, and two clicks close enough
  // together to land in one render would otherwise extend from a stale anchor.
  const anchor = useRef<number | null>(null)

  // Reloaded whenever the collection is, which covers an import having just added
  // cards without needing to be told about it separately.
  useEffect(() => {
    let live = true
    api.importBatches()
      .then((b) => live && setBatches(b))
      .catch(() => live && setBatches([]))
    api.wants()
      .then((w) => live && setWantsAtPrice(w.filter((x) => x.atOrBelowTarget).length))
      .catch(() => live && setWantsAtPrice(0))
    return () => {
      live = false
    }
  }, [items])

  const unreviewed = useMemo(() => batches.filter((b) => !b.acknowledgedAt), [batches])
  const unreviewedIds = useMemo(() => new Set(unreviewed.map((b) => b.id)), [unreviewed])

  /**
   * Re-reads batches after the imports pane changes something.
   *
   * Also drops the batch filter if that batch has just been removed — otherwise
   * the vault would keep filtering on an import that no longer exists and look
   * empty for no visible reason.
   */
  async function refreshBatches() {
    const next = await api.importBatches()
    setBatches(next)
    if (batchFilter && !next.some((b) => b.id === batchFilter)) setBatchFilter('')
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

  // Offered only once you own something that isn't English: for an all-English
  // collection the control would filter nothing and just take up room.
  const languages = useMemo(
    () => [...new Set(items.map((i) => i.language))].sort(),
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
    if (languageFilter) out = out.filter((i) => i.language === languageFilter)
    if (gradedFilter === 'graded') out = out.filter((i) => !!i.grade)
    else if (gradedFilter === 'raw') out = out.filter((i) => !i.grade)
    if (flaggedOnly) out = out.filter((i) => !!i.flagged)
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
  }, [items, filter, setFilter_, languageFilter, gradedFilter, flaggedOnly, locationFilter, batchFilter, sort, kind])

  // Entries a scan comparison disagreed with. Counted over everything you own, not
  // the current view, so narrowing a filter can't make the number look better.
  const flaggedCount = useMemo(() => items.filter((i) => i.flagged).length, [items])

  const ownedForDetail = detailCardId ? items.filter((i) => i.cardId === detailCardId) : []

  /**
   * Selection is pruned to what's on screen whenever the filters change.
   *
   * Otherwise a card selected under one filter stays selected after you narrow to a
   * different set, and "delete 40 cards" quietly takes a few you can no longer see.
   * What you can see is what you can act on.
   */
  useEffect(() => {
    setSelected((prev) => {
      if (prev.size === 0) return prev
      const onScreen = new Set(visible.map((i) => i.id))
      const next = new Set([...prev].filter((id) => onScreen.has(id)))
      return next.size === prev.size ? prev : next
    })
  }, [visible])

  function toggleSelect(id: number, extend: boolean) {
    // Read before the updater, not inside it. React runs an updater when it renders,
    // by which point the assignment at the bottom of this function has already moved
    // the anchor to the card just clicked — so a range read lazily would always
    // collapse to that one card.
    const from_ = anchor.current
    anchor.current = id

    setSelected((prev) => {
      const next = new Set(prev)

      // Shift extends from the last tile you touched, over the order on screen —
      // which is the order you're looking at, not the order they were added.
      if (extend && from_ != null) {
        const order = visible.map((i) => i.id)
        const from = order.indexOf(from_)
        const to = order.indexOf(id)
        if (from >= 0 && to >= 0) {
          const [lo, hi] = from < to ? [from, to] : [to, from]
          for (const between of order.slice(lo, hi + 1)) next.add(between)
          return next
        }
      }

      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const selectedItems = visible.filter((i) => selected.has(i.id))

  async function applyToSelection(run: (ids: number[]) => Promise<unknown>) {
    await run([...selected])
    setSelected(new Set())
    anchor.current = null
    onChanged()
  }
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
            ['imports', unreviewed.length ? `Imports (${unreviewed.length})` : 'Imports'],
            ['wanted', wantsAtPrice ? `Wanted (${wantsAtPrice})` : 'Wanted'],
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

  if (pane === 'imports') {
    return (
      <div className="space-y-5">
        {header}
        <ImportsView
          batches={batches}
          onChanged={refreshBatches}
          onShowOnly={(id) => {
            setBatchFilter(id)
            setPane('owned')
          }}
        />
      </div>
    )
  }

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

      {/* One line rather than the stack of banners this used to be — six unchecked
          imports made a wall of them above the cards. The full list moved to its
          own pane, but something has to stay here: the whole point of these is
          that a batch can't be quietly forgotten half-checked, and hiding them
          entirely behind a button would give that up. */}
      {unreviewed.length > 0 && (
        <button
          onClick={() => setPane('imports')}
          className="panel flex w-full items-center justify-between gap-3 rounded-xl px-4 py-2.5 text-left transition hover:border-gold/50"
        >
          <span className="text-sm">
            <span className="text-gold">
              {unreviewed.length} {unreviewed.length === 1 ? 'import' : 'imports'} not checked yet
            </span>
            <span className="text-mute">
              {' '}· {unreviewed.reduce((n, b) => n + b.cards, 0).toLocaleString()} cards
            </span>
          </span>
          <span className="text-xs text-arc">Review →</span>
        </button>
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

        {flaggedCount > 0 && (
          <button
            onClick={() => setFlaggedOnly((v) => !v)}
            className={`rounded-lg border px-3 py-2 text-sm transition ${
              flaggedOnly
                ? 'border-gold bg-gold/15 text-gold'
                : 'border-edge text-mute hover:border-gold/60 hover:text-gold'
            }`}
            title="Entries a scan comparison read differently. Nothing has been changed."
          >
            Needs review ({flaggedCount.toLocaleString()})
          </button>
        )}

        {items.some((i) => i.grade) && (
          <select
            value={gradedFilter}
            onChange={(e) => setGradedFilter(e.target.value)}
            className={control}
          >
            <option value="">Graded or not</option>
            <option value="graded">Graded only</option>
            <option value="raw">Raw only</option>
          </select>
        )}

        {languages.length > 1 && (
          <select
            value={languageFilter}
            onChange={(e) => setLanguageFilter(e.target.value)}
            className={control}
          >
            <option value="">Any language</option>
            {languages.map((l) => (
              <option key={l} value={l}>
                {languageName(l)}
              </option>
            ))}
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

      {selected.size > 0 ? (
        <BulkBar
          count={selected.size}
          value={selectedItems.reduce((sum, i) => sum + (i.lineValue ?? 0), 0)}
          onSelectAll={() => setSelected(new Set(visible.map((i) => i.id)))}
          allSelected={selected.size === visible.length}
          onClear={() => {
            setSelected(new Set())
            anchor.current = null
          }}
          onApply={applyToSelection}
        />
      ) : (
        <p className="text-sm text-mute">
          {visible.length.toLocaleString()} of {items.length.toLocaleString()} entries ·{' '}
          <span className="text-gold tabular-nums">{money(visibleValue)}</span> shown
        </p>
      )}

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
        {visible.map((item) => (
          <CardTile
            key={item.id}
            selected={selected.has(item.id)}
            onToggleSelect={(extend) => toggleSelect(item.id, extend)}
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
                {item.flagged && (
                  <span
                    className="rounded-full bg-gold/90 px-2 py-0.5 text-[11px] font-semibold text-black shadow"
                    title={item.flagged}
                  >
                    ⚑ Review
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
                  {item.grade ? gradeLabel(item) : item.condition}
                  {languageTag(item.language) && (
                    <span className="ml-1 rounded bg-white/15 px-1 text-[10px] tracking-wide">
                      {languageTag(item.language)}
                    </span>
                  )}
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

/**
 * What you can do to a selection.
 *
 * Replaces the count line rather than appearing above it: while a selection is
 * live, how many cards are in it and what they're worth is the more useful
 * version of the same fact, and stacking two summaries would just push the grid
 * down the page.
 *
 * Deliberately not offering a bulk sell. A lot sale is one price for the whole
 * pile, and splitting it back across the cards means inventing a per-card figure
 * that then feeds realised profit. Better to have no feature than a made-up
 * number in the ledger.
 */
function BulkBar({
  count,
  value,
  allSelected,
  onSelectAll,
  onClear,
  onApply,
}: {
  count: number
  value: number
  allSelected: boolean
  onSelectAll: () => void
  onClear: () => void
  onApply: (run: (ids: number[]) => Promise<unknown>) => Promise<void>
}) {
  const [busy, setBusy] = useState(false)
  const [location, setLocation] = useState('')
  const [language, setLanguage] = useState('')

  async function run(fn: (ids: number[]) => Promise<unknown>) {
    setBusy(true)
    try {
      await onApply(fn)
      setLocation('')
      setLanguage('')
    } finally {
      setBusy(false)
    }
  }

  const control =
    'rounded-lg border border-edge bg-abyss px-2.5 py-1.5 text-sm text-bright outline-none focus:border-arc disabled:opacity-40'

  return (
    <div className="panel flex flex-wrap items-center gap-x-3 gap-y-2 rounded-xl px-4 py-3">
      <div className="text-sm font-medium text-bright">
        {count.toLocaleString()} selected
        {value > 0 && <span className="ml-2 text-gold tabular-nums">{money(value)}</span>}
      </div>

      {!allSelected && (
        <button onClick={onSelectAll} className="text-xs text-arc transition hover:underline">
          Select all shown
        </button>
      )}
      <button onClick={onClear} className="text-xs text-mute transition hover:text-bright">
        Clear
      </button>

      <div className="ml-auto flex flex-wrap items-center gap-2">
        <input
          value={location}
          onChange={(e) => setLocation(e.target.value)}
          placeholder="Move to…"
          className={`${control} w-40`}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && location.trim()) void run((ids) => api.updateMany(ids, { location }))
          }}
        />
        <button
          disabled={busy || !location.trim()}
          onClick={() => void run((ids) => api.updateMany(ids, { location }))}
          className="rounded-lg bg-arc px-3 py-1.5 text-sm text-white transition hover:brightness-110 disabled:opacity-40"
        >
          Move
        </button>

        <select
          value={language}
          disabled={busy}
          onChange={(e) => {
            const next = e.target.value
            setLanguage(next)
            if (next) void run((ids) => api.updateMany(ids, { language: next }))
          }}
          className={control}
        >
          <option value="">Set language…</option>
          {LANGUAGES.map((l) => (
            <option key={l} value={l}>
              {LANGUAGE_LABELS[l]}
            </option>
          ))}
        </select>

        <ConfirmButton
          disabled={busy}
          label={`Remove ${count}`}
          confirm={`Really remove ${count}`}
          onConfirm={() => run((ids) => api.removeMany(ids))}
        />
      </div>
    </div>
  )
}
