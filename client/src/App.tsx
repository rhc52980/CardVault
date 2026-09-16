import { useCallback, useEffect, useState } from 'react'
import { api } from './api'
import { feedbackHref } from './lib/feedback'
// Imported rather than referenced as /logo.png so Vite content-hashes the URL.
// A fixed name in public/ let a cached copy of the old artwork survive several
// releases, because nothing ever told the browser the picture had changed.
import logoUrl from './assets/logo.png'
import { CollectionView } from './components/CollectionView'
import { ImportView } from './components/ImportView'
import { LoginScreen } from './components/LoginScreen'
import { PriceRefreshButton } from './components/PriceRefreshButton'
import { SearchView } from './components/SearchView'
import { DecksView } from './components/DecksView'
import { SetsView } from './components/SetsView'
import { Toaster } from './components/Toaster'
import { VaultSwitcher } from './components/VaultSwitcher'
import { SettingsView } from './components/SettingsView'
import { StatsBar } from './components/StatsBar'
import type { AuthStatus, CollectionItem, CollectionStats } from './types'

type Tab = 'vault' | 'decks' | 'sets' | 'search' | 'import' | 'settings'

export default function App() {
  const [tab, setTab] = useState<Tab>('vault')
  const [items, setItems] = useState<CollectionItem[]>([])
  const [stats, setStats] = useState<CollectionStats | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [auth, setAuth] = useState<AuthStatus | null>(null)
  const [appVersion, setAppVersion] = useState('')
  const [buildDate, setBuildDate] = useState('')
  const [vaultName, setVaultName] = useState('CardVault')
  const [updateAvailable, setUpdateAvailable] = useState<{ latest?: string | null; url?: string | null } | null>(null)

  const checkAuth = useCallback(async () => {
    try {
      setAuth(await api.authStatus())
    } catch {
      // If even the status call fails the server is unreachable; the collection
      // fetch below will surface that more usefully.
      setAuth({ enabled: false, authenticated: true, isSecureConnection: false })
    }
  }, [])

  const refresh = useCallback(async () => {
    try {
      const [collection, statistics] = await Promise.all([api.collection(), api.stats()])
      setItems(collection)
      setStats(statistics)
      setError(null)

      // Version lives on the settings payload; fetched separately so a settings
      // failure can never stop the collection rendering.
      void api
        .settings()
        .then((s) => {
          setAppVersion(s.version.split('+')[0])
          setBuildDate(s.buildDate)
          setVaultName(s.vaultName)
          setUpdateAvailable(
            s.update.available ? { latest: s.update.latest, url: s.update.releaseUrl } : null,
          )
        })
        .catch(() => {})
    } catch (e) {
      // A session can expire while the tab is open; re-check so the login screen
      // takes over rather than leaving a stuck error on screen.
      void checkAuth()
      setError(e instanceof Error ? e.message : 'Could not reach the vault server')
    } finally {
      setLoading(false)
    }
  }, [checkAuth])

  useEffect(() => {
    void checkAuth().then(refresh)
  }, [checkAuth, refresh])

  // The tab title too, not just the header — with several of these open, the tab
  // strip is where you actually tell one from another.
  useEffect(() => {
    document.title = vaultName
  }, [vaultName])

  if (!auth) return <div className="aurora min-h-full" />

  if (auth.enabled && !auth.authenticated) {
    return (
      <LoginScreen
        status={auth}
        onSignedIn={() => {
          void checkAuth().then(refresh)
        }}
      />
    )
  }

  return (
    <div className="aurora relative min-h-full">
      <Toaster />
      <div className="relative z-10 mx-auto max-w-[1600px] px-5 py-6 sm:px-8">
        <header className="mb-6 flex flex-wrap items-center justify-between gap-4">
          <div className="flex items-center gap-3">
            <img
              src={logoUrl}
              alt=""
              width={40}
              height={40}
              className="h-10 w-10 shrink-0 rounded-xl shadow-lg"
            />
            <div>
              <h1 className="text-xl font-semibold tracking-tight">
                {vaultName}
                {appVersion && (
                  <span
                    className="ml-2 align-middle font-mono text-[11px] font-normal text-mute"
                    title={buildDate ? `Built ${buildDate} UTC` : undefined}
                  >
                    {appVersion}
                  </span>
                )}
              </h1>
              <p className="text-xs text-mute">Your collection, valued daily</p>
            </div>
          </div>

          {updateAvailable && (
            <a
              href={updateAvailable.url ?? '#'}
              target="_blank"
              rel="noreferrer"
              className="rounded-lg bg-gold/15 px-3 py-1.5 text-xs text-gold ring-1 ring-gold/30 transition hover:brightness-110"
            >
              {updateAvailable.latest} available ↗
            </a>
          )}

          <VaultSwitcher />

          <nav className="flex rounded-xl border border-edge bg-surface p-1">
            {(
              [
                ['vault', 'My vault'],
                ['decks', 'Decks'],
                ['sets', 'Sets'],
                ['search', 'Add cards'],
                ['import', 'Import CSV'],
                ['settings', 'Settings'],
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
            {/* Next to the totals it acts on, rather than only buried in Settings.
                Reloads the collection when a run ends so the new prices appear
                without anyone thinking to refresh the page. */}
            <div className="mt-3 flex justify-end">
              <PriceRefreshButton onFinished={refresh} compact />
            </div>
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
          {tab === 'decks' && <DecksView />}
          {tab === 'sets' && <SetsView onCollectionChanged={refresh} />}
          {tab === 'search' && <SearchView onCollectionChanged={refresh} />}
          {tab === 'import' && <ImportView onImported={refresh} />}
          {tab === 'settings' && (
            <SettingsView
              onAuthChanged={() => {
                void checkAuth().then(refresh)
              }}
              onVaultRenamed={setVaultName}
            />
          )}
        </main>

        <footer className="mt-16 flex flex-wrap items-baseline justify-between gap-x-6 gap-y-2 border-t border-edge pt-5 text-xs text-mute">
          <span>
            Card data and prices from{' '}
            <a href="https://pokemontcg.io" target="_blank" rel="noreferrer" className="text-arc hover:underline">
              pokemontcg.io
            </a>
            . Prices are TCGplayer market values and refresh daily.
          </span>
          {/* Bottom-right, out of the way. It fetches nothing until clicked. */}
          <a
            href={feedbackHref(appVersion)}
            target="_blank"
            rel="noopener noreferrer"
            title="Report a bug or suggest an idea on GitHub (opens in a new tab)"
            className="whitespace-nowrap hover:text-arc hover:underline"
          >
            Feedback
          </a>
        </footer>
      </div>
    </div>
  )
}
