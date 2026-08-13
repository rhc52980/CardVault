import type {
  AddEntryRequest,
  CollectionItem,
  CollectionStats,
  CustomItemRequest,
  FullCard,
  ImportJob,
  SearchCard,
  SetCard,
  SetSummary,
  UpdateEntryRequest,
} from './types'

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new Error(body || `${res.status} ${res.statusText}`)
  }
  return res.json() as Promise<T>
}

export interface SearchResponse {
  data: SearchCard[]
  totalCount: number
  page: number
}

export const api = {
  search(query: string, page = 1, pageSize = 24, signal?: AbortSignal) {
    const params = new URLSearchParams({ q: query, page: String(page), pageSize: String(pageSize) })
    return fetch(`/api/search?${params}`, { signal }).then(json<SearchResponse>)
  },

  card(id: string) {
    return fetch(`/api/cards/${encodeURIComponent(id)}`).then(json<FullCard>)
  },

  collection() {
    return fetch('/api/collection').then(json<CollectionItem[]>)
  },

  stats() {
    return fetch('/api/collection/stats').then(json<CollectionStats>)
  },

  add(entry: AddEntryRequest) {
    return fetch('/api/collection', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(entry),
    }).then(json<{ id: number }>)
  },

  /**
   * Creates a sealed box, slab or other item the catalogue doesn't carry. Sent as
   * multipart when an image file is attached, JSON otherwise.
   */
  addCustom(item: CustomItemRequest, image?: File | null) {
    if (image) {
      const form = new FormData()
      for (const [k, v] of Object.entries(item)) {
        if (v !== null && v !== undefined && v !== '') form.append(k, String(v))
      }
      form.append('image', image)
      return fetch('/api/custom', { method: 'POST', body: form }).then(
        json<{ cardId: string; entryId: number }>,
      )
    }

    return fetch('/api/custom', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(item),
    }).then(json<{ cardId: string; entryId: number }>)
  },

  async update(id: number, patch: UpdateEntryRequest) {
    const res = await fetch(`/api/collection/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    })
    if (!res.ok) throw new Error(`Could not update entry ${id}`)
  },

  async remove(id: number) {
    const res = await fetch(`/api/collection/${id}`, { method: 'DELETE' })
    if (!res.ok) throw new Error(`Could not delete entry ${id}`)
  },

  snapshot() {
    return fetch('/api/prices/snapshot', { method: 'POST' }).then(json<{ captured: number }>)
  },

  sets() {
    return fetch('/api/sets').then(json<SetSummary[]>)
  },

  setCards(setId: string) {
    return fetch(`/api/sets/${encodeURIComponent(setId)}/cards`).then(json<SetCard[]>)
  },

  startImport(csv: string) {
    return fetch('/api/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ csv }),
    }).then(json<{ jobId: string; total: number; unmappedColumns: string[] }>)
  },

  importJob(jobId: string) {
    return fetch(`/api/import/${jobId}`).then(json<ImportJob>)
  },

  commitImport(jobId: string, rows: AddEntryRequest[]) {
    return fetch(`/api/import/${jobId}/commit`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rows }),
    }).then(json<{ added: number }>)
  },
}

/** Cached card art served off our own disk rather than hot-linking every render. */
export function cardImage(cardId: string, size: 'small' | 'large' = 'small') {
  return `/img/${encodeURIComponent(cardId)}/${size}`
}

export function money(value?: number | null, fallback = '—') {
  if (value === null || value === undefined) return fallback
  return value.toLocaleString('en-US', { style: 'currency', currency: 'USD' })
}
