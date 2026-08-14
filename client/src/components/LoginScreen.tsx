import { useState } from 'react'
import logoUrl from '../assets/logo.png'
import { api } from '../api'
import type { AuthStatus } from '../types'

const field =
  'w-full rounded-lg border border-edge bg-abyss px-3 py-2.5 text-sm text-bright outline-none focus:border-arc focus:ring-1 focus:ring-arc'

export function LoginScreen({ status, onSignedIn }: { status: AuthStatus; onSignedIn: () => void }) {
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.login(password)
      setPassword('')
      onSignedIn()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not sign in')
      setBusy(false)
    }
  }

  return (
    <div className="aurora relative flex min-h-full items-center justify-center px-5 py-16">
      <div className="relative z-10 w-full max-w-sm">
        {/* Room to breathe here, so the full logo gets used rather than a mark. */}
        <div className="mb-6 text-center">
          <img src={logoUrl} alt="CardVault" className="mx-auto h-32 w-32 drop-shadow-2xl" />
          <p className="mt-3 text-sm text-mute">Sign in to see your collection</p>
        </div>

        <form onSubmit={submit} className="panel rounded-2xl p-5">
          <label className="mb-1 block text-[11px] tracking-wider text-mute uppercase" htmlFor="pw">
            Password
          </label>
          <input
            id="pw"
            type="password"
            autoFocus
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            className={field}
          />

          {error && <p className="mt-3 text-sm text-rose">{error}</p>}

          <button
            type="submit"
            disabled={busy || !password}
            className="mt-4 w-full rounded-lg bg-arc py-2.5 text-sm font-medium text-white transition hover:brightness-110 disabled:opacity-40"
          >
            {busy ? 'Signing in…' : 'Sign in'}
          </button>

          {!status.isSecureConnection && (
            <p className="mt-4 rounded-lg bg-gold/10 px-3 py-2 text-xs text-gold">
              This connection isn't encrypted. On your own network that's usually fine, but don't
              sign in over plain HTTP from outside your home — the password is readable in transit.
            </p>
          )}
        </form>

        <p className="mt-4 text-center text-xs text-mute">
          Forgotten it? There's no reset — the password is only stored as a hash. See the README for
          how to clear it directly in the database.
        </p>
      </div>
    </div>
  )
}
