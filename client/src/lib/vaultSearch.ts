import type { CollectionItem } from '../types'

/**
 * Searching your own shelf the way you search the catalogue.
 *
 * The box used to be an OR of substrings against one field at a time, which quietly
 * failed at the two things people actually type. "106/189" is written on the card and
 * matched nothing, because the number is held as "106" and no single field contains
 * the slash. "Purrloin 106" matched nothing either, because no one field holds both
 * words — the name is in one column and the number in another.
 *
 * So a query is a set of terms that must *all* match, each free to match a different
 * field. That is the rule people already expect from every other search box, and it
 * turns "charizard base" and "darkness ablaze 106" into the questions they look like.
 */
export interface VaultQuery {
  /** Collector numbers that must match exactly, from "106/189" or a bare "106". */
  numbers: string[]
  /** Everything else, each of which must appear somewhere on the card. */
  terms: string[]
}

/** Leading zeros are how a card prints its number, not how the vault stores it. */
function normalizeNumber(raw: string) {
  const n = raw.trim().toLowerCase()
  const trimmed = n.replace(/^0+/, '')
  return trimmed.length > 0 ? trimmed : n
}

const NUMBER_OVER_TOTAL = /^([a-z]*\d+[a-z]*)\/(\d+|[a-z]+\d+)$/i

export function parseVaultQuery(raw: string): VaultQuery {
  const numbers: string[] = []
  const terms: string[] = []

  for (const word of raw.trim().toLowerCase().split(/\s+/)) {
    if (!word) continue

    const pair = NUMBER_OVER_TOTAL.exec(word)
    if (pair) {
      // The denominator is deliberately dropped rather than checked. The vault
      // doesn't hold a card's printed total — only the catalogue does — so there is
      // nothing here to compare it against. Ignoring it means "106/189" finds your
      // 106s, which is the point; enforcing it would mean finding nothing, which is
      // the bug being fixed. The set dropdown is right there if a number collides.
      numbers.push(normalizeNumber(pair[1]))
      continue
    }

    terms.push(word)
  }

  return { numbers, terms }
}

/**
 * The fields a bare word is allowed to match.
 *
 * Set *series* is deliberately absent. Fossil, Jungle and Team Rocket all sit in the
 * series called "Base", so including it quietly turned "gastly base" — as plain a
 * query as this box gets — into five sets' worth of answers, with nothing on screen
 * to explain why a Fossil card was among them. A result you cannot account for from
 * what you can see reads as a broken filter.
 */
function haystack(item: CollectionItem) {
  return [
    item.name,
    item.setName,
    item.rarity,
    item.location,
    item.artist,
    item.grade,
    item.supertype,
    ...item.types,
  ]
}

export function matchesVaultQuery(item: CollectionItem, q: VaultQuery) {
  const number = item.number ? normalizeNumber(item.number) : null

  for (const wanted of q.numbers) {
    if (number !== wanted) return false
  }

  if (q.terms.length === 0) return true

  const fields = haystack(item)
    .filter((f): f is string => !!f)
    .map((f) => f.toLowerCase())

  return q.terms.every((term) => {
    // A bare number means the collector number, and it has to match it exactly:
    // as a substring "4" drags in 40 through 49 and every card numbered 14, 24 or
    // 104, which on a real collection is a third of the shelf. The number is kept
    // out of the text fields for the same reason.
    //
    // It can still match text elsewhere, and that is not redundant — it is what
    // keeps "base set 2" and "team magma vs team aqua" working, where the digit
    // belongs to the set's name rather than to any card's number.
    if (/^\d+$/.test(term)) {
      return number === normalizeNumber(term) || fields.some((f) => f.includes(term))
    }

    return fields.some((f) => f.includes(term)) || number === term
  })
}
