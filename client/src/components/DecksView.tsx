import { useEffect, useState } from 'react'
import { api, cardImage } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { Deck, DeckSummary, SearchCard } from '../types'
import { ConfirmButton } from './ConfirmButton'

const FORMATS = [
  { id: 'standard', name: 'Standard' },
  { id: 'expanded', name: 'Expanded' },
  { id: 'unlimited', name: 'Unlimited' },
] as const

const control =
  'rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc'

/**
 * Decks you're building, and the gap between them and what you own.
 *
 * A deck lists cards rather than the copies you have, which is the whole point: the
 * useful question is what you'd still need to buy. Rules are shown, never enforced —
 * a deck of 43 cards is a deck in progress, and refusing to save it would make this
 * useless for the thing people actually do.
 */
export function DecksView() {
  const [decks, setDecks] = useState<DeckSummary[]>([])
  const [openId, setOpenId] = useState<number | null>(null)
  const [loading, setLoading] = useState(true)

  const load = () =>
    api
      .decks()
      .then(setDecks)
      .catch(() => setDecks([]))
      .finally(() => setLoading(false))

  useEffect(() => {
    void load()
  }, [])

  async function create() {
    const { id } = await api.createDeck({ name: 'Untitled deck', format: 'standard' })
    await load()
    setOpenId(id)
  }

  if (openId !== null) {
    return (
      <DeckEditor
        id={openId}
        onClose={() => {
          setOpenId(null)
          void load()
        }}
      />
    )
  }

  if (loading) return <p className="text-sm text-mute">Loading…</p>

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-mute">
          {decks.length === 0
            ? 'No decks yet.'
            : `${decks.length} ${decks.length === 1 ? 'deck' : 'decks'}`}
        </p>
        <button
          onClick={create}
          className="rounded-lg bg-arc px-3 py-1.5 text-sm text-white transition hover:brightness-110"
        >
          + New deck
        </button>
      </div>

      {decks.length === 0 ? (
        <div className="panel rounded-2xl px-6 py-16 text-center">
          <p className="text-lg text-bright">Build a deck</p>
          <p className="mx-auto mt-2 max-w-md text-sm text-mute">
            A deck is a list of cards you want to play, not the copies you own — so it can
            call for cards you haven't got, and it tells you how many you'd still need.
          </p>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {decks.map((d) => (
            <button
              key={d.id}
              onClick={() => setOpenId(d.id)}
              className="panel rounded-xl p-4 text-left transition hover:ring-1 hover:ring-arc/50"
            >
              <div className="truncate font-medium text-bright">{d.name}</div>
              <div className="mt-1 text-xs text-mute">
                {FORMATS.find((f) => f.id === d.format)?.name ?? d.format} · {d.cards} cards
              </div>
              <div className="mt-2 text-sm">
                {d.missing > 0 ? (
                  <span className="text-gold">{d.missing} still to find</span>
                ) : d.cards > 0 ? (
                  <span className="text-mint">You own every card</span>
                ) : (
                  <span className="text-mute">Empty</span>
                )}
              </div>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function DeckEditor({ id, onClose }: { id: number; onClose: () => void }) {
  const [deck, setDeck] = useState<Deck | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => api.deck(id).then(setDeck).catch(() => setDeck(null))

  useEffect(() => {
    void load()
  }, [id])

  async function change(run: () => Promise<unknown>) {
    setBusy(true)
    try {
      await run()
      await load()
    } finally {
      setBusy(false)
    }
  }

  if (!deck) return <p className="text-sm text-mute">Loading…</p>

  const total = deck.cards.reduce((n, c) => n + c.needed, 0)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <button onClick={onClose} className="text-sm text-arc transition hover:underline">
          ← All decks
        </button>
      </div>

      <div className="panel flex flex-wrap items-center gap-3 rounded-xl px-4 py-3">
        <input
          defaultValue={deck.summary.name}
          onBlur={(e) => {
            if (e.target.value.trim() !== deck.summary.name)
              void change(() => api.updateDeck(id, { name: e.target.value }))
          }}
          className={`${control} min-w-[200px] flex-1 font-medium`}
        />
        <select
          value={deck.summary.format}
          onChange={(e) => void change(() => api.updateDeck(id, { format: e.target.value }))}
          className={control}
        >
          {FORMATS.map((f) => (
            <option key={f.id} value={f.id}>
              {f.name}
            </option>
          ))}
        </select>
        <div className="text-sm tabular-nums text-mute">
          <span className={total === 60 ? 'text-mint' : 'text-bright'}>{total}</span>
          <span className="text-mute"> / 60</span>
        </div>
        <ConfirmButton
          label="Delete deck"
          confirm="Really delete"
          disabled={busy}
          onConfirm={async () => {
            await api.deleteDeck(id)
            onClose()
          }}
        />
      </div>

      {deck.problems.length > 0 && (
        <div className="panel rounded-xl border border-gold/30 px-4 py-3">
          <div className="text-xs tracking-wider text-gold uppercase">Not playable yet</div>
          <ul className="mt-1.5 space-y-0.5 text-sm text-mute">
            {deck.problems.map((p) => (
              <li key={p}>· {p}</li>
            ))}
          </ul>
        </div>
      )}

      <AddCards deckId={id} onAdded={load} />

      {deck.cards.length > 0 && (
        <div className="panel divide-y divide-edge rounded-xl">
          {deck.cards.map((c) => (
            <div key={c.cardId} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
              <img
                src={c.imageSmall ? cardImage(c.cardId, 'small') : ''}
                alt=""
                className="h-14 w-10 shrink-0 rounded object-cover ring-1 ring-edge"
              />
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-bright">
                  {c.name}
                  {!c.legal && <span className="ml-2 text-xs text-rose">not legal</span>}
                  {c.overCopyLimit && <span className="ml-2 text-xs text-rose">over 4</span>}
                </div>
                <div className="truncate text-xs text-mute">
                  {c.setName} · #{c.number}
                  {c.rarity && <span className={`ml-1 ${rarityClass(c.rarity)}`}>· {c.rarity}</span>}
                </div>
              </div>

              <div className="text-right text-xs tabular-nums">
                {c.short > 0 ? (
                  <span className="text-gold">
                    {c.short} to find
                    <span className="text-mute"> (own {c.owned})</span>
                  </span>
                ) : (
                  <span className="text-mint">own {c.owned}</span>
                )}
              </div>

              <div className="flex items-center gap-1">
                <button
                  disabled={busy}
                  onClick={() => void change(() => api.setDeckCard(id, c.cardId, c.needed - 1))}
                  className="h-7 w-7 rounded-md border border-edge text-mute transition hover:border-arc hover:text-bright disabled:opacity-40"
                >
                  −
                </button>
                <span className="w-6 text-center text-sm tabular-nums text-bright">{c.needed}</span>
                <button
                  disabled={busy}
                  onClick={() => void change(() => api.setDeckCard(id, c.cardId, c.needed + 1))}
                  className="h-7 w-7 rounded-md border border-edge text-mute transition hover:border-arc hover:text-bright disabled:opacity-40"
                >
                  +
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

/**
 * Search for a card and put it in the deck.
 *
 * Searches the whole catalogue rather than your collection, because a deck is
 * allowed to want what you haven't bought yet — restricting this to what you own
 * would remove the one thing a decklist is for.
 */
function AddCards({ deckId, onAdded }: { deckId: number; onAdded: () => Promise<unknown> }) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<SearchCard[]>([])
  const [searching, setSearching] = useState(false)

  async function run(e: React.FormEvent) {
    e.preventDefault()
    if (!query.trim()) return
    setSearching(true)
    try {
      setResults((await api.search(query.trim(), 1, 12)).data)
    } finally {
      setSearching(false)
    }
  }

  return (
    <div className="panel rounded-xl p-4">
      <form onSubmit={run} className="flex flex-wrap gap-2">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Find a card to add…"
          className={`${control} min-w-[200px] flex-1`}
        />
        <button
          type="submit"
          disabled={searching}
          className="rounded-lg bg-arc px-3 py-1.5 text-sm text-white transition hover:brightness-110 disabled:opacity-40"
        >
          {searching ? 'Searching…' : 'Search'}
        </button>
      </form>

      {results.length > 0 && (
        <div className="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
          {results.map((c) => (
            <button
              key={c.id}
              onClick={async () => {
                await api.setDeckCard(deckId, c.id, 1)
                await onAdded()
                setResults([])
                setQuery('')
              }}
              className="flex items-center gap-2 rounded-lg border border-edge px-2 py-1.5 text-left transition hover:border-arc/60"
            >
              <img
                src={cardImage(c.id, 'small')}
                alt=""
                className="h-10 w-7 shrink-0 rounded object-cover"
              />
              <div className="min-w-0">
                <div className="truncate text-sm text-bright">{c.name}</div>
                <div className="truncate text-xs text-mute">
                  {c.setName} · #{c.number}
                </div>
              </div>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
