import type {
  AddEntryRequest,
  AddWantRequest,
  ApiKeyStatus,
  AppSettings,
  AuthStatus,
  BackupInfo,
  CardHistory,
  CatalogueStatus,
  PriceRefreshProgress,
  CollectionItem,
  CollectionStats,
  CommitResult,
  CustomItemRequest,
  Deck,
  DeckSummary,
  FriendMatches,
  FriendVaultSummary,
  FullCard,
  ImportBatchSummary,
  ImportJob,
  Mover,
  MoverSettings,
  PhotoStatus,
  ReconcileReport,
  SaleRecord,
  SearchCard,
  SharedCard,
  SellRequest,
  SessionInfo,
  SetCard,
  PriceSourceSettings,
  SetSummary,
  UpdateEntryRequest,
  UpdateStatus,
  UpdateWantRequest,
  VaultInfo,
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

function post<T>(url: string, body: unknown): Promise<T> {
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then(json<T>)
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

  /**
   * Compares the vault against a fresher read of the scans. Dry by default — pass
   * apply to write the flags, which is the only thing it ever writes.
   */
  async reconcile(files: File[], apply: boolean) {
    const body = new FormData()
    for (const f of files) body.append('files', f)
    if (apply) body.append('apply', 'true')
    const res = await fetch('/api/reconcile', { method: 'POST', body })
    if (!res.ok) {
      const detail = await res.json().catch(() => null)
      throw new Error(detail?.error ?? 'Could not compare those files')
    }
    return (await res.json()) as ReconcileReport
  },

  async clearFlags() {
    const res = await fetch('/api/reconcile/flags', { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not clear the flags')
    return (await res.json()) as { cleared: number }
  },

  async dismissFlag(entryId: number) {
    const res = await fetch(`/api/collection/${entryId}/flag`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not dismiss that flag')
  },

  movers() {
    return fetch('/api/movers').then(json<{ settings: MoverSettings; cards: Mover[] }>)
  },

  saveMoverSettings(s: MoverSettings) {
    return fetch('/api/movers/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(s),
    }).then(json<MoverSettings>)
  },

  vaults() {
    return fetch('/api/vaults').then(json<{ current: string; vaults: VaultInfo[] }>)
  },

  async createVault(name: string) {
    const res = await fetch('/api/vaults', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    if (!res.ok) {
      const detail = await res.json().catch(() => null)
      throw new Error(detail?.error ?? 'Could not create that collection')
    }
    return (await res.json()) as { id: string }
  },

  async renameVault(id: string, name: string) {
    const res = await fetch(`/api/vaults/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    if (!res.ok) throw new Error('Could not rename that collection')
  },

  async deleteVault(id: string) {
    const res = await fetch(`/api/vaults/${encodeURIComponent(id)}`, { method: 'DELETE' })
    if (!res.ok) {
      const detail = await res.json().catch(() => null)
      throw new Error(detail?.error ?? 'Could not remove that collection')
    }
    return (await res.json()) as { removed: boolean; warning?: string | null }
  },

  async selectVault(id: string) {
    const res = await fetch(`/api/vaults/${encodeURIComponent(id)}/select`, { method: 'POST' })
    if (!res.ok) throw new Error('Could not switch collection')
  },

  friends() {
    return fetch('/api/friends').then(json<FriendVaultSummary[]>)
  },

  friendMatches(id: number) {
    return fetch(`/api/friends/${id}`).then(json<FriendMatches>)
  },

  friendCards(id: number, kind: 'own' | 'want') {
    return fetch(`/api/friends/${id}/cards?kind=${kind}`).then(json<SharedCard[]>)
  },

  async importFriend(file: File, name: string) {
    const body = new FormData()
    body.append('vault', file)
    if (name.trim()) body.append('name', name.trim())
    const res = await fetch('/api/friends', { method: 'POST', body })
    if (!res.ok) {
      const detail = await res.json().catch(() => null)
      throw new Error(detail?.error ?? 'Could not read that vault')
    }
    return (await res.json()) as { id: number }
  },

  async removeFriend(id: number) {
    const res = await fetch(`/api/friends/${id}`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not remove that vault')
  },

  decks() {
    return fetch('/api/decks').then(json<DeckSummary[]>)
  },

  deck(id: number) {
    return fetch(`/api/decks/${id}`).then(json<Deck>)
  },

  createDeck(body: { name?: string; format?: string; notes?: string }) {
    return fetch('/api/decks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).then(json<{ id: number }>)
  },

  async updateDeck(id: number, body: { name?: string; format?: string; notes?: string }) {
    const res = await fetch(`/api/decks/${id}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    if (!res.ok) throw new Error('Could not update that deck')
  },

  async deleteDeck(id: number) {
    const res = await fetch(`/api/decks/${id}`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not delete that deck')
  },

  /** Sets how many copies the deck calls for. Zero takes the card out. */
  async setDeckCard(id: number, cardId: string, quantity: number) {
    const res = await fetch(`/api/decks/${id}/cards`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cardId, quantity }),
    })
    if (!res.ok) throw new Error('Could not change that card')
  },

  photoStatus() {
    return fetch('/api/photos').then(json<PhotoStatus>)
  },

  setPhotosEnabled(enabled: boolean) {
    return fetch('/api/photos/enabled', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    }).then(json<PhotoStatus>)
  },

  async deleteAllPhotos() {
    const res = await fetch('/api/photos', { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not remove the photos')
    return (await res.json()) as { removed: number; status: PhotoStatus }
  },

  /** The photo for one entry. Cache-busted so a replacement shows immediately. */
  photoUrl(entryId: number, stamp?: number) {
    return `/api/collection/${entryId}/photo${stamp ? `?v=${stamp}` : ''}`
  },

  async attachPhoto(entryId: number, file: File) {
    const body = new FormData()
    body.append('photo', file)
    const res = await fetch(`/api/collection/${entryId}/photo`, { method: 'POST', body })
    if (!res.ok) {
      const detail = await res.json().catch(() => null)
      throw new Error(detail?.error ?? 'Could not save that photo')
    }
  },

  async detachPhoto(entryId: number) {
    const res = await fetch(`/api/collection/${entryId}/photo`, { method: 'DELETE' })
    if (!res.ok) throw new Error('Could not remove that photo')
  },

  /** One edit applied to a whole selection. Returns how many rows it touched. */
  async updateMany(ids: number[], patch: UpdateEntryRequest) {
    const res = await fetch('/api/collection/bulk', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids, update: patch }),
    })
    if (!res.ok) throw new Error('Could not update those cards')
    return (await res.json()) as { changed: number }
  },

  async removeMany(ids: number[]) {
    const res = await fetch('/api/collection/bulk/remove', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids }),
    })
    if (!res.ok) throw new Error('Could not remove those cards')
    return (await res.json()) as { removed: number }
  },

  /**
   * Starts a refresh; poll refreshProgress for how it's getting on.
   *
   * `onlyMissing` limits it to cards with no recorded price, which after an import is
   * the few that arrived without one — seconds rather than the full sweep's minutes.
   */
  refreshPrices(onlyMissing = false) {
    const query = onlyMissing ? '?onlyMissing=true' : ''
    return fetch(`/api/prices/snapshot${query}`, { method: 'POST' }).then(json<PriceRefreshProgress>)
  },

  refreshProgress() {
    return fetch('/api/prices/snapshot').then(json<PriceRefreshProgress>)
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

  priceSources() {
    return fetch('/api/prices/sources').then(json<PriceSourceSettings>)
  },

  async setPreferredPriceSource(source: string) {
    const res = await fetch('/api/prices/sources/preferred', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ source }),
    })
    if (!res.ok) throw new Error(await res.text())
  },

  authStatus() {
    return fetch('/api/auth/status').then(json<AuthStatus>)
  },

  setupPassword(password: string) {
    return post<{ enabled: boolean }>('/api/auth/setup', { password })
  },

  login(password: string) {
    return post<{ authenticated: boolean }>('/api/auth/login', { password })
  },

  logout() {
    return post<{ authenticated: boolean }>('/api/auth/logout', {})
  },

  changePassword(currentPassword: string, newPassword: string) {
    return post<{ changed: boolean }>('/api/auth/password', { currentPassword, newPassword })
  },

  disableAuth(password: string) {
    return post<{ enabled: boolean }>('/api/auth/disable', { password })
  },

  sessions() {
    return fetch('/api/auth/sessions').then(json<SessionInfo[]>)
  },

  revokeOtherSessions() {
    return post<{ revoked: boolean }>('/api/auth/sessions/revoke-others', {})
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

  saveEbayCredentials(clientId: string, clientSecret: string) {
    return fetch('/api/settings/ebay', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ clientId, clientSecret }),
    }).then(json<{ saved: boolean; reachable: boolean; ebay?: ApiKeyStatus }>)
  },

  clearEbayCredentials() {
    return fetch('/api/settings/ebay', { method: 'DELETE' }).then(json<{ ebay: ApiKeyStatus }>)
  },

  saveScrydexCredentials(apiKey: string, teamId: string) {
    return fetch('/api/settings/scrydex', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ apiKey, teamId }),
    }).then(json<{ saved: boolean; reachable: boolean; scrydex?: ApiKeyStatus }>)
  },

  clearScrydexCredentials() {
    return fetch('/api/settings/scrydex', { method: 'DELETE' }).then(json<{ scrydex: ApiKeyStatus }>)
  },

  setUpdateCheck(enabled: boolean) {
    return post<UpdateStatus>('/api/settings/update-check', { enabled })
  },

  catalogue() {
    return fetch('/api/catalogue').then(json<CatalogueStatus>)
  },

  downloadCatalogue(includeImages: boolean) {
    return post<CatalogueStatus>('/api/catalogue/download', { includeImages })
  },

  cancelCatalogue() {
    return post<CatalogueStatus>('/api/catalogue/cancel', {})
  },

  setCatalogueEnabled(enabled: boolean) {
    return fetch('/api/catalogue/enabled', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    }).then(json<CatalogueStatus>)
  },

  deleteCatalogue() {
    return fetch('/api/catalogue', { method: 'DELETE' }).then(json<CatalogueStatus>)
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

  /** Renames the vault. An empty name restores the default. */
  setVaultName(name: string) {
    return fetch('/api/settings/vault-name', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    }).then(json<{ vaultName: string }>)
  },

  /** Imports that still have cards in the collection, newest first. */
  importBatches() {
    return fetch('/api/imports').then(json<ImportBatchSummary[]>)
  },

  acknowledgeImport(id: string) {
    return fetch(`/api/imports/${encodeURIComponent(id)}/acknowledge`, { method: 'POST' })
      .then(json<ImportBatchSummary>)
  },

  acknowledgeAllImports() {
    return fetch('/api/imports/acknowledge', { method: 'POST' }).then(json<{ acknowledged: number }>)
  },

  /** Removes the cards an import added. Sales and price history are left alone. */
  removeImport(id: string) {
    return fetch(`/api/imports/${encodeURIComponent(id)}`, { method: 'DELETE' })
      .then(json<{ removed: number }>)
  },

  /** Each row carries its index so the outcomes can be matched back to the review list. */
  commitImport(jobId: string, rows: Array<AddEntryRequest & { index: number }>) {
    return fetch(`/api/import/${jobId}/commit`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rows }),
    }).then(json<CommitResult>)
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
