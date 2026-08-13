import { useMemo, useState } from 'react'
import { cardImage, money } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { CollectionItem } from '../types'
import { CardDetail } from './CardDetail'
import { CardTile } from './CardTile'

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

  if (items.length === 0) {
    return (
      <div className="panel rounded-2xl px-6 py-20 text-center">
        <p className="text-xl font-medium text-bright">Your vault is empty</p>
        <p className="mx-auto mt-2 max-w-md text-sm text-mute">
          Search the Pokémon TCG catalogue to add your first card. Artwork, set details and market
          prices are pulled in automatically.
        </p>
        <button
          onClick={onGoToSearch}
          className="mt-6 rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
        >
          Find your first card
        </button>
      </div>
    )
  }

  return (
    <div className="space-y-5">
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
      </div>

      <p className="text-sm text-mute">
        {visible.length.toLocaleString()} of {items.length.toLocaleString()} entries ·{' '}
        <span className="text-gold tabular-nums">{money(visibleValue)}</span> shown
      </p>

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
        {visible.map((item) => (
          <CardTile
            key={item.id}
            image={cardImage(item.cardId, 'small')}
            fallbackImage={item.imageSmall}
            name={item.name}
            subtitle={
              <>
                {item.setName} · #{item.number}
                {item.rarity && <span className={`ml-1 ${rarityClass(item.rarity)}`}>· {item.rarity}</span>}
              </>
            }
            badge={
              item.quantity > 1 ? (
                <span className="rounded-full bg-black/75 px-2 py-0.5 text-[11px] font-semibold text-bright backdrop-blur">
                  ×{item.quantity}
                </span>
              ) : null
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
                {item.purchasePrice != null && item.marketPrice != null && (
                  <div
                    className={
                      item.marketPrice >= item.purchasePrice ? 'text-mint' : 'text-rose'
                    }
                  >
                    {item.marketPrice >= item.purchasePrice ? '+' : ''}
                    {money((item.marketPrice - item.purchasePrice) * item.quantity)} vs paid
                  </div>
                )}
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
    </div>
  )
}
