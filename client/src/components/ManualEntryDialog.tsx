import { useRef, useState } from 'react'
import { api } from '../api'
import {
  CONDITIONS,
  CONDITION_LABELS,
  DEFAULT_LANGUAGE,
  LANGUAGES,
  LANGUAGE_LABELS,
} from '../lib/cardStyles'
import { Modal } from './Modal'

const field =
  'w-full rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'
const label = 'block text-[11px] uppercase tracking-wider text-mute mb-1'

/**
 * Presets, because the three things people actually hand-enter want different
 * fields: sealed product has no condition worth recording, a slab is all about
 * the grade.
 */
const KINDS = [
  {
    key: 'sealed',
    label: 'Sealed product',
    category: 'Sealed Product',
    hint: 'Booster boxes, elite trainer boxes, tins, sealed packs.',
    placeholder: 'Evolving Skies Booster Box',
    showCondition: false,
    showGrade: false,
  },
  {
    key: 'slab',
    label: 'Graded slab',
    category: 'Graded Slab',
    hint: "For a card the catalogue doesn't have. If it does have it, add it normally and set a value on the entry instead.",
    placeholder: 'Japanese Base Set Charizard',
    showCondition: false,
    showGrade: true,
  },
  {
    key: 'other',
    label: 'Other',
    category: 'Custom',
    hint: 'Japanese exclusives, promos, error cards, anything else.',
    placeholder: 'Pikachu Illustrator promo',
    showCondition: true,
    showGrade: false,
  },
] as const

export function ManualEntryDialog({ onClose, onAdded }: { onClose: () => void; onAdded: () => void }) {
  const [kind, setKind] = useState<(typeof KINDS)[number]>(KINDS[0])
  const [name, setName] = useState('')
  const [quantity, setQuantity] = useState(1)
  const [condition, setCondition] = useState('NM')
  const [language, setLanguage] = useState(DEFAULT_LANGUAGE)
  const [grade, setGrade] = useState('')
  const [value, setValue] = useState('')
  const [purchasePrice, setPurchasePrice] = useState('')
  const [purchaseDate, setPurchaseDate] = useState('')
  const [notes, setNotes] = useState('')
  const [imageUrl, setImageUrl] = useState('')
  const [file, setFile] = useState<File | null>(null)
  const [preview, setPreview] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  function pickFile(f: File | null) {
    setFile(f)
    setPreview(f ? URL.createObjectURL(f) : null)
    if (f) setImageUrl('')
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) {
      setError('Give it a name so you can find it later.')
      return
    }

    setSaving(true)
    setError(null)
    try {
      await api.addCustom(
        {
          name: name.trim(),
          category: kind.category,
          imageUrl: file ? null : imageUrl.trim() || null,
          quantity,
          condition: kind.showCondition ? condition : 'NM',
          grade: kind.showGrade ? grade.trim() || null : null,
          value: value.trim() === '' ? null : Number(value),
          purchasePrice: purchasePrice.trim() === '' ? null : Number(purchasePrice),
          purchaseDate: purchaseDate || null,
          notes: notes.trim() || null,
          language,
        },
        file,
      )
      onAdded()
      onClose()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save that item')
      setSaving(false)
    }
  }

  return (
    <Modal onClose={onClose}>
      <form onSubmit={submit}>
        <div className="border-b border-edge p-4">
          <h2 className="text-lg font-semibold">Add something by hand</h2>
          <p className="mt-1 text-sm text-mute">
            For anything the Pokémon TCG catalogue doesn't list. You set the value yourself — it
            won't be updated by the daily price refresh.
          </p>
        </div>

        <div className="border-b border-edge p-4">
          <div className="flex rounded-lg border border-edge bg-abyss p-1">
            {KINDS.map((k) => (
              <button
                key={k.key}
                type="button"
                onClick={() => setKind(k)}
                className={`flex-1 rounded-md px-3 py-1.5 text-sm transition ${
                  kind.key === k.key ? 'bg-arc text-white' : 'text-mute hover:text-bright'
                }`}
              >
                {k.label}
              </button>
            ))}
          </div>
          <p className="mt-2 text-xs text-mute">{kind.hint}</p>
        </div>

        <div className="grid grid-cols-2 gap-3 p-4">
          <div className="col-span-2">
            <label className={label} htmlFor="cname">
              Name
            </label>
            <input
              id="cname"
              className={field}
              placeholder={kind.placeholder}
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoFocus
            />
          </div>

          <div>
            <label className={label} htmlFor="cqty">
              Quantity
            </label>
            <input
              id="cqty"
              type="number"
              min={1}
              className={field}
              value={quantity}
              onChange={(e) => setQuantity(Math.max(1, Number(e.target.value) || 1))}
            />
          </div>

          <div>
            <label className={label} htmlFor="cvalue">
              Value each
            </label>
            <input
              id="cvalue"
              type="number"
              step="0.01"
              min="0"
              className={field}
              placeholder="0.00"
              value={value}
              onChange={(e) => setValue(e.target.value)}
            />
          </div>

          {kind.showCondition && (
            <div>
              <label className={label} htmlFor="ccond">
                Condition
              </label>
              <select id="ccond" className={field} value={condition} onChange={(e) => setCondition(e.target.value)}>
                {CONDITIONS.map((c) => (
                  <option key={c} value={c}>
                    {CONDITION_LABELS[c]}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div>
            <label className={label} htmlFor="clang">
              Language
            </label>
            <select
              id="clang"
              className={field}
              value={language}
              onChange={(e) => setLanguage(e.target.value)}
            >
              {LANGUAGES.map((l) => (
                <option key={l} value={l}>
                  {LANGUAGE_LABELS[l]}
                </option>
              ))}
            </select>
          </div>

          {kind.showGrade && (
            <div>
              <label className={label} htmlFor="cgrade">
                Grade
              </label>
              <input
                id="cgrade"
                className={field}
                placeholder="PSA 10"
                value={grade}
                onChange={(e) => setGrade(e.target.value)}
              />
            </div>
          )}

          <div>
            <label className={label} htmlFor="cpaid">
              Paid each <span className="normal-case">(optional)</span>
            </label>
            <input
              id="cpaid"
              type="number"
              step="0.01"
              min="0"
              className={field}
              placeholder="0.00"
              value={purchasePrice}
              onChange={(e) => setPurchasePrice(e.target.value)}
            />
          </div>

          <div className={kind.showCondition || kind.showGrade ? '' : 'col-span-2'}>
            <label className={label} htmlFor="cdate">
              Acquired <span className="normal-case">(optional)</span>
            </label>
            <input
              id="cdate"
              type="date"
              className={field}
              value={purchaseDate}
              onChange={(e) => setPurchaseDate(e.target.value)}
            />
          </div>

          <div className="col-span-2">
            <label className={label}>
              Photo <span className="normal-case">(optional)</span>
            </label>
            <div className="flex items-center gap-3">
              {preview ? (
                <img src={preview} alt="" className="h-16 w-auto rounded ring-1 ring-white/10" />
              ) : (
                <div className="flex h-16 w-12 items-center justify-center rounded bg-abyss text-xs text-mute ring-1 ring-white/5">
                  —
                </div>
              )}
              <div className="min-w-0 flex-1 space-y-2">
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => fileRef.current?.click()}
                    className="rounded-lg border border-edge px-3 py-1.5 text-xs text-mute transition hover:text-bright"
                  >
                    {file ? 'Change photo' : 'Upload a photo'}
                  </button>
                  {file && (
                    <button
                      type="button"
                      onClick={() => pickFile(null)}
                      className="rounded-lg px-3 py-1.5 text-xs text-mute transition hover:text-rose"
                    >
                      Remove
                    </button>
                  )}
                </div>
                {!file && (
                  <input
                    className={field}
                    placeholder="…or paste an image URL"
                    value={imageUrl}
                    onChange={(e) => setImageUrl(e.target.value)}
                  />
                )}
              </div>
            </div>
            <input
              ref={fileRef}
              type="file"
              accept="image/*"
              className="hidden"
              onChange={(e) => pickFile(e.target.files?.[0] ?? null)}
            />
          </div>

          <div className="col-span-2">
            <label className={label} htmlFor="cnotes">
              Notes <span className="normal-case">(optional)</span>
            </label>
            <input
              id="cnotes"
              className={field}
              placeholder="Sealed since release"
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
            {saving ? 'Saving…' : 'Add to vault'}
          </button>
        </div>
      </form>
    </Modal>
  )
}
