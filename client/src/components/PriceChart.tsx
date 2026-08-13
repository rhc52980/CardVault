import { useEffect, useMemo, useRef, useState } from 'react'
import { api, money } from '../api'
import { prettyVariant } from '../lib/cardStyles'
import type { CardHistory, PriceSeries } from '../types'

/**
 * Categorical hues for the chart, in fixed order — never cycled.
 *
 * These are darker steps of the app's brand hues: the UI colours themselves sit
 * well outside the lightness band a mark needs against a dark chart surface, and
 * this set is validated for lightness, chroma, colourblind separation and contrast
 * rather than picked by eye.
 */
const SERIES_COLORS = ['#5b82ee', '#19a06f', '#b98a20', '#d9506b'] as const
const MAX_SERIES = SERIES_COLORS.length

const SURFACE = '#161a2b'
const GRID = '#2b3149'

const PAD = { top: 14, right: 16, bottom: 28, left: 60 }
const HEIGHT = 230

interface Plotted {
  variant: string
  color: string
  points: { date: string; market: number; x: number; y: number }[]
}

export function PriceChart({ cardId, ownedVariants }: { cardId: string; ownedVariants?: string[] }) {
  const [history, setHistory] = useState<CardHistory | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [width, setWidth] = useState(560)
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const [showTable, setShowTable] = useState(false)
  const wrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let active = true
    api
      .cardHistory(cardId)
      .then((h) => active && setHistory(h))
      .catch((e) => active && setError(e instanceof Error ? e.message : 'Could not load price history'))
    return () => {
      active = false
    }
  }, [cardId])

  // Render at real pixels rather than scaling a viewBox, so strokes and text
  // keep their intended size at any panel width.
  useEffect(() => {
    const el = wrapRef.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => setWidth(Math.max(280, entry.contentRect.width)))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // Owned printings lead, so the line you care about is the first colour.
  const chosen: PriceSeries[] = useMemo(() => {
    if (!history) return []
    const owned = new Set(ownedVariants ?? history.ownedVariants)
    return [...history.series]
      .sort((a, b) => Number(owned.has(b.variant)) - Number(owned.has(a.variant)))
      .slice(0, MAX_SERIES)
  }, [history, ownedVariants])

  // Every date across the chosen series, so the crosshair can snap to a column.
  const dates = useMemo(
    () => [...new Set(chosen.flatMap((s) => s.points.map((p) => p.date)))].sort(),
    [chosen],
  )

  const geometry = useMemo(() => {
    if (dates.length < 2) return null

    const values = chosen.flatMap((s) => s.points.map((p) => p.market))
    if (values.length === 0) return null

    const rawMin = Math.min(...values)
    const rawMax = Math.max(...values)
    const span = rawMax - rawMin || rawMax || 1
    // Breathing room top and bottom. A price line isn't a magnitude comparison,
    // so it doesn't need a zero baseline — that would flatten every real move.
    const yMin = Math.max(0, rawMin - span * 0.12)
    const yMax = rawMax + span * 0.12

    const plotW = width - PAD.left - PAD.right
    const plotH = HEIGHT - PAD.top - PAD.bottom

    const xFor = (date: string) => {
      const i = dates.indexOf(date)
      return PAD.left + (dates.length === 1 ? plotW / 2 : (i / (dates.length - 1)) * plotW)
    }
    const yFor = (v: number) => PAD.top + plotH - ((v - yMin) / (yMax - yMin || 1)) * plotH

    const plotted: Plotted[] = chosen.map((s, i) => ({
      variant: s.variant,
      color: SERIES_COLORS[i],
      points: s.points.map((p) => ({ ...p, x: xFor(p.date), y: yFor(p.market) })),
    }))

    // Clean, rounded tick values rather than raw data extremes.
    const ticks = niceTicks(yMin, yMax, 4)

    return { plotted, yFor, xFor, plotW, plotH, ticks, yMin, yMax }
  }, [chosen, dates, width])

  if (error) return <p className="text-sm text-rose">{error}</p>

  if (!history) return <div className="skeleton h-[230px] rounded-xl" />

  // --- not enough history yet -------------------------------------------------

  if (dates.length < 2) {
    const only = chosen[0]?.points.at(-1)
    return (
      <div className="rounded-xl border border-edge bg-abyss/60 px-4 py-8 text-center">
        {only ? (
          <>
            <div className="text-2xl font-semibold text-bright">{money(only.market)}</div>
            <p className="mt-1 text-xs text-mute">
              Today's price for {prettyVariant(chosen[0].variant)} — the first reading.
            </p>
            <p className="mx-auto mt-3 max-w-sm text-xs text-mute">
              A line needs at least two days. The app records prices once a day, so this
              chart fills in from tomorrow. pokemontcg.io doesn't publish past prices, so
              history can only start when you do.
            </p>
          </>
        ) : (
          <p className="text-sm text-mute">
            No price history recorded yet. It starts once this card is in your collection.
          </p>
        )}
      </div>
    )
  }

  const g = geometry!
  const hoverDate = hoverIndex != null ? dates[hoverIndex] : null

  function handleMove(e: React.PointerEvent<SVGSVGElement>) {
    const rect = e.currentTarget.getBoundingClientRect()
    const x = e.clientX - rect.left
    const ratio = (x - PAD.left) / (g.plotW || 1)
    const idx = Math.round(ratio * (dates.length - 1))
    setHoverIndex(Math.min(dates.length - 1, Math.max(0, idx)))
  }

  return (
    <div ref={wrapRef} className="relative">
      <svg
        width={width}
        height={HEIGHT}
        role="img"
        aria-label={`Market price over time for ${chosen.map((s) => prettyVariant(s.variant)).join(', ')}`}
        onPointerMove={handleMove}
        onPointerLeave={() => setHoverIndex(null)}
        className="touch-none"
      >
        {/* Gridlines: hairline, solid, one step off the surface — recessive. */}
        {g.ticks.map((t) => (
          <g key={t}>
            <line
              x1={PAD.left}
              x2={width - PAD.right}
              y1={g.yFor(t)}
              y2={g.yFor(t)}
              stroke={GRID}
              strokeWidth={1}
            />
            <text
              x={PAD.left - 10}
              y={g.yFor(t)}
              textAnchor="end"
              dominantBaseline="middle"
              className="fill-mute"
              style={{ fontSize: 11, fontVariantNumeric: 'tabular-nums' }}
            >
              {compactMoney(t)}
            </text>
          </g>
        ))}

        {/* X labels: ends and middle only — enough to orient without clutter. */}
        {[0, Math.floor((dates.length - 1) / 2), dates.length - 1]
          .filter((v, i, a) => a.indexOf(v) === i)
          .map((i) => (
            <text
              key={i}
              x={g.xFor(dates[i])}
              y={HEIGHT - 8}
              textAnchor={i === 0 ? 'start' : i === dates.length - 1 ? 'end' : 'middle'}
              className="fill-mute"
              style={{ fontSize: 11 }}
            >
              {shortDate(dates[i])}
            </text>
          ))}

        {/* Crosshair sits under the marks so it never covers a data point. */}
        {hoverDate && (
          <line
            x1={g.xFor(hoverDate)}
            x2={g.xFor(hoverDate)}
            y1={PAD.top}
            y2={HEIGHT - PAD.bottom}
            stroke={GRID}
            strokeWidth={1}
          />
        )}

        {g.plotted.map((s) => (
          <g key={s.variant}>
            <polyline
              points={s.points.map((p) => `${p.x},${p.y}`).join(' ')}
              fill="none"
              stroke={s.color}
              strokeWidth={2}
              strokeLinejoin="round"
              strokeLinecap="round"
            />
            {/* End marker only — a dot on every point is noise. 2px surface ring
                keeps it legible where lines cross. */}
            {s.points.length > 0 && (
              <circle
                cx={s.points.at(-1)!.x}
                cy={s.points.at(-1)!.y}
                r={4}
                fill={s.color}
                stroke={SURFACE}
                strokeWidth={2}
              />
            )}
            {hoverDate &&
              s.points
                .filter((p) => p.date === hoverDate)
                .map((p) => (
                  <circle
                    key={p.date}
                    cx={p.x}
                    cy={p.y}
                    r={4}
                    fill={s.color}
                    stroke={SURFACE}
                    strokeWidth={2}
                  />
                ))}
          </g>
        ))}
      </svg>

      {/* Tooltip: value leads, series name follows, keyed by a short stroke. */}
      {hoverDate && (
        <div
          className="pointer-events-none absolute z-10 rounded-lg border border-edge bg-abyss/95 px-3 py-2 shadow-xl backdrop-blur"
          style={{
            left: Math.min(Math.max(g.xFor(hoverDate) - 60, 0), Math.max(width - 150, 0)),
            top: PAD.top,
          }}
        >
          <div className="text-[11px] text-mute">{longDate(hoverDate)}</div>
          {g.plotted.map((s) => {
            const p = s.points.find((q) => q.date === hoverDate)
            if (!p) return null
            return (
              <div key={s.variant} className="mt-1 flex items-center gap-2">
                <span style={{ background: s.color }} className="h-0.5 w-3 rounded-full" />
                <span className="text-sm font-medium tabular-nums text-bright">{money(p.market)}</span>
                <span className="text-[11px] text-mute">{prettyVariant(s.variant)}</span>
              </div>
            )
          })}
        </div>
      )}

      {/* Legend for two or more series; a single line is named by the heading. */}
      {g.plotted.length > 1 && (
        <div className="mt-2 flex flex-wrap items-center gap-x-4 gap-y-1">
          {g.plotted.map((s) => (
            <span key={s.variant} className="flex items-center gap-2 text-xs text-mute">
              <span style={{ background: s.color }} className="h-0.5 w-4 rounded-full" />
              {prettyVariant(s.variant)}
            </span>
          ))}
        </div>
      )}

      <div className="mt-2 flex items-center justify-between">
        <p className="text-xs text-mute">
          {dates.length} {dates.length === 1 ? 'day' : 'days'} recorded
          {history.series.length > MAX_SERIES &&
            ` · showing ${MAX_SERIES} of ${history.series.length} printings`}
        </p>
        <button
          onClick={() => setShowTable((v) => !v)}
          className="text-xs text-arc transition hover:underline"
        >
          {showTable ? 'Hide table' : 'Show table'}
        </button>
      </div>

      {/* Every value the tooltip shows is reachable without hovering. */}
      {showTable && (
        <div className="mt-2 max-h-56 overflow-y-auto rounded-lg border border-edge">
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-abyss">
              <tr className="text-left text-mute">
                <th className="px-3 py-1.5 font-normal">Date</th>
                {g.plotted.map((s) => (
                  <th key={s.variant} className="px-3 py-1.5 text-right font-normal">
                    {prettyVariant(s.variant)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {[...dates].reverse().map((d) => (
                <tr key={d} className="border-t border-edge/60">
                  <td className="px-3 py-1.5 text-mute">{d}</td>
                  {g.plotted.map((s) => {
                    const p = s.points.find((q) => q.date === d)
                    return (
                      <td key={s.variant} className="px-3 py-1.5 text-right tabular-nums text-bright">
                        {p ? money(p.market) : '—'}
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// ------------------------------------------------------------------- helpers

/** Rounded tick values (10 / 25 / 50 / 100 …) rather than raw data extremes. */
function niceTicks(min: number, max: number, count: number): number[] {
  const span = max - min
  if (span <= 0) return [min]

  const rough = span / count
  const magnitude = 10 ** Math.floor(Math.log10(rough))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= rough) ?? magnitude * 10

  const ticks: number[] = []
  for (let t = Math.ceil(min / step) * step; t <= max; t += step) ticks.push(Number(t.toFixed(6)))
  return ticks
}

function compactMoney(v: number) {
  if (v >= 1000) return `$${(v / 1000).toFixed(v >= 10000 ? 0 : 1)}k`
  return `$${v.toFixed(v < 10 ? 2 : 0)}`
}

function shortDate(iso: string) {
  const d = new Date(`${iso}T00:00:00`)
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

function longDate(iso: string) {
  const d = new Date(`${iso}T00:00:00`)
  return Number.isNaN(d.getTime())
    ? iso
    : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}
