import { useEffect, useRef, useState } from 'react'
import { api, cardImage, money } from '../api'
import {
  DEFAULT_LANGUAGE,
  LANGUAGES,
  LANGUAGE_LABELS,
  gradeLabel,
  languageName,
  prettyVariant,
  rarityClass,
  typeClass,
} from '../lib/cardStyles'
import type { CollectionItem, FullCard } from '../types'
import { ConfirmButton } from './ConfirmButton'
import { Modal } from './Modal'
import { PriceChart } from './PriceChart'
import { SellDialog } from './SellDialog'

export function CardDetail({
  cardId,
  owned,
  onClose,
  onChanged,
}: {
  cardId: string
  owned: CollectionItem[]
  onClose: () => void
  onChanged: () => void
}) {
  const [card, setCard] = useState<FullCard | null>(null)
  const [error, setError] = useState<string | null>(null)

  // Asked here rather than threaded down from every screen that opens a card. It's
  // one small request when a panel opens, and it keeps the feature's on/off state a
  // detail of the one place that renders its controls.
  const [photosEnabled, setPhotosEnabled] = useState(false)

  useEffect(() => {
    let active = true
    api
      .photoStatus()
      .then((s) => active && setPhotosEnabled(s.enabled))
      .catch(() => active && setPhotosEnabled(false))
    return () => {
      active = false
    }
  }, [])

  useEffect(() => {
    let active = true
    api
      .card(cardId)
      .then((c) => active && setCard(c))
      .catch((e) => active && setError(e instanceof Error ? e.message : 'Could not load card'))
    return () => {
      active = false
    }
  }, [cardId])

  const prices = card?.tcgplayer?.prices ?? {}
  const priceRows = Object.entries(prices)

  return (
    <Modal onClose={onClose} wide>
      <div className="flex items-start justify-between gap-4 border-b border-edge p-4">
        <div className="min-w-0">
          <h2 className="truncate text-xl font-semibold">{card?.name ?? 'Loading…'}</h2>
          {card && (
            <p className="mt-1 text-sm text-mute">
              {String((card.set as { name?: string })?.name ?? '')} · #{String(card.number ?? '')}
              {card.rarity ? (
                <>
                  {' · '}
                  <span className={rarityClass(card.rarity as string)}>{String(card.rarity)}</span>
                </>
              ) : null}
            </p>
          )}
        </div>
        <button
          onClick={onClose}
          aria-label="Close"
          className="rounded-lg px-2 py-1 text-mute transition hover:bg-white/5 hover:text-bright"
        >
          ✕
        </button>
      </div>

      {error && <p className="p-4 text-sm text-rose">{error}</p>}

      <div className="grid gap-6 p-4 md:grid-cols-[minmax(0,260px)_1fr]">
        <div>
          <img
            src={cardImage(cardId, 'large')}
            alt={card?.name ?? cardId}
            className="w-full rounded-xl shadow-2xl ring-1 ring-white/10"
            onError={(e) => {
              const large = (card?.images as { large?: string } | undefined)?.large
              if (large) (e.currentTarget as HTMLImageElement).src = large
            }}
          />
          {card?.artist ? <p className="mt-2 text-center text-xs text-mute">Illustrated by {String(card.artist)}</p> : null}
        </div>

        <div className="min-w-0 space-y-5">
          {/* ---------------------------------------------------- market prices */}
          <section>
            <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">
              TCGplayer prices
              {card?.tcgplayer?.updatedAt ? ` · updated ${card.tcgplayer.updatedAt}` : ''}
            </h3>
            {priceRows.length === 0 ? (
              <p className="text-sm text-mute">No market data listed for this card.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="text-left text-xs text-mute">
                      <th className="py-1 pr-3 font-normal">Printing</th>
                      <th className="py-1 pr-3 text-right font-normal">Low</th>
                      <th className="py-1 pr-3 text-right font-normal">Market</th>
                      <th className="py-1 text-right font-normal">High</th>
                    </tr>
                  </thead>
                  <tbody>
                    {priceRows.map(([variant, p]) => (
                      <tr key={variant} className="border-t border-edge/60">
                        <td className="py-1.5 pr-3">{prettyVariant(variant)}</td>
                        <td className="py-1.5 pr-3 text-right tabular-nums text-mute">{money(p.low)}</td>
                        <td className="py-1.5 pr-3 text-right font-medium tabular-nums text-gold">{money(p.market)}</td>
                        <td className="py-1.5 text-right tabular-nums text-mute">{money(p.high)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {card?.tcgplayer?.url && (
              <a
                href={card.tcgplayer.url}
                target="_blank"
                rel="noreferrer"
                className="mt-2 inline-block text-xs text-arc hover:underline"
              >
                View on TCGplayer ↗
              </a>
            )}
          </section>

          {/* --------------------------------------------------- price history */}
          <section>
            <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">
              Market price over time
            </h3>
            <PriceChart cardId={cardId} ownedVariants={owned.map((o) => o.variant)} />
          </section>

          {/* ------------------------------------------------------ card stats */}
          {card && (
            <section className="flex flex-wrap items-center gap-2">
              {(card.types as string[] | undefined)?.map((t) => (
                <span key={t} className={`rounded-full px-2.5 py-1 text-xs ring-1 ${typeClass(t)}`}>
                  {t}
                </span>
              ))}
              {card.hp ? (
                <span className="rounded-full bg-white/5 px-2.5 py-1 text-xs text-mute ring-1 ring-white/10">
                  {String(card.hp)} HP
                </span>
              ) : null}
              {card.evolvesFrom ? (
                <span className="rounded-full bg-white/5 px-2.5 py-1 text-xs text-mute ring-1 ring-white/10">
                  Evolves from {card.evolvesFrom}
                </span>
              ) : null}
            </section>
          )}

          {card?.abilities?.length ? (
            <section>
              <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">Abilities</h3>
              {card.abilities.map((a) => (
                <div key={a.name} className="mb-2 rounded-lg bg-white/[0.03] p-3">
                  <div className="text-sm font-medium text-bright">{a.name}</div>
                  {a.text && <p className="mt-1 text-sm leading-relaxed text-mute">{a.text}</p>}
                </div>
              ))}
            </section>
          ) : null}

          {card?.attacks?.length ? (
            <section>
              <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">Attacks</h3>
              {card.attacks.map((a) => (
                <div key={a.name} className="mb-2 rounded-lg bg-white/[0.03] p-3">
                  <div className="flex items-baseline justify-between gap-3">
                    <div className="text-sm font-medium text-bright">
                      {a.name}
                      {a.cost?.length ? <span className="ml-2 text-xs text-mute">{a.cost.join(' · ')}</span> : null}
                    </div>
                    {a.damage ? <div className="text-sm font-semibold text-gold tabular-nums">{a.damage}</div> : null}
                  </div>
                  {a.text && <p className="mt-1 text-sm leading-relaxed text-mute">{a.text}</p>}
                </div>
              ))}
            </section>
          ) : null}

          {/* --------------------------------------------------- copies you own */}
          <section>
            <h3 className="mb-2 text-[11px] tracking-wider text-mute uppercase">
              In your vault
              {owned.length > 0 &&
                (() => {
                  const copies = owned.reduce((n, o) => n + o.quantity, 0)
                  return ` · ${copies} ${copies === 1 ? 'copy' : 'copies'}`
                })()}
            </h3>
            {owned.length === 0 ? (
              <p className="text-sm text-mute">You don't own this one yet.</p>
            ) : (
              <div className="space-y-2">
                {owned.map((o) => (
                  <OwnedRow key={o.id} entry={o} photosEnabled={photosEnabled} onChanged={onChanged} />
                ))}
              </div>
            )}
          </section>
        </div>
      </div>
    </Modal>
  )
}

function OwnedRow({
  entry,
  photosEnabled,
  onChanged,
}: {
  entry: CollectionItem
  photosEnabled: boolean
  onChanged: () => void
}) {
  const [busy, setBusy] = useState(false)
  const photoInput = useRef<HTMLInputElement>(null)
  // Bumped after an upload so the browser refetches rather than showing the old
  // file from cache — the URL is the same every time, which is otherwise a trap.
  const [photoStamp, setPhotoStamp] = useState(0)
  const [photoError, setPhotoError] = useState<string | null>(null)

  async function attachPhoto(file: File) {
    setBusy(true)
    setPhotoError(null)
    try {
      await api.attachPhoto(entry.id, file)
      setPhotoStamp(Date.now())
      onChanged()
    } catch (e) {
      setPhotoError(e instanceof Error ? e.message : 'Could not save that photo')
    } finally {
      setBusy(false)
    }
  }

  async function removePhoto() {
    setBusy(true)
    try {
      await api.detachPhoto(entry.id)
      onChanged()
    } finally {
      setBusy(false)
    }
  }
  const [selling, setSelling] = useState(false)
  const [editingValue, setEditingValue] = useState(false)
  const [draftValue, setDraftValue] = useState(String(entry.manualValue ?? ''))
  const [editingLocation, setEditingLocation] = useState(false)
  const [draftLocation, setDraftLocation] = useState(entry.location ?? '')
  const [editingPaid, setEditingPaid] = useState(false)
  const [draftPaid, setDraftPaid] = useState(String(entry.purchasePrice ?? ''))

  async function saveLocation() {
    setBusy(true)
    try {
      await api.update(entry.id, { location: draftLocation.trim() })
      setEditingLocation(false)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  async function saveLanguage(next: string) {
    setBusy(true)
    try {
      await api.update(entry.id, { language: next })
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  async function saveValue() {
    setBusy(true)
    try {
      const trimmed = draftValue.trim()
      await api.update(entry.id, trimmed === '' ? { clearManualValue: true } : { manualValue: Number(trimmed) })
      setEditingValue(false)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  /**
   * What the card cost. Only settable at the moment of adding until now, which
   * meant a price typed wrong, or left blank in a hurry, could only be corrected
   * by deleting the entry and adding it again — losing everything else recorded
   * against it.
   *
   * Blank clears it, and the card goes back to showing no profit rather than a
   * profit of its full value.
   */
  async function savePaid() {
    setBusy(true)
    try {
      const trimmed = draftPaid.trim()
      await api.update(
        entry.id,
        trimmed === '' ? { clearPurchasePrice: true } : { purchasePrice: Number(trimmed) },
      )
      setEditingPaid(false)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  async function setQuantity(next: number) {
    if (next < 1) return
    setBusy(true)
    try {
      await api.update(entry.id, { quantity: next })
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    setBusy(true)
    try {
      await api.remove(entry.id)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  // A graded slab is worth what you say it is, not what a raw copy trades for.
  const effectiveValue = entry.manualValue ?? entry.marketPrice
  const gain =
    entry.purchasePrice != null && effectiveValue != null
      ? (effectiveValue - entry.purchasePrice) * entry.quantity
      : null

  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
      <div className="min-w-0 flex-1">
        <div className="truncate">
          {prettyVariant(entry.variant)} · {entry.condition}
          {entry.grade ? ` · ${gradeLabel(entry)}` : ''}
          {entry.language !== DEFAULT_LANGUAGE ? ` · ${languageName(entry.language)}` : ''}
        </div>
        <div className="mt-0.5 text-xs text-mute">
          {money(effectiveValue)} each
          {entry.manualValue != null && <span className="text-gold"> (your value)</span>}
          {entry.purchasePrice != null && ` · paid ${money(entry.purchasePrice)}`}
          {gain != null && (
            <span className={gain >= 0 ? ' text-mint' : ' text-rose'}>
              {' '}
              ({gain >= 0 ? '+' : ''}
              {money(gain)})
            </span>
          )}
        </div>

        {entry.manualValue != null && entry.priced && entry.marketPrice != null && (
          <div className="mt-0.5 text-xs text-mute">Market price is {money(entry.marketPrice)}</div>
        )}

        {!entry.priced && (
          <div className="mt-0.5 text-xs text-amber-300/80">
            {entry.unpricedReason === 'graded' ? (
              <>
                No market price: every figure we hold is for a raw card, and a slab is worth
                a multiple of one — sometimes a fraction.
              </>
            ) : (
              <>
                No market price: our prices come from a catalogue of the English printings, and
                this is the {languageName(entry.language)} one.
              </>
            )}
            {entry.referencePrice != null && <> A raw copy trades at {money(entry.referencePrice)}.</>}
            {entry.manualValue == null && ' Set your own value to have it count towards your total.'}
          </div>
        )}

        {editingValue ? (
          <div className="mt-2 flex items-center gap-2">
            <input
              type="number"
              step="0.01"
              min="0"
              autoFocus
              value={draftValue}
              onChange={(e) => setDraftValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void saveValue()
                if (e.key === 'Escape') setEditingValue(false)
              }}
              placeholder="Leave blank to track market"
              className="w-48 rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc"
            />
            <button
              onClick={saveValue}
              disabled={busy}
              className="rounded-md bg-arc px-2.5 py-1 text-xs text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Save
            </button>
            <button
              onClick={() => setEditingValue(false)}
              className="rounded-md px-2 py-1 text-xs text-mute transition hover:text-bright"
            >
              Cancel
            </button>
          </div>
        ) : (
          <button
            onClick={() => {
              setDraftValue(String(entry.manualValue ?? ''))
              setEditingValue(true)
            }}
            className="mt-1 text-xs text-arc transition hover:underline"
          >
            {entry.manualValue != null ? 'Edit your value' : 'Set your own value'}
          </button>
        )}

        {editingPaid ? (
          <div className="mt-2 flex items-center gap-2">
            <input
              type="number"
              step="0.01"
              min="0"
              autoFocus
              value={draftPaid}
              onChange={(e) => setDraftPaid(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void savePaid()
                if (e.key === 'Escape') setEditingPaid(false)
              }}
              placeholder="Leave blank if unknown"
              className="w-48 rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc"
            />
            <button
              onClick={savePaid}
              disabled={busy}
              className="rounded-md bg-arc px-2.5 py-1 text-xs text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Save
            </button>
            <button
              onClick={() => setEditingPaid(false)}
              className="rounded-md px-2 py-1 text-xs text-mute transition hover:text-bright"
            >
              Cancel
            </button>
          </div>
        ) : (
          <button
            onClick={() => {
              setDraftPaid(String(entry.purchasePrice ?? ''))
              setEditingPaid(true)
            }}
            className="mt-1 block text-xs text-arc transition hover:underline"
          >
            {entry.purchasePrice != null ? 'Edit what you paid' : 'Record what you paid'}
          </button>
        )}

        {editingLocation ? (
          <div className="mt-2 flex items-center gap-2">
            <input
              autoFocus
              value={draftLocation}
              onChange={(e) => setDraftLocation(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void saveLocation()
                if (e.key === 'Escape') setEditingLocation(false)
              }}
              placeholder="Binder 3, page 4"
              className="w-52 rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc"
            />
            <button
              onClick={saveLocation}
              disabled={busy}
              className="rounded-md bg-arc px-2.5 py-1 text-xs text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Save
            </button>
            <button
              onClick={() => setEditingLocation(false)}
              className="rounded-md px-2 py-1 text-xs text-mute transition hover:text-bright"
            >
              Cancel
            </button>
          </div>
        ) : (
          <button
            onClick={() => {
              setDraftLocation(entry.location ?? '')
              setEditingLocation(true)
            }}
            className="mt-1 block text-xs text-mute transition hover:text-arc"
          >
            {entry.location ? `📍 ${entry.location}` : '📍 Where is it?'}
          </button>
        )}

        <label className="mt-1 flex items-center gap-1.5 text-xs text-mute">
          Language
          <select
            value={entry.language}
            disabled={busy}
            onChange={(e) => void saveLanguage(e.target.value)}
            className="rounded-md border border-edge bg-abyss px-1.5 py-0.5 text-xs text-bright outline-none focus:border-arc disabled:opacity-40"
          >
            {LANGUAGES.map((l) => (
              <option key={l} value={l}>
                {LANGUAGE_LABELS[l]}
              </option>
            ))}
          </select>
        </label>

        {photosEnabled && (
          <div className="mt-2">
            <input
              ref={photoInput}
              type="file"
              accept="image/png,image/jpeg,image/webp"
              className="hidden"
              onChange={(e) => {
                const file = e.target.files?.[0]
                // Cleared so choosing the same file twice still fires a change.
                e.target.value = ''
                if (file) void attachPhoto(file)
              }}
            />

            {entry.hasPhoto ? (
              <div className="flex items-start gap-2">
                <a href={api.photoUrl(entry.id, photoStamp)} target="_blank" rel="noreferrer">
                  <img
                    src={api.photoUrl(entry.id, photoStamp)}
                    alt="Your photo of this copy"
                    className="h-20 w-14 rounded-md object-cover ring-1 ring-edge transition hover:ring-arc"
                  />
                </a>
                <div className="flex flex-col gap-1">
                  <button
                    onClick={() => photoInput.current?.click()}
                    disabled={busy}
                    className="text-left text-xs text-arc transition hover:underline disabled:opacity-40"
                  >
                    Replace photo
                  </button>
                  <button
                    onClick={removePhoto}
                    disabled={busy}
                    className="text-left text-xs text-mute transition hover:text-rose disabled:opacity-40"
                  >
                    Remove photo
                  </button>
                </div>
              </div>
            ) : (
              <button
                onClick={() => photoInput.current?.click()}
                disabled={busy}
                className="block text-xs text-mute transition hover:text-arc disabled:opacity-40"
              >
                📷 Add a photo of this copy
              </button>
            )}

            {photoError && <div className="mt-1 text-xs text-rose">{photoError}</div>}
          </div>
        )}

        {entry.notes && <div className="mt-0.5 truncate text-xs text-mute italic">{entry.notes}</div>}
      </div>

      <div className="flex items-center gap-1">
        <button
          onClick={() => setQuantity(entry.quantity - 1)}
          disabled={busy || entry.quantity <= 1}
          aria-label="Decrease quantity"
          className="h-7 w-7 rounded-md bg-white/5 transition hover:bg-white/10 disabled:opacity-30"
        >
          −
        </button>
        <span className="w-8 text-center tabular-nums">{entry.quantity}</span>
        <button
          onClick={() => setQuantity(entry.quantity + 1)}
          disabled={busy}
          aria-label="Increase quantity"
          className="h-7 w-7 rounded-md bg-white/5 transition hover:bg-white/10 disabled:opacity-30"
        >
          +
        </button>
        <button
          onClick={() => setSelling(true)}
          disabled={busy}
          className="ml-2 rounded-md border border-edge px-2 py-1 text-xs text-mute transition hover:border-mint/60 hover:text-mint disabled:opacity-30"
          title="Records what you made and takes it out of the vault"
        >
          Sell
        </button>
        <ConfirmButton
          onConfirm={remove}
          disabled={busy}
          label="Remove"
          confirm="Remove from vault"
          title="Deletes it outright, with no record kept — use Sell if it sold"
        />
      </div>

      {selling && (
        <SellDialog
          entry={entry}
          onClose={() => setSelling(false)}
          onSold={onChanged}
        />
      )}
    </div>
  )
}
