import { useEffect, useState } from 'react'
import { api, cardImage, money } from '../api'
import { CONDITIONS, CONDITION_LABELS, prettyVariant, rarityClass } from '../lib/cardStyles'
import { toast } from '../lib/toast'
import type { WantItem } from '../types'
import { ConfirmButton } from './ConfirmButton'
import { Modal } from './Modal'

const control =
  'rounded-md border border-edge bg-abyss px-2 py-1 text-xs text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'

/**
 * How long a card has been at your price. Worth saying because a drop this morning
 * and one that has sat there a fortnight are different situations: the first is news,
 * the second is a price you've already decided not to pay.
 */
function sinceLabel(iso: string) {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000)
  if (days <= 0) return 'today'
  if (days === 1) return 'since yesterday'
  if (days < 30) return `for ${days} days`
  return `since ${new Date(iso).toLocaleDateString()}`
}

export function WantedView({
  onChanged,
  onGoToSearch,
}: {
  onChanged: () => void
  onGoToSearch: () => void
}) {
  const [wants, setWants] = useState<WantItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [acquiring, setAcquiring] = useState<WantItem | null>(null)

  const load = () =>
    api
      .wants()
      .then(setWants)
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load your want list'))

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (error) return <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>

  if (!wants) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="skeleton h-20 rounded-xl" />
        ))}
      </div>
    )
  }

  if (wants.length === 0) {
    return (
      <div className="panel rounded-2xl px-6 py-16 text-center">
        <p className="text-lg text-bright">Nothing on your want list</p>
        <p className="mx-auto mt-2 max-w-md text-sm text-mute">
          Add cards you're hunting for and set the price you'd pay. Because the app already
          checks prices daily, it'll tell you when one comes down to your number.
        </p>
        <button
          onClick={onGoToSearch}
          className="mt-6 rounded-lg bg-arc px-5 py-2.5 text-sm font-medium text-white transition hover:brightness-110"
        >
          Find cards to add
        </button>
      </div>
    )
  }

  const reached = wants.filter((w) => w.atOrBelowTarget)
  const totalAtMarket = wants.reduce((sum, w) => sum + (w.marketPrice ?? 0) * w.quantity, 0)

  return (
    <div className="space-y-4">
      <div className="panel flex flex-wrap items-center gap-x-8 gap-y-2 rounded-xl px-4 py-3">
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Cards wanted</div>
          <div className="text-xl font-semibold tabular-nums text-bright">
            {wants.reduce((n, w) => n + w.quantity, 0)}
          </div>
        </div>
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Cost at market</div>
          <div className="text-xl font-semibold tabular-nums text-bright">{money(totalAtMarket)}</div>
        </div>
        {reached.length > 0 && (
          <div className="rounded-lg bg-mint/10 px-3 py-2 text-sm text-mint">
            {reached.length} {reached.length === 1 ? 'card has' : 'cards have'} reached your price
          </div>
        )}
      </div>

      <div className="space-y-2">
        {wants.map((w) => (
          <WantRow
            key={w.id}
            want={w}
            onChanged={() => {
              void load()
              onChanged()
            }}
            onAcquire={() => setAcquiring(w)}
          />
        ))}
      </div>

      {acquiring && (
        <AcquireDialog
          want={acquiring}
          onClose={() => setAcquiring(null)}
          onDone={() => {
            void load()
            onChanged()
          }}
        />
      )}
    </div>
  )
}

function WantRow({
  want,
  onChanged,
  onAcquire,
}: {
  want: WantItem
  onChanged: () => void
  onAcquire: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [editing, setEditing] = useState(false)
  const [target, setTarget] = useState(String(want.targetPrice ?? ''))

  async function saveTarget() {
    setBusy(true)
    try {
      const t = target.trim()
      await api.updateWant(want.id, t === '' ? { clearTarget: true } : { targetPrice: Number(t) })
      setEditing(false)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    setBusy(true)
    try {
      await api.removeWant(want.id)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div
      className={`panel flex flex-wrap items-center gap-3 rounded-xl p-3 ${
        want.atOrBelowTarget ? 'ring-1 ring-mint/40' : ''
      }`}
    >
      <img
        src={cardImage(want.cardId, 'small')}
        alt=""
        className="h-16 w-auto shrink-0 rounded ring-1 ring-white/10"
        onError={(e) => {
          const img = e.currentTarget as HTMLImageElement
          if (want.imageSmall && img.src !== want.imageSmall) img.src = want.imageSmall
          else img.style.visibility = 'hidden'
        }}
      />

      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-baseline gap-2">
          <span className="truncate font-medium text-bright">{want.name}</span>
          {want.quantity > 1 && <span className="text-xs text-mute">×{want.quantity}</span>}
          <span className="truncate text-xs text-mute">
            {want.setName}
            {want.number ? ` · #${want.number}` : ''}
          </span>
          {want.rarity && <span className={`text-xs ${rarityClass(want.rarity)}`}>{want.rarity}</span>}
        </div>

        <div className="mt-0.5 text-xs text-mute">
          {prettyVariant(want.variant)} · market {money(want.marketPrice)}
        </div>

        {want.atOrBelowTarget && (
          <div className="mt-1 text-xs font-medium text-mint">
            At your price — {money(Math.abs(want.differenceToTarget ?? 0))} under target
            {want.metSince && <span className="text-mint/70"> · {sinceLabel(want.metSince)}</span>}
          </div>
        )}

        {want.notes && <div className="mt-0.5 truncate text-xs text-mute italic">{want.notes}</div>}
      </div>

      <div className="text-right">
        <div className="text-[11px] tracking-wider text-mute uppercase">Your target</div>
        {editing ? (
          <div className="mt-1 flex items-center gap-1">
            <input
              type="number"
              step="0.01"
              min="0"
              autoFocus
              value={target}
              onChange={(e) => setTarget(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void saveTarget()
                if (e.key === 'Escape') setEditing(false)
              }}
              placeholder="none"
              className={`${control} w-24`}
            />
            <button
              onClick={saveTarget}
              disabled={busy}
              className="rounded-md bg-arc px-2 py-1 text-xs text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Save
            </button>
          </div>
        ) : (
          <button
            onClick={() => {
              setTarget(String(want.targetPrice ?? ''))
              setEditing(true)
            }}
            className="mt-0.5 block w-full text-right text-sm tabular-nums text-bright transition hover:text-arc"
          >
            {want.targetPrice != null ? money(want.targetPrice) : 'set a price'}
          </button>
        )}
        {want.targetPrice != null && want.differenceToTarget != null && !want.atOrBelowTarget && (
          <div className="text-xs text-mute tabular-nums">{money(want.differenceToTarget)} to go</div>
        )}
      </div>

      <div className="flex items-center gap-1">
        <button
          onClick={onAcquire}
          disabled={busy}
          className="rounded-md border border-edge px-2.5 py-1 text-xs text-mute transition hover:border-mint/60 hover:text-mint disabled:opacity-40"
          title="Move it into your collection"
        >
          Got it
        </button>
        <ConfirmButton
          onConfirm={remove}
          disabled={busy}
          label="Remove"
          confirm="Remove from want list"
        />
      </div>
    </div>
  )
}

/** Found one — captures what you actually paid on the way into the collection. */
function AcquireDialog({
  want,
  onClose,
  onDone,
}: {
  want: WantItem
  onClose: () => void
  onDone: () => void
}) {
  const field =
    'w-full rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'
  const label = 'block text-[11px] uppercase tracking-wider text-mute mb-1'

  const [quantity, setQuantity] = useState(want.quantity)
  const [condition, setCondition] = useState('NM')
  const [paid, setPaid] = useState(String(want.targetPrice ?? want.marketPrice ?? ''))
  const [location, setLocation] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await api.acquireWant(want.id, {
        quantity,
        variant: want.variant,
        condition,
        purchasePrice: paid.trim() === '' ? null : Number(paid),
        location: location.trim() || null,
      })

      // Worth saying explicitly: this both adds the card and takes it off the want
      // list, and the want vanishing is the only other thing you'd see.
      toast(
        quantity > 1 ? `Added ${quantity} × ${want.name}` : `Added ${want.name}`,
        'Moved from your want list into the vault',
      )
      onDone()
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add that card')
      setSaving(false)
    }
  }

  return (
    <Modal onClose={onClose}>
      <form onSubmit={submit}>
        <div className="flex gap-4 border-b border-edge p-4">
          <img
            src={cardImage(want.cardId, 'small')}
            alt=""
            className="h-24 w-auto rounded-lg ring-1 ring-white/10"
            onError={(e) => {
              if (want.imageSmall) (e.currentTarget as HTMLImageElement).src = want.imageSmall
            }}
          />
          <div className="min-w-0">
            <h2 className="text-lg font-semibold">You found it</h2>
            <p className="mt-0.5 truncate text-sm text-mute">
              {want.name} · {want.setName}
            </p>
            <p className="mt-0.5 text-xs text-mute">
              {prettyVariant(want.variant)} · market {money(want.marketPrice)}
            </p>
          </div>
        </div>

        <div className="grid grid-cols-2 gap-3 p-4">
          <div>
            <label className={label} htmlFor="aqty">
              Quantity
            </label>
            <input
              id="aqty"
              type="number"
              min={1}
              className={field}
              value={quantity}
              onChange={(e) => setQuantity(Math.max(1, Number(e.target.value) || 1))}
            />
          </div>

          <div>
            <label className={label} htmlFor="apaid">
              Paid each
            </label>
            <input
              id="apaid"
              type="number"
              step="0.01"
              min="0"
              className={field}
              value={paid}
              onChange={(e) => setPaid(e.target.value)}
              autoFocus
            />
          </div>

          <div>
            <label className={label} htmlFor="acond">
              Condition
            </label>
            <select id="acond" className={field} value={condition} onChange={(e) => setCondition(e.target.value)}>
              {CONDITIONS.map((c) => (
                <option key={c} value={c}>
                  {CONDITION_LABELS[c]}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className={label} htmlFor="aloc">
              Location <span className="normal-case">(optional)</span>
            </label>
            <input
              id="aloc"
              className={field}
              placeholder="Binder 3, page 4"
              value={location}
              onChange={(e) => setLocation(e.target.value)}
            />
          </div>
        </div>

        {error && <p className="px-4 pb-2 text-sm text-rose">{error}</p>}

        <div className="flex items-center justify-between gap-2 border-t border-edge p-4">
          <p className="text-xs text-mute">This takes it off your want list.</p>
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
              {saving ? 'Adding…' : 'Add to vault'}
            </button>
          </div>
        </div>
      </form>
    </Modal>
  )
}
