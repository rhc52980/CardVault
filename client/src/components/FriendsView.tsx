import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { languageName } from '../lib/cardStyles'
import type { FriendMatches, FriendVaultSummary, TradeMatch } from '../types'
import { ConfirmButton } from './ConfirmButton'

/**
 * Someone else's collection, imported from a file they sent you.
 *
 * Chosen over a share link because a vault mostly isn't on the internet: nothing has
 * to become reachable, there's no URL to leak into a chat window, and it works on a
 * server only your own network can see. The cost is that a file is a snapshot — which
 * is why the date they exported it is shown rather than hidden.
 *
 * Their cards live in their own tables and never touch yours, so removing a vault is
 * a delete rather than an unpick.
 */
export function FriendsView() {
  const [vaults, setVaults] = useState<FriendVaultSummary[]>([])
  const [openId, setOpenId] = useState<number | null>(null)
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  const load = () => api.friends().then(setVaults).catch(() => setVaults([]))

  useEffect(() => {
    void load()
  }, [])

  async function importFile(file: File) {
    setBusy(true)
    setError(null)
    try {
      const { id } = await api.importFriend(file, name)
      setName('')
      await load()
      setOpenId(id)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not read that vault')
    } finally {
      setBusy(false)
    }
  }

  if (openId !== null) {
    return (
      <FriendDetail
        id={openId}
        onClose={() => {
          setOpenId(null)
          void load()
        }}
      />
    )
  }

  return (
    <div className="space-y-4">
      <section className="panel rounded-xl p-4">
        <h2 className="font-medium text-bright">Swap collections with someone</h2>
        <p className="mt-1 text-sm text-mute">
          Send them a share file and import theirs. CardVault then shows what they have that
          you're after, and what you have spare that they want.{' '}
          <span className="text-bright">A share file carries no money</span> — no purchase
          prices, no valuations, no locations, notes or photos. Just which cards, how many,
          and what condition.
        </p>

        <div className="mt-3 flex flex-wrap items-center gap-2">
          <a
            href="/api/share/export"
            download
            className="rounded-lg bg-arc px-3 py-1.5 text-sm text-white transition hover:brightness-110"
          >
            Export mine
          </a>

          <span className="mx-1 text-edge">|</span>

          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Their name (optional)"
            className="rounded-lg border border-edge bg-abyss px-3 py-1.5 text-sm text-bright outline-none focus:border-arc"
          />
          <input
            ref={fileRef}
            type="file"
            accept=".json"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0]
              e.target.value = ''
              if (file) void importFile(file)
            }}
          />
          <button
            onClick={() => fileRef.current?.click()}
            disabled={busy}
            className="rounded-lg border border-edge px-3 py-1.5 text-sm text-mute transition hover:border-arc/60 hover:text-bright disabled:opacity-40"
          >
            Import theirs…
          </button>
        </div>

        {error && <div className="mt-2 text-sm text-rose">{error}</div>}
      </section>

      {vaults.length === 0 ? (
        <div className="panel rounded-2xl px-6 py-12 text-center">
          <p className="text-bright">No shared vaults yet</p>
          <p className="mx-auto mt-2 max-w-md text-sm text-mute">
            Import a file someone sent you and it appears here, kept entirely separate from
            your own collection.
          </p>
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {vaults.map((v) => (
            <button
              key={v.id}
              onClick={() => setOpenId(v.id)}
              className="panel rounded-xl p-4 text-left transition hover:ring-1 hover:ring-arc/50"
            >
              <div className="truncate font-medium text-bright">{v.name}</div>
              <div className="mt-1 text-xs text-mute">
                {v.owned.toLocaleString()} cards · {v.wanted.toLocaleString()} wanted
              </div>
              <div className="mt-2 text-xs text-mute">
                {v.exportedAt
                  ? `Their list from ${new Date(v.exportedAt).toLocaleDateString()}`
                  : `Imported ${new Date(v.importedAt).toLocaleDateString()}`}
              </div>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function FriendDetail({ id, onClose }: { id: number; onClose: () => void }) {
  const [matches, setMatches] = useState<FriendMatches | null>(null)

  useEffect(() => {
    api.friendMatches(id).then(setMatches).catch(() => setMatches(null))
  }, [id])

  if (!matches) return <p className="text-sm text-mute">Loading…</p>

  const { vault, theyHave, youCouldOffer } = matches

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <button onClick={onClose} className="text-sm text-arc transition hover:underline">
          ← All shared vaults
        </button>
      </div>

      <div className="panel flex flex-wrap items-center gap-x-6 gap-y-2 rounded-xl px-4 py-3">
        <div className="font-medium text-bright">{vault.name}</div>
        <div className="text-xs text-mute">
          {vault.owned.toLocaleString()} cards · {vault.wanted.toLocaleString()} wanted
          {vault.exportedAt && ` · exported ${new Date(vault.exportedAt).toLocaleDateString()}`}
        </div>
        <div className="ml-auto">
          <ConfirmButton
            label="Remove this vault"
            confirm="Really remove"
            onConfirm={async () => {
              await api.removeFriend(id)
              onClose()
            }}
          />
        </div>
      </div>

      <MatchList
        title="They have, and you want"
        empty="Nothing of theirs is on your want list."
        matches={theyHave}
        quantity={(m) => `they have ${m.theirQuantity}`}
      />

      <MatchList
        title="You could offer"
        empty="None of your spares are on their want list."
        matches={youCouldOffer}
        quantity={(m) => `you can spare ${m.yourQuantity}`}
      />
    </div>
  )
}

function MatchList({
  title,
  empty,
  matches,
  quantity,
}: {
  title: string
  empty: string
  matches: TradeMatch[]
  quantity: (m: TradeMatch) => string
}) {
  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">
        {title}
        {matches.length > 0 && <span className="ml-2 text-sm text-mute">{matches.length}</span>}
      </h2>

      {matches.length === 0 ? (
        <p className="mt-1 text-sm text-mute">{empty}</p>
      ) : (
        <div className="mt-2 divide-y divide-edge">
          {matches.map((m) => (
            <div key={m.cardId} className="flex flex-wrap items-center gap-3 py-2">
              <div className="min-w-0 flex-1">
                <div className="truncate text-sm text-bright">{m.name}</div>
                <div className="truncate text-xs text-mute">
                  {m.setName} · #{m.number}
                  {m.condition && ` · ${m.condition}`}
                  {m.language && m.language !== 'en' && ` · ${languageName(m.language)}`}
                </div>
              </div>
              <div className="text-xs tabular-nums text-mint">{quantity(m)}</div>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
