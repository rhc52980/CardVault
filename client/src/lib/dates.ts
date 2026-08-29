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
  { key: '1', label: 'Added today', days: 1 },
  { key: '7', label: 'Added in 7 days', days: 7 },
  { key: '30', label: 'Added in 30 days', days: 30 },
  { key: '90', label: 'Added in 90 days', days: 90 },
] as const

/** True when the entry was added within the last `days` days. */
export function addedWithin(iso: string, days: number) {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return false
  return Date.now() - then < days * 86_400_000
}
