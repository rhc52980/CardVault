import { useState } from 'react'
import { api, cardImage, money } from '../api'
import { prettyVariant } from '../lib/cardStyles'
import type { CollectionItem } from '../types'
import { Modal } from './Modal'

const field =
  'w-full rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'
const label = 'block text-[11px] uppercase tracking-wider text-mute mb-1'

export function SellDialog({
  entry,
  onClose,
  onSold,
}: {
  entry: CollectionItem
  onClose: () => void
  onSold: () => void
}) {
  const [quantity, setQuantity] = useState(1)
  const [salePrice, setSalePrice] = useState(String(entry.manualValue ?? entry.marketPrice ?? ''))
  const [saleDate, setSaleDate] = useState(new Date().toISOString().slice(0, 10))
  const [fees, setFees] = useState('')
  const [notes, setNotes] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const price = Number(salePrice) || 0
  const feeAmount = Number(fees) || 0
  const proceeds = price * quantity - feeAmount
  const cost = entry.purchasePrice != null ? entry.purchasePrice * quantity : null
  const gain = cost != null ? proceeds - cost : null

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!salePrice.trim()) {
      setError('What did it sell for?')
      return
    }

    setSaving(true)
    setError(null)
    try {
      await api.sell(entry.id, {
        quantity,
        salePrice: price,
        saleDate,
        fees: fees.trim() === '' ? null : feeAmount,
        notes: notes.trim() || null,
      })
      onSold()
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not record that sale')
      setSaving(false)
    }
  }

  return (
    <Modal onClose={onClose}>
      <form onSubmit={submit}>
        <div className="flex gap-4 border-b border-edge p-4">
          <img
            src={cardImage(entry.cardId, 'small')}
            alt=""
            className="h-24 w-auto rounded-lg ring-1 ring-white/10"
            onError={(e) => {
              if (entry.imageSmall) (e.currentTarget as HTMLImageElement).src = entry.imageSmall
            }}
          />
          <div className="min-w-0">
            <h2 className="truncate text-lg font-semibold">Record a sale</h2>
            <p className="mt-0.5 truncate text-sm text-mute">
              {entry.name}
              {entry.setName ? ` · ${entry.setName}` : ''}
            </p>
            <p className="mt-0.5 text-xs text-mute">
              {prettyVariant(entry.variant)} · {entry.condition}
              {entry.grade ? ` · ${entry.grade}` : ''} · you have {entry.quantity}
            </p>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3 p-4">
          <div>
            <label className={label} htmlFor="sqty">
              How many
            </label>
            <input
              id="sqty"
              type="number"
              min={1}
              max={entry.quantity}
              className={field}
              value={quantity}
              onChange={(e) =>
                setQuantity(Math.min(entry.quantity, Math.max(1, Number(e.target.value) || 1)))
              }
            />
          </div>

          <div>
            <label className={label} htmlFor="sprice">
              Sold for, each
            </label>
            <input
              id="sprice"
              type="number"
              step="0.01"
              min="0"
              className={field}
              value={salePrice}
              onChange={(e) => setSalePrice(e.target.value)}
              autoFocus
            />
          </div>

          <div>
            <label className={label} htmlFor="sfees">
              Fees <span className="normal-case">(total, optional)</span>
            </label>
            <input
              id="sfees"
              type="number"
              step="0.01"
              min="0"
              className={field}
              placeholder="0.00"
              value={fees}
              onChange={(e) => setFees(e.target.value)}
            />
          </div>

          <div>
            <label className={label} htmlFor="sdate">
              Date
            </label>
            <input
              id="sdate"
              type="date"
              className={field}
              value={saleDate}
              onChange={(e) => setSaleDate(e.target.value)}
            />
          </div>

          <div className="col-span-2">
            <label className={label} htmlFor="snotes">
              Notes <span className="normal-case">(optional)</span>
            </label>
            <input
              id="snotes"
              className={field}
              placeholder="Sold on eBay"
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </div>
        </div>

        <div className="mx-4 mb-4 rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
          <div className="flex justify-between">
            <span className="text-mute">Proceeds after fees</span>
            <span className="tabular-nums text-bright">{money(proceeds)}</span>
          </div>
          <div className="mt-1 flex justify-between">
            <span className="text-mute">{cost == null ? 'Profit' : `Less what you paid (${money(cost)})`}</span>
            <span className={`tabular-nums ${gain == null ? 'text-mute' : gain >= 0 ? 'text-mint' : 'text-rose'}`}>
              {gain == null ? 'unknown — no purchase price recorded' : `${gain >= 0 ? '+' : ''}${money(gain)}`}
            </span>
          </div>
        </div>

        {error && <p className="px-4 pb-2 text-sm text-rose">{error}</p>}

        <div className="flex items-center justify-between gap-2 border-t border-edge p-4">
          <p className="text-xs text-mute">
            {quantity >= entry.quantity
              ? 'This removes the card from your vault.'
              : `${entry.quantity - quantity} will stay in your vault.`}
          </p>
          <div className="flex gap-2">
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
              {saving ? 'Recording…' : 'Record sale'}
            </button>
          </div>
        </div>
      </form>
    </Modal>
  )
}
