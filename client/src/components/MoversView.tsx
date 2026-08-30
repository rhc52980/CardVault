import { useCallback, useEffect, useState } from 'react'
import { api, cardImage, money } from '../api'
import { rarityClass } from '../lib/cardStyles'
import type { Mover, MoverSettings } from '../types'

const control =
  'w-20 rounded-lg border border-edge bg-abyss px-2 py-1 text-sm text-bright outline-none focus:border-arc'

/**
 * Cards whose price has moved enough to be worth telling you about.
 *
 * Rises and falls together, because the arithmetic is identical and the decision a
 * fall informs — sell before it slides further — is the mirror of the one a rise
 * informs. Sorted by what it did to your holding rather than to one card: a £6 rise
 * on eight copies changes your collection more than £40 on one.
 */
export function MoversView() {
  const [cards, setCards] = useState<Mover[]>([])
  const [settings, setSettings] = useState<MoverSettings | null>(null)
  const [loading, setLoading] = useState(true)
  const [show, setShow] = useState<'up' | 'down'>('up')

  const load = useCallback(
    () =>
      api
        .movers()
        .then((m) => {
          setCards(m.cards)
          setSettings(m.settings)
        })
        .catch(() => setCards([]))
        .finally(() => setLoading(false)),
    [],
  )

  useEffect(() => {
    void load()
  }, [load])

  async function save(next: MoverSettings) {
    setSettings(next)
    await api.saveMoverSettings(next)
    await load()
  }

  if (loading || !settings) return <p className="text-sm text-mute">Loading…</p>

  const up = cards.filter((c) => c.change > 0)
  const down = cards.filter((c) => c.change < 0)
  const shown = show === 'up' ? up : down

  return (
    <div className="space-y-4">
      <section className="panel rounded-xl p-4">
        <h2 className="font-medium text-bright">What counts as a move</h2>
        <p className="mt-1 text-sm text-mute">
          A card is listed when it has moved by <span className="text-bright">both</span> of
          these. Either on its own is useless — a percentage alone fills the page with penny
          cards doubling, an amount alone calls a 1% wobble on an expensive card news.
        </p>
        <p className="mt-2 text-xs text-mute">
          The amount is what the move did to your <span className="text-bright">whole
          holding</span>, not to one card. Eight copies shifting 6p each is 48p off your
          collection; the same 6p on a single card is nothing.
        </p>

        <div className="mt-3 flex flex-wrap items-center gap-2 text-sm text-mute">
          <span>at least</span>
          <input
            type="number"
            min="0"
            value={settings.minPercent}
            onChange={(e) => void save({ ...settings, minPercent: Number(e.target.value) })}
            className={control}
          />
          <span>%</span>
          <span className="mx-1 text-bright">and</span>
          <input
            type="number"
            min="0"
            step="0.05"
            value={settings.minAmount}
            onChange={(e) => void save({ ...settings, minAmount: Number(e.target.value) })}
            className={control}
          />
          <span>across your copies, over the last</span>
          <select
            value={settings.days}
            onChange={(e) => void save({ ...settings, days: Number(e.target.value) })}
            className={`${control} w-24`}
          >
            {[7, 30, 90, 180, 365].map((d) => (
              <option key={d} value={d}>
                {d} days
              </option>
            ))}
          </select>
        </div>
      </section>

      <div className="flex flex-wrap items-center gap-2">
        {(
          [
            ['up', `Risen (${up.length})`],
            ['down', `Fallen (${down.length})`],
          ] as const
        ).map(([key, label]) => (
          <button
            key={key}
            onClick={() => setShow(key)}
            className={`rounded-lg border px-3 py-1.5 text-sm transition ${
              show === key
                ? 'border-arc bg-arc/10 text-bright'
                : 'border-edge text-mute hover:text-bright'
            }`}
          >
            {label}
          </button>
        ))}
      </div>

      {shown.length === 0 ? (
        <div className="panel rounded-2xl px-6 py-12 text-center">
          <p className="text-bright">Nothing has moved that much</p>
          <p className="mx-auto mt-2 max-w-md text-sm text-mute">
            Either your cards have been steady, or there isn't enough price history yet — a
            card needs two readings before it can have moved at all, and prices are recorded
            once a day.
          </p>
        </div>
      ) : (
        <div className="panel divide-y divide-edge rounded-xl">
          {shown.map((c) => (
            <div key={`${c.cardId}-${c.variant}`} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
              <img
                src={c.imageSmall ? cardImage(c.cardId, 'small') : ''}
                alt=""
                className="h-14 w-10 shrink-0 rounded object-cover ring-1 ring-edge"
              />

              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-bright">{c.name}</div>
                <div className="truncate text-xs text-mute">
                  {c.setName} · #{c.number}
                  {c.rarity && <span className={`ml-1 ${rarityClass(c.rarity)}`}>· {c.rarity}</span>}
                  {c.owned > 1 && <span className="ml-1">· you have {c.owned}</span>}
                </div>
              </div>

              <div className="text-right text-xs tabular-nums text-mute">
                {money(c.was)} → <span className="text-bright">{money(c.now)}</span>
              </div>

              <div className={`w-28 text-right text-sm tabular-nums ${c.change > 0 ? 'text-mint' : 'text-rose'}`}>
                {c.change > 0 ? '+' : ''}
                {c.percentChange}%
                <div className="text-xs">
                  {c.change > 0 ? '+' : '−'}
                  {money(Math.abs(c.lineChange))}
                  {c.owned > 1 && <span className="text-mute"> total</span>}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
