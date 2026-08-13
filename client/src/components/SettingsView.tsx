import { useEffect, useState } from 'react'
import { api } from '../api'
import type { AppSettings, AuthStatus, SessionInfo } from '../types'

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
      <SecurityCard onAuthChanged={onAuthChanged} />
      <ApiKeyCard settings={settings} onChanged={load} />
      <DataCard settings={settings} />
      <BackupsCard settings={settings} onChanged={load} />
    </div>
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
