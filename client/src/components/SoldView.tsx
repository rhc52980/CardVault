import { useEffect, useState } from 'react'
import { api, cardImage, money } from '../api'
import { DEFAULT_LANGUAGE, languageName, prettyVariant } from '../lib/cardStyles'
import type { SaleRecord } from '../types'
import { ConfirmButton } from './ConfirmButton'

export function SoldView({ onChanged }: { onChanged: () => void }) {
  const [sales, setSales] = useState<SaleRecord[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () =>
    api
      .sales()
      .then(setSales)
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load your sales'))

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function remove(id: number) {
    setBusy(true)
    try {
      await api.deleteSale(id)
      await load()
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  if (error) return <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>

  if (!sales) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 4 }).map((_, i) => (
          <div key={i} className="skeleton h-20 rounded-xl" />
        ))}
      </div>
    )
  }

  if (sales.length === 0) {
    return (
      <div className="panel rounded-2xl px-6 py-16 text-center">
        <p className="text-lg text-bright">Nothing sold yet</p>
        <p className="mx-auto mt-2 max-w-md text-sm text-mute">
          When you sell a card, use <strong className="text-bright">Sell</strong> on it rather than
          deleting it. The card leaves your vault but the record stays here, so what you actually
          made is never lost.
        </p>
      </div>
    )
  }

  const proceeds = sales.reduce((s, r) => s + r.proceeds, 0)
  const realised = sales.reduce((s, r) => s + (r.realisedGain ?? 0), 0)
  const unknownCost = sales.filter((s) => s.realisedGain == null).length

  return (
    <div className="space-y-4">
      <div className="panel flex flex-wrap items-center gap-x-8 gap-y-2 rounded-xl px-4 py-3">
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Proceeds</div>
          <div className="text-xl font-semibold tabular-nums text-bright">{money(proceeds)}</div>
        </div>
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Realised profit</div>
          <div className={`text-xl font-semibold tabular-nums ${realised >= 0 ? 'text-mint' : 'text-rose'}`}>
            {realised >= 0 ? '+' : ''}
            {money(realised)}
          </div>
        </div>
        <div>
          <div className="text-[11px] tracking-wider text-mute uppercase">Cards sold</div>
          <div className="text-xl font-semibold tabular-nums text-bright">
            {sales.reduce((n, s) => n + s.quantity, 0)}
          </div>
        </div>
        {unknownCost > 0 && (
          <p className="text-xs text-mute">
            {unknownCost} {unknownCost === 1 ? 'sale has' : 'sales have'} no purchase price recorded,
            so {unknownCost === 1 ? 'it counts' : 'they count'} towards proceeds but not profit.
          </p>
        )}
      </div>

      <div className="space-y-2">
        {sales.map((s) => (
          <div key={s.id} className="panel flex flex-wrap items-center gap-3 rounded-xl p-3">
            <img
              src={cardImage(s.cardId, 'small')}
              alt=""
              className="h-16 w-auto shrink-0 rounded ring-1 ring-white/10"
              onError={(e) => {
                const img = e.currentTarget as HTMLImageElement
                if (s.imageSmall && img.src !== s.imageSmall) img.src = s.imageSmall
                else img.style.visibility = 'hidden'
              }}
            />

            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline gap-2">
                <span className="truncate font-medium text-bright">{s.cardName}</span>
                {s.quantity > 1 && <span className="text-xs text-mute">×{s.quantity}</span>}
                <span className="truncate text-xs text-mute">
                  {s.setName}
                  {s.number ? ` · #${s.number}` : ''}
                </span>
              </div>
              <div className="mt-0.5 text-xs text-mute">
                {s.saleDate} · {s.variant ? prettyVariant(s.variant) : ''} {s.condition}
                {s.grade ? ` · ${s.grade}` : ''}
                {s.language && s.language !== DEFAULT_LANGUAGE ? ` · ${languageName(s.language)}` : ''}
              </div>
              <div className="mt-0.5 text-xs text-mute">
                Sold at {money(s.salePrice)} each
                {s.fees ? ` · ${money(s.fees)} fees` : ''}
                {s.purchasePrice != null ? ` · paid ${money(s.purchasePrice)}` : ' · cost not recorded'}
              </div>
              {s.notes && <div className="mt-0.5 truncate text-xs text-mute italic">{s.notes}</div>}
            </div>

            <div className="text-right">
              <div className="tabular-nums text-bright">{money(s.proceeds)}</div>
              <div
                className={`text-xs tabular-nums ${
                  s.realisedGain == null ? 'text-mute' : s.realisedGain >= 0 ? 'text-mint' : 'text-rose'
                }`}
              >
                {s.realisedGain == null
                  ? 'profit unknown'
                  : `${s.realisedGain >= 0 ? '+' : ''}${money(s.realisedGain)}`}
              </div>
            </div>

            <ConfirmButton
              onConfirm={() => remove(s.id)}
              disabled={busy}
              label="Delete"
              confirm="Delete sale record"
              title="Removes this record only — it does not put the card back in your vault"
            />
          </div>
        ))}
      </div>

      <p className="text-xs text-mute">
        Deleting a record removes it from this history only — it doesn't return the card to your
        vault. Re-add it from Add cards if you need to undo a sale.
      </p>
    </div>
  )
}
