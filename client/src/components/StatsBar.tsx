import { money } from '../api'
import type { CollectionStats } from '../types'

function Stat({
  label,
  value,
  sub,
  accent,
}: {
  label: string
  value: string
  sub?: string
  accent?: string
}) {
  return (
    <div className="panel rounded-xl px-4 py-3">
      <div className="text-[11px] tracking-wider text-mute uppercase">{label}</div>
      <div className={`mt-1 text-2xl font-semibold tabular-nums ${accent ?? 'text-bright'}`}>{value}</div>
      {sub && <div className="mt-0.5 truncate text-xs text-mute">{sub}</div>}
    </div>
  )
}

/** Value-over-time line, drawn from our own price snapshots. */
function Sparkline({ points }: { points: { date: string; value: number }[] }) {
  if (points.length < 2) {
    return (
      <div className="panel flex items-center justify-center rounded-xl px-4 py-3 text-center text-xs text-mute">
        Value history builds up as the app records daily prices — check back tomorrow.
      </div>
    )
  }

  const w = 260
  const h = 64
  const values = points.map((p) => p.value)
  const min = Math.min(...values)
  const max = Math.max(...values)
  const span = max - min || 1

  const coords = points.map((p, i) => {
    const x = (i / (points.length - 1)) * w
    const y = h - ((p.value - min) / span) * (h - 8) - 4
    return `${x.toFixed(1)},${y.toFixed(1)}`
  })

  const first = values[0]
  const last = values[values.length - 1]
  const up = last >= first
  const stroke = up ? 'var(--color-mint)' : 'var(--color-rose)'
  const delta = first === 0 ? 0 : ((last - first) / first) * 100

  return (
    <div className="panel rounded-xl px-4 py-3">
      <div className="flex items-baseline justify-between">
        <div className="text-[11px] tracking-wider text-mute uppercase">Value trend</div>
        <div className={`text-xs font-medium tabular-nums ${up ? 'text-mint' : 'text-rose'}`}>
          {up ? '▲' : '▼'} {Math.abs(delta).toFixed(1)}%
        </div>
      </div>
      <svg viewBox={`0 0 ${w} ${h}`} className="mt-2 h-16 w-full" preserveAspectRatio="none">
        <polyline
          points={coords.join(' ')}
          fill="none"
          stroke={stroke}
          strokeWidth="2"
          strokeLinejoin="round"
          strokeLinecap="round"
          vectorEffect="non-scaling-stroke"
        />
      </svg>
    </div>
  )
}

export function StatsBar({ stats }: { stats: CollectionStats | null }) {
  if (!stats) {
    return (
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
        {Array.from({ length: 5 }).map((_, i) => (
          <div key={i} className="skeleton h-[86px] rounded-xl" />
        ))}
      </div>
    )
  }

  const gain = stats.totalPaid > 0 ? stats.totalMarketValue - stats.totalPaid : null

  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
      <Stat
        label="Collection value"
        value={money(stats.totalMarketValue)}
        sub={stats.totalPaid > 0 ? `${money(stats.totalPaid)} paid` : 'Add purchase prices to track gains'}
        accent="text-gold"
      />
      <Stat label="Cards" value={stats.totalCards.toLocaleString()} sub={`${stats.distinctCards} unique`} />
      <Stat
        label="Unrealised gain"
        value={gain === null ? '—' : money(gain)}
        sub={gain === null ? 'No purchase data yet' : gain >= 0 ? 'Up on cost' : 'Down on cost'}
        accent={gain === null ? undefined : gain >= 0 ? 'text-mint' : 'text-rose'}
      />
      <Stat
        label="Best performer"
        value={stats.biggestGainAmount == null ? '—' : money(stats.biggestGainAmount)}
        sub={stats.biggestGainCardName ?? 'Needs a purchase price'}
        accent={stats.biggestGainAmount == null ? undefined : 'text-mint'}
      />
      <Sparkline points={stats.valueHistory} />
    </div>
  )
}
