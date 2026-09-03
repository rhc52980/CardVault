/**
 * How long ago something happened, in the words you'd use out loud.
 *
 * Recent things get a relative phrase because that's what you're actually asking
 * ("did this arrive with last week's batch?"), and anything older gets a real date,
 * because "412 days ago" is a number nobody can picture.
 */
export function whenAdded(iso: string) {
  const then = new Date(iso)
  if (Number.isNaN(then.getTime())) return ''

  const days = Math.floor((Date.now() - then.getTime()) / 86_400_000)
  if (days <= 0) return 'today'
  if (days === 1) return 'yesterday'
  if (days < 30) return `${days} days ago`
  return then.toLocaleDateString()
}

/**
 * The windows worth filtering a collection by.
 *
 * Days rather than calendar months: "the last 30 days" is a question you can answer
 * about a shelf, where "in August" needs you to remember what you did in August.
 */
export const ADDED_WINDOWS = [
  { key: 'today', label: 'Added today' },
  { key: '3', label: 'Added in 3 days' },
  { key: '7', label: 'Added in 7 days' },
  { key: '30', label: 'Added in 30 days' },
  { key: '90', label: 'Added in 90 days' },
] as const

/** The window that asks you for two dates instead of picking one for you. */
export const CUSTOM_WINDOW = 'custom'

const DAY = 86_400_000

/** Midnight this morning, in the timezone you're standing in. */
function startOfToday() {
  const d = new Date()
  d.setHours(0, 0, 0, 0)
  return d.getTime()
}

/**
 * True when the entry falls inside one of the named windows.
 *
 * "Today" is the calendar day rather than the last twenty-four hours. A card added
 * at eleven last night was added yesterday, and a filter labelled "today" that
 * lists it is answering a question nobody asked. The rest stay rolling, because
 * "the last 30 days" is the question being asked there and nobody means it to
 * shift by a day at midnight.
 */
export function addedWithin(iso: string, window: string) {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return false

  if (window === 'today') return then >= startOfToday()

  const days = Number(window)
  return Number.isFinite(days) && days > 0 && Date.now() - then < days * DAY
}

/**
 * True when the entry falls inside a range of calendar days, both ends included.
 *
 * Either end may be left empty and simply stops constraining that side — "since
 * March" and "up to March" are both real questions, and demanding the other half
 * of a range you don't care about is busywork. The dates come from a date input,
 * so they are parsed as local days: picking the 5th means the whole of your 5th,
 * not a window that ends mid-afternoon because the entry was stored in UTC.
 */
export function addedBetween(iso: string, from: string, to: string) {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return false

  if (from) {
    const start = new Date(`${from}T00:00:00`).getTime()
    if (!Number.isNaN(start) && then < start) return false
  }

  if (to) {
    const end = new Date(`${to}T23:59:59.999`).getTime()
    if (!Number.isNaN(end) && then > end) return false
  }

  return true
}
