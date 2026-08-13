import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { CollectionView } from './components/CollectionView'
import { ImportView } from './components/ImportView'
import { SearchView } from './components/SearchView'
import { StatsBar } from './components/StatsBar'
import type { CollectionItem, CollectionStats } from './types'

type Tab = 'vault' | 'search' | 'import'

export default function App() {
  const [tab, setTab] = useState<Tab>('vault')
  const [items, setItems] = useState<CollectionItem[]>([])
  const [stats, setStats] = useState<CollectionStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    try {
      const [collection, statistics] = await Promise.all([api.collection(), api.stats()])
      setItems(collection)
      setStats(statistics)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not reach the vault server')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <div className="aurora relative min-h-full">
      <div className="relative z-10 mx-auto max-w-[1600px] px-5 py-6 sm:px-8">
        <header className="mb-6 flex flex-wrap items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-gradient-to-br from-arc to-gold text-lg shadow-lg">
              ◈
            </div>
            <div>
              <h1 className="text-xl font-semibold tracking-tight">Pokémon Vault</h1>
              <p className="text-xs text-mute">Your collection, valued daily</p>
            </div>
          </div>

          <nav className="flex rounded-xl border border-edge bg-surface p-1">
            {(
              [
                ['vault', 'My vault'],
                ['search', 'Add cards'],
                ['import', 'Import CSV'],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                onClick={() => setTab(key)}
                className={`rounded-lg px-4 py-2 text-sm font-medium transition ${
                  tab === key ? 'bg-arc text-white shadow' : 'text-mute hover:text-bright'
                }`}
              >
                {label}
              </button>
            ))}
          </nav>
        </header>

        {error && (
          <div className="mb-5 rounded-xl bg-rose/10 px-4 py-3 text-sm text-rose">
            {error} — is the server running on port 5188?
          </div>
        )}

        {tab === 'vault' && (
          <div className="mb-6">
            <StatsBar stats={stats} />
          </div>
        )}

        <main>
          {tab === 'vault' && (
            <CollectionView
              items={items}
              loading={loading}
              onChanged={refresh}
              onGoToSearch={() => setTab('search')}
            />
          )}
          {tab === 'search' && <SearchView onCollectionChanged={refresh} />}
          {tab === 'import' && <ImportView onImported={refresh} />}
        </main>

        <footer className="mt-16 border-t border-edge pt-5 text-xs text-mute">
          Card data and prices from{' '}
          <a href="https://pokemontcg.io" target="_blank" rel="noreferrer" className="text-arc hover:underline">
            pokemontcg.io
          </a>
          . Prices are TCGplayer market values and refresh daily.
        </footer>
      </div>
    </div>
  )
}
