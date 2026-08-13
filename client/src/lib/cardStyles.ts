/** Type badge colours, roughly matching the energy symbols on the cards themselves. */
const TYPE_COLORS: Record<string, string> = {
  Colorless: 'bg-neutral-400/15 text-neutral-200 ring-neutral-300/30',
  Darkness: 'bg-slate-800/60 text-slate-200 ring-slate-400/30',
  Dragon: 'bg-amber-700/25 text-amber-200 ring-amber-400/30',
  Fairy: 'bg-pink-500/20 text-pink-200 ring-pink-400/30',
  Fighting: 'bg-orange-700/25 text-orange-200 ring-orange-400/30',
  Fire: 'bg-red-500/20 text-red-200 ring-red-400/30',
  Grass: 'bg-emerald-500/20 text-emerald-200 ring-emerald-400/30',
  Lightning: 'bg-yellow-400/20 text-yellow-100 ring-yellow-300/30',
  Metal: 'bg-zinc-400/20 text-zinc-200 ring-zinc-300/30',
  Psychic: 'bg-purple-500/20 text-purple-200 ring-purple-400/30',
  Water: 'bg-sky-500/20 text-sky-200 ring-sky-400/30',
}

export function typeClass(type: string) {
  return TYPE_COLORS[type] ?? 'bg-white/10 text-white/80 ring-white/20'
}

/** Rare pulls get a warmer label so they stand out when scanning the grid. */
export function rarityClass(rarity?: string | null) {
  if (!rarity) return 'text-mute'
  const r = rarity.toLowerCase()
  if (r.includes('secret') || r.includes('hyper') || r.includes('rainbow')) return 'text-fuchsia-300'
  if (r.includes('ultra') || r.includes('illustration')) return 'text-amber-300'
  if (r.includes('holo')) return 'text-sky-300'
  if (r.includes('rare')) return 'text-emerald-300'
  return 'text-mute'
}

/** "reverseHolofoil" -> "Reverse Holofoil" */
export function prettyVariant(variant: string) {
  return variant
    .replace(/([A-Z])/g, ' $1')
    .replace(/^./, (c) => c.toUpperCase())
    .replace(/\s+/g, ' ')
    .trim()
}

export const CONDITIONS = ['NM', 'LP', 'MP', 'HP', 'DMG'] as const

export const CONDITION_LABELS: Record<string, string> = {
  NM: 'Near Mint',
  LP: 'Lightly Played',
  MP: 'Moderately Played',
  HP: 'Heavily Played',
  DMG: 'Damaged',
}
