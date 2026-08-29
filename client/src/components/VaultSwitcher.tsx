import { useEffect, useState } from 'react'
import { api } from '../api'
import type { VaultInfo } from '../types'

/**
 * Which collection you're looking at.
 *
 * Only appears once there's more than one, so a household of one never sees a control
 * for a choice it doesn't have.
 *
 * Switching reloads the page rather than refetching. Everything held in memory —
 * the grid, the stats, decks, imports in progress — belongs to the collection you
 * were just in, and hunting down every last one of them to invalidate is a worse bet
 * than starting cleanly. It's also honest about what happened.
 */
export function VaultSwitcher() {
  const [vaults, setVaults] = useState<VaultInfo[]>([])
  const [current, setCurrent] = useState<string>('')

  useEffect(() => {
    api
      .vaults()
      .then((v) => {
        setVaults(v.vaults)
        setCurrent(v.current)
      })
      .catch(() => setVaults([]))
  }, [])

  if (vaults.length < 2) return null

  return (
    <select
      value={current}
      onChange={async (e) => {
        await api.selectVault(e.target.value)
        window.location.reload()
      }}
      title="Which collection you're looking at"
      className="rounded-lg border border-edge bg-surface px-3 py-1.5 text-sm text-bright outline-none focus:border-arc"
    >
      {vaults.map((v) => (
        <option key={v.id} value={v.id}>
          {v.name}
        </option>
      ))}
    </select>
  )
}
