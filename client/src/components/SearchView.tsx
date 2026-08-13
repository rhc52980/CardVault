import { useEffect, useRef, useState } from 'react'
import { api, cardImage, money } from '../api'
import type { SearchCard } from '../types'
import { AddCardDialog } from './AddCardDialog'
import { CardTile } from './CardTile'

export function SearchView({ onCollectionChanged }: { onCollectionChanged: () => void }) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<SearchCard[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [adding, setAdding] = useState<SearchCard | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  /** Adds to the want list with no target yet — the price is set on the Wanted pane. */
  async function addWant(card: SearchCard) {
    try {
      await api.addWant({ cardId: card.id, variant: card.variants[0] ?? 'normal' })
      setResults((rs) => rs.map((r) => (r.id === card.id ? { ...r, isWanted: true } : r)))
      onCollectionChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not add to your want list')
    }
  }

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  // Debounced search — the API is rate limited, so we don't fire on every keystroke.
  useEffect(() => {
    const term = query.trim()
    if (term.length < 2) {
      setResults([])
      setTotal(0)
      setError(null)
      return
    }

    const controller = new AbortController()
    const timer = setTimeout(() => {
      setLoading(true)
      setError(null)
      api
        .search(term, 1, 36, controller.signal)
        .then((res) => {
          setResults(res.data)
          setTotal(res.totalCount)
        })
        .catch((e) => {
          if (e instanceof DOMException && e.name === 'AbortError') return
          setError(e instanceof Error ? e.message : 'Search failed')
        })
        .finally(() => setLoading(false))
    }, 350)

    return () => {
      controller.abort()
      clearTimeout(timer)
    }
  }, [query])

  return (
    <div className="space-y-5">
      <div>
        <div className="relative">
          <span className="absolute top-1/2 left-4 -translate-y-1/2 text-mute">⌕</span>
          <input
            ref={inputRef}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search any card — try “charizard”, or rarity:&quot;Rare Holo&quot; types:Fire"
            className="w-full rounded-xl border border-edge bg-surface py-3.5 pr-4 pl-11 text-bright placeholder:text-mute/70 focus:border-arc focus:ring-1 focus:ring-arc focus:outline-none"
          />
        </div>
        <p className="mt-2 text-xs text-mute">
          Free text searches by name. Include a colon to use the full query syntax, e.g.{' '}
          <code className="text-arc">set.id:base1 rarity:&quot;Rare Holo&quot;</code>
        </p>
      </div>

      {error && <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>}

      {loading && (
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
          {Array.from({ length: 12 }).map((_, i) => (
            <div key={i} className="skeleton aspect-[245/342] rounded-xl" />
          ))}
        </div>
      )}

      {!loading && results.length > 0 && (
        <>
          <p className="text-sm text-mute">
            {total.toLocaleString()} match{total === 1 ? '' : 'es'}
            {total > results.length && ` · showing the first ${results.length}`}
          </p>
          <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5 xl:grid-cols-6">
            {results.map((card) => (
              <CardTile
                key={card.id}
                image={cardImage(card.id, 'small')}
                fallbackImage={card.imageSmall}
                name={card.name}
                subtitle={
                  <>
                    {card.setName} · #{card.number}
                  </>
                }
                badge={
                  card.ownedQuantity > 0 ? (
                    <span className="rounded-full bg-mint/90 px-2 py-0.5 text-[11px] font-semibold text-black shadow">
                      ×{card.ownedQuantity} owned
                    </span>
                  ) : null
                }
                corner={
                  card.marketPrice != null ? (
                    <span className="rounded-full bg-black/75 px-2 py-0.5 text-[11px] font-medium text-gold tabular-nums backdrop-blur">
                      {money(card.marketPrice)}
                    </span>
                  ) : null
                }
                footer={
                  <div className="flex gap-1.5">
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        setAdding(card)
                      }}
                      className="flex-1 rounded-lg bg-arc py-1.5 text-xs font-medium text-white transition hover:brightness-110"
                    >
                      Add to vault
                    </button>
                    <button
                      onClick={(e) => {
                        e.stopPropagation()
                        void addWant(card)
                      }}
                      disabled={card.isWanted}
                      title={card.isWanted ? 'Already on your want list' : 'Add to your want list'}
                      className="rounded-lg border border-white/25 bg-black/40 px-2 py-1.5 text-xs text-white transition hover:border-white/50 disabled:opacity-50"
                    >
                      {card.isWanted ? 'Wanted' : 'Want'}
                    </button>
                  </div>
                }
                onClick={() => setAdding(card)}
              />
            ))}
          </div>
        </>
      )}

      {!loading && query.trim().length >= 2 && results.length === 0 && !error && (
        <div className="panel rounded-xl px-6 py-12 text-center">
          <p className="text-mute">No cards matched “{query.trim()}”.</p>
        </div>
      )}

      {query.trim().length < 2 && (
        <div className="panel rounded-xl px-6 py-16 text-center">
          <p className="text-lg text-bright">Find a card to add</p>
          <p className="mx-auto mt-2 max-w-md text-sm text-mute">
            Search the full Pokémon TCG catalogue by name, then pick the printing and condition you
            own. Prices and artwork come along automatically.
          </p>
        </div>
      )}

      {adding && (
        <AddCardDialog
          card={adding}
          onClose={() => setAdding(null)}
          onAdded={() => {
            onCollectionChanged()
            // Reflect the new count on the tile without a full re-search.
            setResults((rs) =>
              rs.map((r) => (r.id === adding.id ? { ...r, ownedQuantity: r.ownedQuantity + 1 } : r)),
            )
          }}
        />
      )}
    </div>
  )
}
