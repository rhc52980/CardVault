import { useEffect, useState } from 'react'
import { api } from '../api'
import type {
  AppSettings,
  AuthStatus,
  CatalogueStatus,
  PriceSourceSettings,
  SessionInfo,
} from '../types'
import { PriceRefreshButton } from './PriceRefreshButton'

const field =
  'w-full rounded-lg border border-edge bg-abyss px-3 py-2 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'

function bytes(n: number) {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

function when(iso: string) {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

export function SettingsView({ onAuthChanged }: { onAuthChanged: () => void }) {
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = () =>
    api
      .settings()
      .then(setSettings)
      .catch((e) => setError(e instanceof Error ? e.message : 'Could not load settings'))

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  if (error) return <p className="rounded-lg bg-rose/10 px-4 py-3 text-sm text-rose">{error}</p>
  if (!settings) return <div className="skeleton h-64 rounded-2xl" />

  return (
    <div className="max-w-3xl space-y-5">
      <VersionCard settings={settings} onChanged={load} />
      <PriceSourcesCard />
      <SecurityCard onAuthChanged={onAuthChanged} />
      <ApiKeyCard settings={settings} onChanged={load} />
      <EbayCard settings={settings} onChanged={load} />
      <CatalogueCard />
      <DataCard settings={settings} />
      <BackupsCard settings={settings} onChanged={load} />
    </div>
  )
}

// ------------------------------------------------------------- price sources

function PriceSourcesCard() {
  const [state, setState] = useState<PriceSourceSettings | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => api.priceSources().then(setState).catch(() => {})

  useEffect(() => {
    void load()
  }, [])

  async function choose(id: string) {
    setBusy(true)
    try {
      await api.setPreferredPriceSource(id)
      await load()
    } finally {
      setBusy(false)
    }
  }

  if (!state) return <div className="skeleton h-32 rounded-xl" />

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">Price sources</h2>
      <p className="mt-1 text-sm text-mute">
        All of these are recorded daily, so a card's chart can show them side by side. One drives
        what your collection is said to be worth.
      </p>

      <div className="mt-3 space-y-2">
        {state.sources.map((s) => (
          <label
            key={s.id}
            className={`flex cursor-pointer items-center gap-3 rounded-lg px-3 py-2 text-sm ring-1 transition ${
              s.id === state.preferred
                ? 'bg-arc/10 text-bright ring-arc/40'
                : 'bg-white/[0.03] text-mute ring-transparent hover:text-bright'
            }`}
          >
            <input
              type="radio"
              name="price-source"
              checked={s.id === state.preferred}
              disabled={busy}
              onChange={() => choose(s.id)}
              className="h-4 w-4 accent-[color:var(--color-arc)]"
            />
            <span className="flex-1">{s.name}</span>
            <span className="font-mono text-xs text-mute">{s.currency}</span>
          </label>
        ))}
      </div>

      <div className="mt-3 border-t border-edge pt-3">
        <PriceRefreshButton />
      </div>

      <p className="mt-3 text-xs text-mute">
        Refreshing is worth doing after adding cards from the offline catalogue, which arrive
        without a price until it runs. The same button sits on the vault page.
      </p>
      <p className="mt-2 text-xs text-mute">
        Values are never blended. These are separate markets quoting different currencies, so an
        average across them would mean nothing — the chosen source is used as-is, and your own
        valuation on an entry still overrides it.
      </p>
    </section>
  )
}

// ------------------------------------------------------------ version/updates

function VersionCard({ settings, onChanged }: { settings: AppSettings; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)

  // The source stamp after '+' identifies the exact build; the part before it is
  // the release number people actually talk about.
  const [release, stamp] = settings.version.split('+')

  async function toggle(enabled: boolean) {
    setBusy(true)
    try {
      await api.setUpdateCheck(enabled)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="panel rounded-xl p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="font-medium text-bright">Version</h2>
          <p className="mt-1 text-sm">
            <span className="font-mono text-bright">{release}</span>
            {settings.buildDate && <span className="text-mute"> · built {settings.buildDate} UTC</span>}
          </p>
          {stamp && (
            <p className="mt-0.5 font-mono text-[11px] text-mute" title="Source revision this build came from">
              {stamp.slice(0, 12)}
            </p>
          )}
        </div>

        {settings.update.available && settings.update.releaseUrl && (
          <a
            href={settings.update.releaseUrl}
            target="_blank"
            rel="noreferrer"
            className="rounded-lg bg-gold/15 px-3 py-2 text-sm text-gold ring-1 ring-gold/30 transition hover:brightness-110"
          >
            {settings.update.latest} available ↗
          </a>
        )}
      </div>

      <label className="mt-3 flex items-start gap-2 text-sm text-mute">
        <input
          type="checkbox"
          checked={settings.update.enabled}
          disabled={busy}
          onChange={(e) => toggle(e.target.checked)}
          className="mt-0.5 h-4 w-4 accent-[color:var(--color-arc)]"
        />
        <span>
          Check GitHub for new releases, once a day.
          <span className="block text-xs">
            Off by default — nothing leaves this machine unless you turn it on. It only reads;
            nothing is downloaded or installed.
            {settings.update.lastCheckedUtc && (
              <> Last checked {new Date(settings.update.lastCheckedUtc).toLocaleString()}.</>
            )}
          </span>
        </span>
      </label>

      <p className="mt-3 border-t border-edge pt-3 text-xs text-mute">
        To update: pull the latest code and run{' '}
        <code className="text-arc">install\Update-CardVault.bat</code> (or{' '}
        <code className="text-arc">sudo ./linux/install.sh</code>). Your collection is outside the
        app folder and is backed up automatically whenever the version changes.
      </p>
    </section>
  )
}

// ------------------------------------------------------------------ security

function SecurityCard({ onAuthChanged }: { onAuthChanged: () => void }) {
  const [status, setStatus] = useState<AuthStatus | null>(null)
  const [sessions, setSessions] = useState<SessionInfo[]>([])
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)

  const load = async () => {
    const s = await api.authStatus()
    setStatus(s)
    setSessions(s.enabled ? await api.sessions().catch(() => []) : [])
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function run(action: () => Promise<unknown>, okText: string) {
    setBusy(true)
    setMessage(null)
    try {
      await action()
      setCurrent('')
      setNext('')
      setMessage({ kind: 'ok', text: okText })
      await load()
      onAuthChanged()
    } catch (e) {
      setMessage({ kind: 'error', text: e instanceof Error ? e.message : 'That didn’t work' })
    } finally {
      setBusy(false)
    }
  }

  if (!status) return <div className="skeleton h-40 rounded-xl" />

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">Password</h2>

      {!status.enabled ? (
        <>
          <p className="mt-1 text-sm text-mute">
            The vault is currently open to anyone who can reach it on your network. Set a password
            to require signing in.
          </p>
          <div className="mt-3 flex flex-wrap gap-2">
            <input
              type="password"
              autoComplete="new-password"
              value={next}
              onChange={(e) => setNext(e.target.value)}
              placeholder="Choose a password (10+ characters)"
              className={`${field} min-w-[240px] flex-1`}
            />
            <button
              onClick={() => run(() => api.setupPassword(next), 'Password set — the vault now requires signing in.')}
              disabled={busy || !next}
              className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Set password
            </button>
          </div>
        </>
      ) : (
        <>
          <p className="mt-1 text-sm text-mint">● Signing in is required</p>

          <div className="mt-3 grid gap-2 sm:grid-cols-2">
            <input
              type="password"
              autoComplete="current-password"
              value={current}
              onChange={(e) => setCurrent(e.target.value)}
              placeholder="Current password"
              className={field}
            />
            <input
              type="password"
              autoComplete="new-password"
              value={next}
              onChange={(e) => setNext(e.target.value)}
              placeholder="New password"
              className={field}
            />
          </div>

          <div className="mt-2 flex flex-wrap gap-2">
            <button
              onClick={() => run(() => api.changePassword(current, next), 'Password changed. Other devices have been signed out.')}
              disabled={busy || !current || !next}
              className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Change password
            </button>
            <button
              onClick={() => run(() => api.logout(), 'Signed out.')}
              disabled={busy}
              className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-bright disabled:opacity-40"
            >
              Sign out
            </button>
            <button
              onClick={() => run(() => api.disableAuth(current), 'Password removed — the vault is open again.')}
              disabled={busy || !current}
              title="Requires your current password"
              className="rounded-lg px-4 py-2 text-sm text-mute transition hover:text-rose disabled:opacity-40"
            >
              Remove password
            </button>
          </div>

          {sessions.length > 0 && (
            <div className="mt-4">
              <div className="mb-2 flex items-center justify-between">
                <h3 className="text-[11px] tracking-wider text-mute uppercase">Signed-in devices</h3>
                {sessions.length > 1 && (
                  <button
                    onClick={() => run(() => api.revokeOtherSessions(), 'Other devices signed out.')}
                    disabled={busy}
                    className="text-xs text-arc transition hover:underline disabled:opacity-40"
                  >
                    Sign out everywhere else
                  </button>
                )}
              </div>
              <div className="space-y-1">
                {sessions.map((s) => (
                  <div
                    key={s.tokenPrefix}
                    className="flex flex-wrap items-center gap-2 rounded-lg bg-white/[0.03] px-3 py-2 text-xs"
                  >
                    <span className={s.isCurrent ? 'text-mint' : 'text-mute'}>
                      {s.isCurrent ? 'This device' : 'Other device'}
                    </span>
                    <span className="truncate text-mute">{s.createdIp}</span>
                    <span className="min-w-0 flex-1 truncate text-mute">{s.userAgent}</span>
                    <span className="text-mute">last seen {new Date(s.lastSeen).toLocaleString()}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </>
      )}

      {message && (
        <p className={`mt-3 text-sm ${message.kind === 'ok' ? 'text-mint' : 'text-rose'}`}>{message.text}</p>
      )}

      <div className="mt-4 space-y-2 border-t border-edge pt-3 text-xs text-mute">
        <p>
          <span className="text-gold">Before you open this up to the internet:</span> a password is
          necessary but not sufficient. Over plain HTTP it is sent readable, so anyone between you
          and home can take it.
        </p>
        <p>
          The safer option is not to expose the port at all — a{' '}
          <strong className="text-bright">Tailscale or WireGuard VPN</strong>, or a{' '}
          <strong className="text-bright">Cloudflare Tunnel</strong>, gives you access from anywhere
          with HTTPS and nothing publicly reachable. Port-forwarding this straight to the internet
          puts a hand-rolled login on a machine in your house in front of the whole world.
        </p>
        {!status.isSecureConnection && (
          <p className="text-gold">This page is on plain HTTP right now.</p>
        )}
      </div>
    </section>
  )
}

// ------------------------------------------------------------------- API key

function ApiKeyCard({ settings, onChanged }: { settings: AppSettings; onChanged: () => void }) {
  const [value, setValue] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'warn' | 'error'; text: string } | null>(null)

  async function save() {
    if (!value.trim()) return
    setBusy(true)
    setMessage(null)
    try {
      const res = await api.saveApiKey(value.trim())
      setValue('')
      setMessage(
        res.reachable
          ? { kind: 'ok', text: 'Saved, and pokemontcg.io responded.' }
          : {
              kind: 'warn',
              text: "Saved, but pokemontcg.io didn't respond just now. That's usually the API " +
                'having a wobble rather than a problem with your key.',
            },
      )
      onChanged()
    } catch (e) {
      setMessage({ kind: 'error', text: e instanceof Error ? e.message : 'Could not save that key' })
    } finally {
      setBusy(false)
    }
  }

  async function clear() {
    setBusy(true)
    setMessage(null)
    try {
      await api.clearApiKey()
      setMessage({ kind: 'ok', text: 'Saved key removed.' })
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  const status = settings.apiKey

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">pokemontcg.io API key</h2>
      <p className="mt-1 text-sm text-mute">
        The app works without a key but is heavily rate limited — searches and imports will crawl.
        A free key from{' '}
        <a href="https://dev.pokemontcg.io" target="_blank" rel="noreferrer" className="text-arc hover:underline">
          dev.pokemontcg.io
        </a>{' '}
        raises the limits considerably.
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-2 rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
        <span className={status.configured ? 'text-mint' : 'text-gold'}>
          {status.configured ? '● Key configured' : '○ No key set'}
        </span>
        {status.masked && <code className="font-mono text-xs text-mute">{status.masked}</code>}
        <span className="text-xs text-mute">· {status.source}</span>
      </div>

      <div className="mt-3 flex flex-wrap gap-2">
        <input
          type="password"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && save()}
          placeholder="Paste a new key to replace it"
          className={`${field} min-w-[240px] flex-1 font-mono`}
          autoComplete="off"
        />
        <button
          onClick={save}
          disabled={busy || !value.trim()}
          className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
        >
          {busy ? 'Saving…' : 'Save key'}
        </button>
        {status.configured && status.source === 'Saved in this app' && (
          <button
            onClick={clear}
            disabled={busy}
            className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-rose disabled:opacity-40"
          >
            Remove
          </button>
        )}
      </div>

      {message && (
        <p
          className={`mt-2 text-sm ${
            message.kind === 'ok' ? 'text-mint' : message.kind === 'warn' ? 'text-gold' : 'text-rose'
          }`}
        >
          {message.text}
        </p>
      )}

      <p className="mt-3 text-xs text-mute">
        The key is stored in your local database and takes effect immediately, no restart needed.
        It's only ever shown back partly masked.
      </p>
      <p className="mt-2 text-xs text-mute">
        <span className="text-gold">Note:</span> pokemontcg.io provides no way to check whether a
        key is genuine — a correct key, a mistyped one and no key at all all get the same response.
        Saving only confirms the service answered, not that your key is right. If searches stay slow
        or rate-limited, re-check the key you pasted.
      </p>
      <p className="mt-2 text-xs text-mute">
        With no password set, anyone who can reach this server on your network can read your
        collection and change this setting.
      </p>
    </section>
  )
}

// --------------------------------------------------------------------- eBay

function EbayCard({ settings, onChanged }: { settings: AppSettings; onChanged: () => void }) {
  const [clientId, setClientId] = useState('')
  const [clientSecret, setClientSecret] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ kind: 'ok' | 'warn' | 'error'; text: string } | null>(null)

  const ready = clientId.trim() !== '' && clientSecret.trim() !== ''

  async function save() {
    if (!ready) return
    setBusy(true)
    setMessage(null)
    try {
      const res = await api.saveEbayCredentials(clientId.trim(), clientSecret.trim())
      setClientId('')
      setClientSecret('')
      setMessage(
        res.reachable
          ? { kind: 'ok', text: 'Saved, and eBay accepted the credentials.' }
          : {
              kind: 'warn',
              text: 'Saved, but eBay would not issue a token with them. Check you copied the ' +
                'production keys rather than the sandbox pair.',
            },
      )
      onChanged()
    } catch (e) {
      setMessage({ kind: 'error', text: e instanceof Error ? e.message : 'Could not save those credentials' })
    } finally {
      setBusy(false)
    }
  }

  async function clear() {
    setBusy(true)
    setMessage(null)
    try {
      await api.clearEbayCredentials()
      setMessage({ kind: 'ok', text: 'Saved credentials removed.' })
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  const status = settings.ebay

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">eBay pricing for sealed and slabs</h2>
      <p className="mt-1 text-sm text-mute">
        Sealed product and graded slabs aren't in the card catalogue, so they have no price of
        their own. With eBay credentials the app searches live listings for each one daily and
        records what it finds, giving them a tracked value and a chart like any other item.
        Create an application at{' '}
        <a
          href="https://developer.ebay.com/my/keys"
          target="_blank"
          rel="noreferrer"
          className="text-arc hover:underline"
        >
          developer.ebay.com
        </a>{' '}
        and paste the production App ID and Cert ID below.
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-2 rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
        <span className={status.configured ? 'text-mint' : 'text-gold'}>
          {status.configured ? '● Credentials configured' : '○ Not set'}
        </span>
        {status.masked && <code className="font-mono text-xs text-mute">{status.masked}</code>}
        <span className="text-xs text-mute">· {status.source}</span>
      </div>

      <div className="mt-3 space-y-2">
        <input
          type="text"
          value={clientId}
          onChange={(e) => setClientId(e.target.value)}
          placeholder="App ID (Client ID)"
          className={`${field} w-full font-mono`}
          autoComplete="off"
        />
        <input
          type="password"
          value={clientSecret}
          onChange={(e) => setClientSecret(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && save()}
          placeholder="Cert ID (Client Secret)"
          className={`${field} w-full font-mono`}
          autoComplete="off"
        />
      </div>

      <div className="mt-3 flex flex-wrap gap-2">
        <button
          onClick={save}
          disabled={busy || !ready}
          className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
        >
          {busy ? 'Saving…' : 'Save credentials'}
        </button>
        {status.configured && status.source === 'Saved in this app' && (
          <button
            onClick={clear}
            disabled={busy}
            className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-rose disabled:opacity-40"
          >
            Remove
          </button>
        )}
      </div>

      {message && (
        <p
          className={`mt-2 text-sm ${
            message.kind === 'ok' ? 'text-mint' : message.kind === 'warn' ? 'text-gold' : 'text-rose'
          }`}
        >
          {message.text}
        </p>
      )}

      <p className="mt-3 text-xs text-mute">
        <span className="text-gold">These are asking prices, not sold prices.</span> eBay retired
        its public completed-listings feed, and the sold-data API that replaced it is closed to new
        applicants. What you get here is the median of the cheaper live listings for the item —
        a fair guide to what one is going for, but it will read high against what things actually
        sell for, and it's labelled "eBay (asking)" everywhere it appears so it can't be mistaken
        for a sold comp.
      </p>
      <p className="mt-2 text-xs text-mute">
        Only custom items are priced this way. Ordinary cards already have TCGplayer and Cardmarket
        figures, which are better data and cost no quota.
      </p>
      <p className="mt-2 text-xs text-mute">
        A value you've typed in yourself still wins over anything fetched here — clear it on the
        item to fall back to the tracked price.
      </p>
    </section>
  )
}

// ------------------------------------------------------- offline card catalogue

function CatalogueCard() {
  const [status, setStatus] = useState<CatalogueStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = () => api.catalogue().then(setStatus).catch(() => {})

  useEffect(() => {
    void load()
  }, [])

  const running = status?.progress?.state === 'sets' || status?.progress?.state === 'images'

  // Poll only while something is actually running. A settings page has no business
  // making a request every second forever.
  useEffect(() => {
    if (!running) return
    const id = setInterval(() => void load(), 1000)
    return () => clearInterval(id)
  }, [running])

  async function run(action: () => Promise<CatalogueStatus>) {
    setBusy(true)
    try {
      setStatus(await action())
    } finally {
      setBusy(false)
    }
  }

  if (!status) return <div className="skeleton h-40 rounded-xl" />

  const progress = status.progress
  const pct = progress && progress.total > 0 ? Math.round((progress.done / progress.total) * 100) : 0
  const downloaded = status.cards > 0

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">Offline card catalogue</h2>
      <p className="mt-1 text-sm text-mute">
        Keeps every card and its thumbnail on this machine, so finding a card to add never waits
        on pokemontcg.io. It holds no prices — those still come from the price sources on their
        own daily schedule, and nothing here has an opinion about what a card is worth.
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-2 rounded-lg bg-white/[0.03] px-3 py-2 text-sm">
        <span className={downloaded ? 'text-mint' : 'text-gold'}>
          {downloaded ? '● Downloaded' : '○ Not downloaded'}
        </span>
        {downloaded && (
          <span className="text-xs text-mute">
            · {status.cards.toLocaleString()} cards · {status.images.toLocaleString()} images ·{' '}
            {bytes(status.imageBytes)}
          </span>
        )}
        {status.downloadedAt && <span className="text-xs text-mute">· {when(status.downloadedAt)}</span>}
      </div>

      {progress && (
        <div className="mt-3">
          <div className="flex items-baseline justify-between gap-3 text-xs text-mute">
            <span className="truncate">
              {progress.state === 'sets' && 'Fetching card data'}
              {progress.state === 'images' && 'Downloading artwork'}
              {progress.state === 'done' && 'Finished'}
              {progress.state === 'cancelled' && 'Stopped'}
              {progress.state === 'failed' && <span className="text-rose">Failed</span>}
              {progress.detail ? ` · ${progress.detail}` : ''}
            </span>
            {progress.total > 0 && (
              <span className="shrink-0 tabular-nums">
                {progress.done.toLocaleString()} / {progress.total.toLocaleString()}
              </span>
            )}
          </div>
          {running && (
            <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-white/10">
              <div className="h-full rounded-full bg-arc transition-all" style={{ width: `${pct}%` }} />
            </div>
          )}
          {progress.error && <p className="mt-1 text-sm text-rose">{progress.error}</p>}
        </div>
      )}

      <div className="mt-3 flex flex-wrap gap-2">
        {running ? (
          <button
            onClick={() => run(api.cancelCatalogue)}
            disabled={busy}
            className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-rose disabled:opacity-40"
          >
            Stop
          </button>
        ) : (
          <button
            onClick={() => run(() => api.downloadCatalogue(true))}
            disabled={busy}
            className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
          >
            {downloaded ? 'Download again' : 'Download catalogue'}
          </button>
        )}

        {downloaded && !running && !confirmDelete && (
          <button
            onClick={() => setConfirmDelete(true)}
            disabled={busy}
            className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-rose disabled:opacity-40"
          >
            Delete
          </button>
        )}

        {confirmDelete && (
          <>
            <button
              onClick={async () => {
                setConfirmDelete(false)
                await run(api.deleteCatalogue)
              }}
              disabled={busy}
              className="rounded-lg bg-rose px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
            >
              Delete it
            </button>
            <button
              onClick={() => setConfirmDelete(false)}
              className="rounded-lg border border-edge px-4 py-2 text-sm text-mute transition hover:text-bright"
            >
              Keep it
            </button>
          </>
        )}
      </div>

      <label className="mt-3 flex items-start gap-2 text-sm text-mute">
        <input
          type="checkbox"
          checked={status.enabled}
          disabled={busy}
          onChange={(e) => run(() => api.setCatalogueEnabled(e.target.checked))}
          className="mt-0.5 h-4 w-4 accent-[color:var(--color-arc)]"
        />
        <span>
          Use it when adding cards
          <span className="block text-xs">
            Searching reads this machine instead of the API. Turn it off to go back to live results
            without deleting anything, and it stays inactive on its own until a catalogue has
            actually been downloaded.
          </span>
        </span>
      </label>

      <p className="mt-3 text-xs text-mute">
        Around 415 MB and a couple of minutes on a decent connection. Thumbnails are re-encoded to
        WebP on the way in, which is what turns roughly 3 GB of source PNGs into that.
      </p>
      <p className="mt-2 text-xs text-mute">
        A card added from here arrives with no price and its most likely printing. The daily price
        refresh re-fetches everything you own, so both are corrected on its next run.
      </p>
    </section>
  )
}

// ------------------------------------------------------------ data location

function DataCard({ settings }: { settings: AppSettings }) {
  const [copied, setCopied] = useState(false)

  return (
    <section className="panel rounded-xl p-4">
      <h2 className="font-medium text-bright">Where your collection is stored</h2>
      <p className="mt-1 text-sm text-mute">
        Deliberately outside the application folder, so updating, moving or reinstalling the app
        can't touch your cards.
      </p>

      <div className="mt-3 flex flex-wrap items-center gap-2">
        <code className="min-w-0 flex-1 truncate rounded-lg bg-abyss px-3 py-2 font-mono text-xs text-bright">
          {settings.dataDirectory}
        </code>
        <button
          onClick={() => {
            void navigator.clipboard?.writeText(settings.dataDirectory)
            setCopied(true)
            setTimeout(() => setCopied(false), 1500)
          }}
          className="rounded-lg border border-edge px-3 py-2 text-xs text-mute transition hover:text-bright"
        >
          {copied ? 'Copied' : 'Copy path'}
        </button>
      </div>

      {settings.migratedFromLegacy && (
        <p className="mt-3 rounded-lg bg-mint/10 px-3 py-2 text-xs text-mint">
          A collection from an older version was found inside the app folder and copied here. The
          original was left untouched at <code className="font-mono">{settings.legacyDirectory}</code> —
          delete it once you're happy everything is present.
        </p>
      )}
    </section>
  )
}

// ---------------------------------------------------------------- backups

function BackupsCard({ settings, onChanged }: { settings: AppSettings; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const [note, setNote] = useState<string | null>(null)

  async function backupNow() {
    setBusy(true)
    setNote(null)
    try {
      const b = await api.createBackup()
      setNote(`Saved ${b.name}`)
      onChanged()
    } catch (e) {
      setNote(e instanceof Error ? e.message : 'Backup failed')
    } finally {
      setBusy(false)
    }
  }

  async function remove(name: string) {
    setBusy(true)
    try {
      await api.deleteBackup(name)
      onChanged()
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="panel rounded-xl p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="font-medium text-bright">Backups</h2>
          <p className="mt-1 text-sm text-mute">
            Taken automatically when the app version changes and once a day otherwise. The ten most
            recent are kept.
          </p>
        </div>
        <button
          onClick={backupNow}
          disabled={busy}
          className="rounded-lg bg-arc px-4 py-2 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
        >
          {busy ? 'Working…' : 'Back up now'}
        </button>
      </div>

      {note && <p className="mt-2 text-sm text-mint">{note}</p>}

      <div className="mt-3 space-y-2">
        {settings.backups.length === 0 ? (
          <p className="text-sm text-mute">No backups yet.</p>
        ) : (
          settings.backups.map((b) => (
            <div
              key={b.name}
              className="flex flex-wrap items-center gap-3 rounded-lg bg-white/[0.03] px-3 py-2 text-sm"
            >
              <div className="min-w-0 flex-1">
                <div className="truncate text-bright">{when(b.createdUtc)}</div>
                <div className="truncate text-xs text-mute">
                  {bytes(b.sizeBytes)} · {b.reason}
                </div>
              </div>
              <a
                href={`/api/backups/${encodeURIComponent(b.name)}`}
                download
                className="rounded-md border border-edge px-2.5 py-1 text-xs text-mute transition hover:text-bright"
              >
                Download
              </a>
              <button
                onClick={() => remove(b.name)}
                disabled={busy}
                className="rounded-md px-2 py-1 text-xs text-mute transition hover:text-rose disabled:opacity-40"
              >
                Delete
              </button>
            </div>
          ))
        )}
      </div>

      <p className="mt-3 text-xs text-mute">
        To restore one: stop the app, replace <code className="font-mono">vault.db</code> in the
        folder above with the backup file (renaming it to <code className="font-mono">vault.db</code>),
        delete any <code className="font-mono">vault.db-wal</code> and{' '}
        <code className="font-mono">vault.db-shm</code> alongside it, then start the app again.
      </p>
    </section>
  )
}
