import type {
  AddEntryRequest,
  AddWantRequest,
  ApiKeyStatus,
  AppSettings,
  BackupInfo,
  CardHistory,
  CollectionItem,
  CollectionStats,
  CustomItemRequest,
  FullCard,
  ImportJob,
  SaleRecord,
  SearchCard,
  SellRequest,
  SetCard,
  SetSummary,
  UpdateEntryRequest,
  UpdateWantRequest,
  WantItem,
} from './types'

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) throw new Error(await errorMessage(res))
  return res.json() as Promise<T>
}

/**
 * The server reports failures as {"error": "..."}. Unwrap it here so callers can
 * show err.message directly — otherwise raw JSON ends up on screen.
 */
async function errorMessage(res: Response): Promise<string> {
  const body = await res.text().catch(() => '')
  if (body) {
    try {
      const parsed = JSON.parse(body)
      if (typeof parsed?.error === 'string') return parsed.error
    } catch {
      // Not JSON — fall through and use the raw body.
    }
    return body
  }
  return `${res.status} ${res.statusText}`
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

  wants() {
    return fetch('/api/wants').then(json<WantItem[]>)
  },

  addWant(req: AddWantRequest) {
    return fetch('/api/wants', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    }).then(json<{ id: number }>)
  },

  async updateWant(id: number, patch: UpdateWantRequest) {
    const res = await fetch(`/api/wants/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    })
    if (!res.ok) throw new Error('Could not update that want')
  },

  async removeWant(id: number) {
    const res = await fetch(`/api/wants/${id}`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not remove that want')
  },

  /** Found one — moves it from the want list into the collection. */
  acquireWant(id: number, entry: Omit<AddEntryRequest, 'cardId'>) {
    return fetch(`/api/wants/${id}/acquire`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cardId: '', ...entry }),
    }).then(json<{ entryId: number }>)
  },

  cardHistory(cardId: string) {
    return fetch(`/api/cards/${encodeURIComponent(cardId)}/history`).then(json<CardHistory>)
  },

  sales() {
    return fetch('/api/sales').then(json<SaleRecord[]>)
  },

  sell(entryId: number, req: SellRequest) {
    return fetch(`/api/collection/${entryId}/sell`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    }).then(json<{ saleId: number }>)
  },

  async deleteSale(id: number) {
    const res = await fetch(`/api/sales/${id}`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not delete that sale record')
  },

  settings() {
    return fetch('/api/settings').then(json<AppSettings>)
  },

  saveApiKey(apiKey: string) {
    return fetch('/api/settings/api-key', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ apiKey }),
    }).then(json<{ saved: boolean; reachable: boolean; apiKey?: ApiKeyStatus }>)
  },

  clearApiKey() {
    return fetch('/api/settings/api-key', { method: 'DELETE' }).then(json<{ apiKey: ApiKeyStatus }>)
  },

  createBackup() {
    return fetch('/api/backups', { method: 'POST' }).then(json<BackupInfo>)
  },

  async deleteBackup(name: string) {
    const res = await fetch(`/api/backups/${encodeURIComponent(name)}`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not delete that backup')
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
