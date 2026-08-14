/**
 * How the app looks, and where that choice lives.
 *
 * Kept on the device rather than in the vault database. Two reasons: it's a
 * display preference rather than collection data, so a phone and a desktop can
 * reasonably disagree about it; and it has to be applied before the first paint,
 * which a value that needs fetching from the server cannot be.
 */

export type Theme = 'dark' | 'light' | 'poke'
export type Background = 'aurora' | 'none' | 'pokeball' | 'custom'

export const THEMES: { key: Theme; label: string; hint: string; swatch: string[] }[] = [
  {
    key: 'dark',
    label: 'Dark',
    hint: 'The original. Near-black, so the card art carries the colour.',
    swatch: ['#0b0d15', '#6d8bff', '#f2b632'],
  },
  {
    key: 'light',
    label: 'Light',
    hint: 'For a bright room. Accents are darkened so they stay readable on white.',
    swatch: ['#eef0f6', '#3050dd', '#9a6600'],
  },
  {
    key: 'poke',
    label: 'Pokémon',
    hint: 'Pokéball red, Pokémon blue and the logotype yellow, on a deep navy.',
    swatch: ['#071021', '#ee1515', '#ffcb05'],
  },
]

export const BACKGROUNDS: { key: Background; label: string; hint: string }[] = [
  { key: 'aurora', label: 'Ambient glow', hint: 'A soft wash of colour behind the app.' },
  { key: 'none', label: 'Plain', hint: 'Nothing at all behind the cards.' },
  { key: 'pokeball', label: 'Pokéball', hint: 'A large, faint Pokéball. Drawn, not downloaded.' },
  { key: 'custom', label: 'Your own image', hint: 'Any image URL, dimmed so cards stay readable.' },
]

const KEYS = { theme: 'vault.theme', background: 'vault.bg', wallpaper: 'vault.wallpaper' }

export interface Appearance {
  theme: Theme
  background: Background
  /** Only meaningful when background is 'custom'. */
  wallpaper: string
}

export function loadAppearance(): Appearance {
  return {
    theme: (localStorage.getItem(KEYS.theme) as Theme) || 'dark',
    background: (localStorage.getItem(KEYS.background) as Background) || 'aurora',
    wallpaper: localStorage.getItem(KEYS.wallpaper) || '',
  }
}

export function saveAppearance(next: Appearance) {
  localStorage.setItem(KEYS.theme, next.theme)
  localStorage.setItem(KEYS.background, next.background)
  localStorage.setItem(KEYS.wallpaper, next.wallpaper)
  applyAppearance(next)
}

/**
 * Writes the choice onto <html>, which is all the CSS needs — the themes are
 * variable overrides on html[data-theme], so nothing has to re-render.
 */
export function applyAppearance(a: Appearance = loadAppearance()) {
  const root = document.documentElement
  root.dataset.theme = a.theme

  // A custom background with no image would leave a blank scrim over nothing, so
  // it falls back to the glow until a URL is actually given.
  const usable = a.background === 'custom' && !a.wallpaper.trim() ? 'aurora' : a.background
  root.dataset.bg = usable

  // url() built here rather than in CSS so the quoting is done once and a stray
  // quote or bracket in a pasted URL can't break out of the declaration.
  root.style.setProperty(
    '--wallpaper',
    a.wallpaper.trim() ? `url("${a.wallpaper.trim().replace(/["\\]/g, '')}")` : 'none',
  )
}
