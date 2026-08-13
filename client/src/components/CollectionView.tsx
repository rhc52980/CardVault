import { useMemo, useState } from 'react'
import { cardImage, money } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { CollectionItem } from '../types'
import { CardDetail } from './CardDetail'
import { CardTile } from './CardTile'
import { ManualEntryDialog } from './ManualEntryDialog'
import { SoldView } from './SoldView'

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
  const [pane, setPane] = useState<'owned' | 'sold'>('owned')

  const sets = useMemo(() => {
    const seen = new Map<string, string>()
    for (const i of items) if (i.setId && i.setName) seen.set(i.setId, i.setName)
    return [...seen.entries()].sort((a, b) => a[1].localeCompare(b[1]))
  }, [items])

  const visible = useMemo(() => {
    const term = filter.trim().toLowerCase()
    let out = items

    if (term) {
      out = out.filter(
        (i) =>
          i.name.toLowerCase().includes(term) ||
          i.setName?.toLowerCase().includes(term) ||
          i.rarity?.toLowerCase().includes(term) ||
          i.number?.toLowerCase().includes(term),
      )
    }

    if (setFilter_) out = out.filter((i) => i.setId === setFilter_)

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
  }, [items, filter, setFilter_, sort])

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

      <div className="flex flex-wrap items-center gap-3">
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
            corner={
              item.lineValue != null ? (
                <span className="rounded-full bg-black/75 px-2 py-0.5 text-[11px] font-medium text-gold tabular-nums backdrop-blur">
                  {money(item.lineValue)}
                </span>
              ) : null
            }
            footer={
              <div className="text-[11px] text-white/85">
                <div className="truncate">
                  {item.condition}
                  {item.grade ? ` · ${item.grade}` : ''}
                </div>
                {(() => {
                  // Your own valuation wins, so a slab compares against what it's
                  // really worth rather than the raw card's market price.
                  const worth = item.manualValue ?? item.marketPrice
                  if (item.purchasePrice == null || worth == null) return null
                  const up = worth >= item.purchasePrice
                  return (
                    <div className={up ? 'text-mint' : 'text-rose'}>
                      {up ? '+' : ''}
                      {money((worth - item.purchasePrice) * item.quantity)} vs paid
                    </div>
                  )
                })()}
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
