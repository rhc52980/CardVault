import { useState } from 'react'
import { api, cardImage, money } from '../api'
import { CONDITIONS, CONDITION_LABELS, prettyVariant } from '../lib/cardStyles'
import type { SearchCard } from '../types'
import { Modal } from './Modal'

const field =
  'w-full rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'
const label = 'block text-[11px] uppercase tracking-wider text-mute mb-1'

export function AddCardDialog({
  card,
  onClose,
  onAdded,
}: {
  card: SearchCard
  onClose: () => void
  onAdded: () => void
}) {
  const variants = card.variants.length > 0 ? card.variants : ['normal']
  const [variant, setVariant] = useState(variants[0])
  const [quantity, setQuantity] = useState(1)
  const [condition, setCondition] = useState<string>('NM')
  const [grade, setGrade] = useState('')
  const [purchasePrice, setPurchasePrice] = useState('')
  const [purchaseDate, setPurchaseDate] = useState('')
  const [notes, setNotes] = useState('')
  const [location, setLocation] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await api.add({
        cardId: card.id,
        quantity,
        variant,
        condition,
        grade: grade.trim() || null,
        purchasePrice: purchasePrice.trim() === '' ? null : Number(purchasePrice),
        purchaseDate: purchaseDate || null,
        notes: notes.trim() || null,
        location: location.trim() || null,
      })
      onAdded()
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add this card')
      setSaving(false)
    }
  }

  return (
    <Modal onClose={onClose}>
      <form onSubmit={submit}>
        <div className="flex gap-4 border-b border-edge p-4">
          <img
            src={cardImage(card.id, 'small')}
            alt={card.name}
            className="h-28 w-auto rounded-lg ring-1 ring-white/10"
            onError={(e) => {
              if (card.imageSmall) (e.currentTarget as HTMLImageElement).src = card.imageSmall
            }}
          />
          <div className="min-w-0">
            <h2 className="truncate text-lg font-semibold">{card.name}</h2>
            <p className="mt-0.5 text-sm text-mute">
              {card.setName} · #{card.number}
            </p>
            {card.rarity && <p className="mt-0.5 text-xs text-mute">{card.rarity}</p>}
            <p className="mt-2 text-sm text-gold tabular-nums">{money(card.marketPrice)} market</p>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3 p-4">
          <div className="col-span-2">
            <label className={label} htmlFor="variant">
              Printing
            </label>
            <select id="variant" className={field} value={variant} onChange={(e) => setVariant(e.target.value)}>
              {variants.map((v) => (
                <option key={v} value={v}>
                  {prettyVariant(v)}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className={label} htmlFor="quantity">
              Quantity
            </label>
            <input
              id="quantity"
              type="number"
              min={1}
              className={field}
              value={quantity}
              onChange={(e) => setQuantity(Math.max(1, Number(e.target.value) || 1))}
            />
          </div>

          <div>
            <label className={label} htmlFor="condition">
              Condition
            </label>
            <select id="condition" className={field} value={condition} onChange={(e) => setCondition(e.target.value)}>
              {CONDITIONS.map((c) => (
                <option key={c} value={c}>
                  {CONDITION_LABELS[c]}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className={label} htmlFor="grade">
              Grade <span className="normal-case">(optional)</span>
            </label>
            <input
              id="grade"
              className={field}
              placeholder="PSA 9"
              value={grade}
              onChange={(e) => setGrade(e.target.value)}
            />
          </div>

          <div>
            <label className={label} htmlFor="paid">
              Paid <span className="normal-case">(optional)</span>
            </label>
            <input
              id="paid"
              type="number"
              step="0.01"
              min="0"
              className={field}
              placeholder="0.00"
              value={purchasePrice}
              onChange={(e) => setPurchasePrice(e.target.value)}
            />
          </div>

          <div className="col-span-2">
            <label className={label} htmlFor="acquired">
              Acquired <span className="normal-case">(optional)</span>
            </label>
            <input
              id="acquired"
              type="date"
              className={field}
              value={purchaseDate}
              onChange={(e) => setPurchaseDate(e.target.value)}
            />
          </div>

          <div className="col-span-2">
            <label className={label} htmlFor="location">
              Location <span className="normal-case">(optional)</span>
            </label>
            <input
              id="location"
              className={field}
              placeholder="Binder 3, page 4"
              value={location}
              onChange={(e) => setLocation(e.target.value)}
            />
          </div>

          <div className="col-span-2">
            <label className={label} htmlFor="notes">
              Notes <span className="normal-case">(optional)</span>
            </label>
            <input
              id="notes"
              className={field}
              placeholder="Pulled from a Journey Together booster"
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </div>
        </div>

        {error && <p className="px-4 pb-2 text-sm text-rose">{error}</p>}

        <div className="flex justify-end gap-2 border-t border-edge p-4">
          <button
            type="button"
            onClick={onClose}
            className="rounded-lg px-4 py-2 text-sm text-mute transition hover:bg-white/5 hover:text-bright"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={saving}
            className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-50"
          >
            {saving ? 'Adding…' : 'Add to vault'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
