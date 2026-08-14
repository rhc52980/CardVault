import { useEffect, useMemo, useState } from 'react'
import { api, cardImage, money } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { SearchCard, SetCard, SetSummary } from '../types'
import { AddCardDialog } from './AddCardDialog'
import { CardTile } from './CardTile'

const control =
  'rounded-lg border border-edge bg-surface px-3 py-2 text-sm text-bright focus:border-arc focus:ring-1 focus:ring-arc focus:outline-none'

/**
 * Completion is tracked two ways, because collectors mean different things by
 * "complete": the printed set (numbers shown on the cards) and the master set
 * (everything including secret rares).
 */
function completion(set: SetSummary, master: boolean) {
  const denom = master ? set.total : set.printedTotal
  if (!denom) return { owned: set.ownedDistinct, denom: 0, pct: 0 }
  const owned = Math.min(set.ownedDistinct, denom)
  return { owned, denom, pct: (owned / denom) * 100 }
}

function ProgressBar({ pct, complete }: { pct: number; complete: boolean }) {
  return (
    <div className="h-1.5 w-full overflow-hidden rounded-full bg-abyss">
      <div
        className={`h-full rounded-full transition-[width] duration-500 ${complete ? 'bg-mint' : 'bg-arc'}`}
        style={{ width: `${Math.max(pct, pct > 0 ? 2 : 0)}%` }}
      />
    </div>
  )
}

export function SetsView({ onCollectionChanged }: { onCollectionChanged: () => void }) {
  const [sets, setSets] = useState<SetSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [sort, setSort] = useState<'completion' | 'release' | 'name'>('completion')
  const [startedOnly, setStartedOnly] = useState(true)
  const [master, setMaster] = useState(false)
  const [openSet, setOpenSet] = useState<SetSummary | null>(null)

  const load = () =>
    api
      .sets()
      .then(setSets)
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load sets'))

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const visible = useMemo(() => {
    if (!sets) return []
    const term = filter.trim().toLowerCase()
    let out = sets

    if (startedOnly) out = out.filter((s) => s.ownedDistinct > 0)
    if (term)
      out = out.filter(
        (s) => s.name.toLowerCase().includes(term) || s.series?.toLowerCase().includes(term),
      )

    return [...out].sort((a, b) => {
      if (sort === 'name') return a.name.localeCompare(b.name)
      if (sort === 'release') return (b.releaseDate ?? '').localeCompare(a.releaseDate ?? '')
      return completion(b, master).pct - completion(a, master).pct
    })
  }, [sets, filter, sort, startedOnly, master])

  if (openSet) {
    return (
      <SetDetail
        set={openSet}
        master={master}
        onBack={() => {
          setOpenSet(null)
          void load()
        }}
        onCollectionChanged={() => {
          onCollectionChanged()
          void load()
        }}
      />
    )
  }

  if (error) return <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>

  if (!sets) {
    return (
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {Array.from({ length: 6 }).map((_, i) => (
          <div key={i} className="skeleton h-28 rounded-xl" />
        ))}
      </div>
    )
  }

  const started = sets.filter((s) => s.ownedDistinct > 0).length

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center gap-3">
        <input
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          placeholder="Find a set…"
          className={`${control} min-w-[200px] flex-1`}
        />

        <select value={sort} onChange={(e) => setSort(e.target.value as typeof sort)} className={control}>
          <option value="completion">Sort: Completion</option>
          <option value="release">Sort: Newest</option>
          <option value="name">Sort: Name</option>
        </select>

        <div className="flex rounded-lg border border-edge bg-surface p-1 text-sm">
          <button
            onClick={() => setStartedOnly(true)}
            className={`rounded-md px-3 py-1.5 transition ${startedOnly ? 'bg-arc text-white' : 'text-mute hover:text-bright'}`}
          >
            Started ({started})
          </button>
          <button
            onClick={() => setStartedOnly(false)}
            className={`rounded-md px-3 py-1.5 transition ${!startedOnly ? 'bg-arc text-white' : 'text-mute hover:text-bright'}`}
          >
            All ({sets.length})
          </button>
        </div>

        <label className="flex items-center gap-2 text-sm text-mute">
          <input
            type="checkbox"
            checked={master}
            onChange={(e) => setMaster(e.target.checked)}
            className="h-4 w-4 accent-[color:var(--color-arc)]"
          />
          Master set
        </label>
      </div>

      <p className="text-xs text-mute">
        {master
          ? 'Master set counts every card including secret rares.'
          : 'Printed set counts the numbers shown on the cards. Tick “Master set” to include secret rares.'}
      </p>

      {visible.length === 0 ? (
        <div className="panel rounded-2xl px-6 py-16 text-center">
          <p className="text-lg text-bright">
            {startedOnly ? "You haven't added cards from any set yet" : 'No sets matched'}
          </p>
          {startedOnly && (
            <button
              onClick={() => setStartedOnly(false)}
              className="mt-5 rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
            >
              Browse all sets
            </button>
          )}
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {visible.map((set) => {
            const c = completion(set, master)
            const done = c.denom > 0 && c.owned >= c.denom
            return (
              <button
                key={set.id}
                onClick={() => setOpenSet(set)}
                className="panel rounded-xl p-4 text-left transition hover:border-arc/60 hover:brightness-110"
              >
                <div className="flex items-start gap-3">
                  {set.logo ? (
                    <img src={set.logo} alt="" className="h-10 w-16 shrink-0 object-contain" loading="lazy" />
                  ) : (
                    <div className="h-10 w-16 shrink-0 rounded bg-abyss" />
                  )}
                  <div className="min-w-0 flex-1">
                    <div className="flex items-baseline justify-between gap-2">
                      <span className="truncate font-medium text-bright">{set.name}</span>
                      {done && <span className="shrink-0 text-xs text-mint">✓ Complete</span>}
                    </div>
                    <div className="mt-0.5 truncate text-xs text-mute">
                      {set.series}
                      {set.releaseDate ? ` · ${set.releaseDate.slice(0, 4)}` : ''}
                    </div>
                  </div>
                </div>

                <div className="mt-3">
                  <div className="mb-1 flex items-baseline justify-between text-xs">
                    <span className="text-mute tabular-nums">
                      {c.owned} / {c.denom || '?'}
                    </span>
                    <span className={`tabular-nums ${done ? 'text-mint' : 'text-bright'}`}>
                      {c.pct.toFixed(c.pct >= 10 ? 0 : 1)}%
                    </span>
                  </div>
                  <ProgressBar pct={c.pct} complete={done} />
                </div>
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

// ---------------------------------------------------------------- set detail

function SetDetail({
  set,
  master,
  onBack,
  onCollectionChanged,
}: {
  set: SetSummary
  master: boolean
  onBack: () => void
  onCollectionChanged: () => void
}) {
  const [cards, setCards] = useState<SetCard[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [missingOnly, setMissingOnly] = useState(false)
  const [adding, setAdding] = useState<SearchCard | null>(null)

  const load = () =>
    api
      .setCards(set.id)
      .then(setCards)
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load this set'))

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [set.id])

  const ownedCount = cards?.filter((c) => c.ownedQuantity > 0).length ?? 0
  const visible = missingOnly ? (cards ?? []).filter((c) => c.ownedQuantity === 0) : (cards ?? [])
  const missingValue = (cards ?? [])
    .filter((c) => c.ownedQuantity === 0)
    .reduce((sum, c) => sum + (c.marketPrice ?? 0), 0)

  /** The add dialog speaks SearchCard, so adapt the leaner set-card shape. */
  function toSearchCard(card: SetCard): SearchCard {
    return {
      id: card.cardId,
      name: card.name,
      number: card.number,
      rarity: card.rarity,
      supertype: card.supertype,
      types: [],
      subtypes: [],
      setId: set.id,
      setName: set.name,
      setSeries: set.series,
      releaseDate: set.releaseDate,
      imageSmall: card.imageSmall,
      imageLarge: null,
      marketPrice: card.marketPrice,
      variants: card.variants,
      ownedQuantity: card.ownedQuantity,
    }
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <button
            onClick={onBack}
            className="rounded-lg border border-edge px-3 py-2 text-sm text-mute transition hover:text-bright"
          >
            ← All sets
          </button>
          {set.logo && <img src={set.logo} alt="" className="h-9 w-auto object-contain" />}
          <div>
            <h2 className="font-semibold text-bright">{set.name}</h2>
            <p className="text-xs text-mute">
              {set.series}
              {set.releaseDate ? ` · released ${set.releaseDate}` : ''}
            </p>
          </div>
        </div>

        <label className="flex items-center gap-2 text-sm text-mute">
          <input
            type="checkbox"
            checked={missingOnly}
            onChange={(e) => setMissingOnly(e.target.checked)}
            className="h-4 w-4 accent-[color:var(--color-arc)]"
          />
          Show only what I'm missing
        </label>
      </div>

      {cards && (
        <div className="panel rounded-xl px-4 py-3">
          <div className="mb-2 flex flex-wrap items-baseline justify-between gap-2 text-sm">
            <span className="text-bright tabular-nums">
              {ownedCount} of {master ? set.total : set.printedTotal || cards.length} collected
            </span>
            <span className="text-xs text-mute">
              {cards.length - ownedCount} missing
              {missingValue > 0 && ` · ${money(missingValue)} to finish at market`}
            </span>
          </div>
          <ProgressBar
            pct={completion({ ...set, ownedDistinct: ownedCount }, master).pct}
            complete={ownedCount >= (master ? set.total : set.printedTotal)}
          />
        </div>
      )}

      {error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>}

      {!cards && !error && (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
          {Array.from({ length: 18 }).map((_, i) => (
            <div key={i} className="skeleton aspect-[245/342] rounded-xl" />
          ))}
        </div>
      )}

      {cards && (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
          {visible.map((card) => {
            const owned = card.ownedQuantity > 0
            return (
              <div key={card.cardId} className={owned ? '' : 'opacity-45 grayscale transition hover:opacity-100 hover:grayscale-0'}>
                <CardTile
                  image={cardImage(card.cardId, 'small')}
                  fallbackImage={card.imageSmall}
                  name={card.name}
                  subtitle={
                    <>
                      #{card.number}
                      {card.rarity && <span className={`ml-1 ${rarityClass(card.rarity)}`}>· {card.rarity}</span>}
                    </>
                  }
                  badge={
                    owned ? (
                      <span className="rounded-full bg-mint/90 px-2 py-0.5 text-[11px] font-semibold text-black shadow">
                        ✓{card.ownedQuantity > 1 ? ` ×${card.ownedQuantity}` : ''}
                      </span>
                    ) : null
                  }
                  price={card.marketPrice != null ? money(card.marketPrice) : null}
                  footer={
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        setAdding(toSearchCard(card))
                      }}
                      className="w-full rounded-lg bg-arc py-1.5 text-xs font-medium text-white transition hover:brightness-110"
                    >
                      {owned ? 'Add another' : 'Add to vault'}
                    </button>
                  }
                  onClick={() => setAdding(toSearchCard(card))}
                />
              </div>
            )
          })}
        </div>
      )}

      {cards && visible.length === 0 && (
        <div className="panel rounded-2xl px-6 py-16 text-center">
          <p className="text-xl font-medium text-mint">Set complete</p>
          <p className="mt-2 text-sm text-mute">You own every card in {set.name}.</p>
        </div>
      )}

      {adding && (
        <AddCardDialog
          card={adding}
          onClose={() => setAdding(null)}
          onAdded={() => {
            onCollectionChanged()
            void load()
          }}
        />
      )}
    </div>
  )
}
