export interface SearchCard {
  id: string
  name: string
  number?: string | null
  rarity?: string | null
  supertype?: string | null
  hp?: string | null
  artist?: string | null
  types: string[]
  subtypes: string[]
  setId?: string | null
  setName?: string | null
  setSeries?: string | null
  releaseDate?: string | null
  imageSmall?: string | null
  imageLarge?: string | null
  marketPrice?: number | null
  lowPrice?: number | null
  highPrice?: number | null
  variants: string[]
  pricesUpdatedAt?: string | null
  ownedQuantity: number
  isWanted?: boolean
}

export interface CollectionItem {
  id: number
  cardId: string
  name: string
  setId?: string | null
  setName?: string | null
  setSeries?: string | null
  number?: string | null
  rarity?: string | null
  supertype?: string | null
  types: string[]
  hp?: string | null
  artist?: string | null
  releaseDate?: string | null
  imageSmall?: string | null
  imageLarge?: string | null
  quantity: number
  variant: string
  condition: string
  grade?: string | null
  /** The grader read out of `grade`, when it names one. */
  gradeCompany?: string | null
  /** The number read out of `grade`, on the 1-10 scale. */
  gradeValue?: number | null
  purchasePrice?: number | null
  purchaseDate?: string | null
  notes?: string | null
  addedAt: string
  /** The CSV import this card came in on, or null if it was added by hand. */
  importBatch?: string | null
  marketPrice?: number | null
  lowPrice?: number | null
  highPrice?: number | null
  lineValue?: number | null
  pricesUpdatedAt?: string | null
  /** Your own valuation, which wins over market price when set. */
  manualValue?: number | null
  /** True for sealed product and anything else entered by hand. */
  isCustom: boolean
  /** Where the card physically lives. */
  location?: string | null
  /** Printing language as an ISO code - "en", "ja", "zh-tw". */
  language: string
  /**
   * False when our prices don't describe this copy — a slab, or a printing in a
   * language none of our sources cover. Such a card is worth its manual value or
   * nothing, and the UI says so rather than showing a figure from the wrong market.
   */
  priced: boolean
  /** The market figure we hold but won't count. Present only when `priced` is false. */
  referencePrice?: number | null
  /** Why the figure doesn't count, when it doesn't. */
  unpricedReason?: 'graded' | 'language' | null
}

export interface WantItem {
  id: number
  cardId: string
  name: string
  setName?: string | null
  number?: string | null
  rarity?: string | null
  imageSmall?: string | null
  variant: string
  variants: string[]
  quantity: number
  targetPrice?: number | null
  notes?: string | null
  addedAt: string
  marketPrice?: number | null
  /** Market minus target; negative means it's going for less than you'd pay. */
  differenceToTarget?: number | null
  atOrBelowTarget: boolean
  /**
   * When it first came down and stayed there, or null if it hasn't. A drop this
   * morning and one that has sat for a month are different situations.
   */
  metSince?: string | null
}

export interface AddWantRequest {
  cardId: string
  variant?: string
  targetPrice?: number | null
  quantity?: number
  notes?: string | null
}

export interface UpdateWantRequest {
  targetPrice?: number | null
  quantity?: number
  notes?: string | null
  variant?: string
  clearTarget?: boolean
}

export interface SetBreakdown {
  setId?: string | null
  setName?: string | null
  cards: number
  value: number
}

export interface ValuePoint {
  date: string
  value: number
}

export interface CollectionStats {
  distinctCards: number
  totalCards: number
  totalMarketValue: number
  totalPaid: number
  biggestGainAmount?: number | null
  biggestGainCardName?: string | null
  bySet: SetBreakdown[]
  valueHistory: ValuePoint[]
  /** Profit actually banked on cards sold, after fees. */
  realisedGain: number
  saleProceeds: number
  cardsSold: number
}

export interface PricePoint {
  date: string
  market: number
  low?: number | null
  high?: number | null
}

export interface PriceSeries {
  variant: string
  source: string
  sourceName: string
  /** Different markets in different money — never compare figures across them. */
  currency: string
  points: PricePoint[]
}

export interface PriceSourceInfo {
  id: string
  name: string
  currency: string
}

export interface PriceSourceSettings {
  sources: PriceSourceInfo[]
  /** The single market that drives valuation. */
  preferred: string
}

export interface CardHistory {
  cardId: string
  series: PriceSeries[]
  /** Printings you own, so the chart can lead with those. */
  ownedVariants: string[]
}

export interface SellRequest {
  quantity: number
  salePrice: number
  saleDate?: string | null
  fees?: number | null
  notes?: string | null
}

export interface SaleRecord {
  id: number
  cardId: string
  cardName: string
  setName?: string | null
  number?: string | null
  imageSmall?: string | null
  quantity: number
  variant?: string | null
  condition?: string | null
  grade?: string | null
  gradeCompany?: string | null
  gradeValue?: number | null
  /** Null for sales recorded before language was tracked. */
  language?: string | null
  purchasePrice?: number | null
  salePrice: number
  fees?: number | null
  saleDate: string
  notes?: string | null
  recordedAt: string
  /** Null when the purchase price was never recorded, so profit is unknowable. */
  realisedGain?: number | null
  proceeds: number
}

export interface AddEntryRequest {
  cardId: string
  quantity?: number
  variant?: string
  condition?: string
  grade?: string | null
  purchasePrice?: number | null
  purchaseDate?: string | null
  notes?: string | null
  manualValue?: number | null
  location?: string | null
  language?: string | null
}

export interface UpdateEntryRequest extends Partial<Omit<AddEntryRequest, 'cardId'>> {
  /** Removes a manual value so the entry tracks market price again. */
  clearManualValue?: boolean
  /** Forgets what was paid. A null price means "leave alone", so this is the undo. */
  clearPurchasePrice?: boolean
}

export interface CustomItemRequest {
  name: string
  category?: string | null
  imageUrl?: string | null
  quantity?: number
  condition?: string
  grade?: string | null
  value?: number | null
  purchasePrice?: number | null
  purchaseDate?: string | null
  notes?: string | null
  language?: string | null
}

export interface AuthStatus {
  /** True once a password has been set. */
  enabled: boolean
  authenticated: boolean
  /** False on plain HTTP — a password sent over that is readable in transit. */
  isSecureConnection: boolean
}

export interface SessionInfo {
  /** Only the first few characters — the full token is never sent to the client. */
  tokenPrefix: string
  createdAt: string
  lastSeen: string
  userAgent?: string | null
  createdIp?: string | null
  isCurrent: boolean
}

export interface ApiKeyStatus {
  configured: boolean
  /** Only the first and last few characters — the full key is never sent back. */
  masked?: string | null
  source: string
}

/** Progress of a running catalogue download. State drives what the panel shows. */
export interface CatalogueProgress {
  state: 'sets' | 'images' | 'done' | 'failed' | 'cancelled'
  done: number
  total: number
  detail?: string | null
  error?: string | null
}

/** A price refresh in flight, whether you started it or the daily timer did. */
export interface PriceRefreshProgress {
  running: boolean
  done: number
  total: number
  detail?: string | null
  error?: string | null
  finishedAt?: string | null
}

export interface CatalogueStatus {
  enabled: boolean
  cards: number
  images: number
  imageBytes: number
  downloadedAt?: string | null
  progress?: CatalogueProgress | null
}

export interface BackupInfo {
  name: string
  sizeBytes: number
  createdUtc: string
  reason: string
}

export interface UpdateStatus {
  /** Off by default — the app makes no outbound calls you didn't ask for. */
  enabled: boolean
  available: boolean
  latest?: string | null
  releaseUrl?: string | null
  lastCheckedUtc?: string | null
}

export interface AppSettings {
  apiKey: ApiKeyStatus
  /** eBay credentials. Only the App ID is ever echoed back, and masked at that. */
  ebay: ApiKeyStatus
  /** Scrydex credentials, for Japanese cards. Only the API key is echoed, masked. */
  scrydex: ApiKeyStatus
  dataDirectory: string
  migratedFromLegacy: boolean
  legacyDirectory: string
  backups: BackupInfo[]
  /** What this collection is called. Defaults to "CardVault" when unset. */
  vaultName: string
  /** From <Version> in the csproj; release builds append a source stamp. */
  version: string
  buildDate: string
  update: UpdateStatus
}

export interface SetSummary {
  id: string
  name: string
  series?: string | null
  /** Number printed on the cards themselves — the "printed set". */
  printedTotal: number
  /** Includes secret rares — the "master set". */
  total: number
  releaseDate?: string | null
  logo?: string | null
  symbol?: string | null
  ownedDistinct: number
  ownedTotal: number
}

export interface SetCard {
  cardId: string
  name: string
  number: string
  numberSort: number
  rarity?: string | null
  supertype?: string | null
  imageSmall?: string | null
  marketPrice?: number | null
  variants: string[]
  ownedQuantity: number
}

export type ImportStatus =
  | 'Matched'
  | 'Ambiguous'
  /** The id resolved, but to a card the row's own name or number disagrees with. */
  | 'Mismatch'
  | 'NotFound'
  | 'LookupFailed'
  | 'Invalid'

export interface CardCandidate {
  cardId: string
  name: string
  setName?: string | null
  number?: string | null
  rarity?: string | null
  imageSmall?: string | null
  marketPrice?: number | null
  variants: string[]
}

export interface ImportRow {
  index: number
  source: string
  status: ImportStatus
  message?: string | null
  cardId?: string | null
  name?: string | null
  setName?: string | null
  setId?: string | null
  setCode?: string | null
  number?: string | null
  /** The denominator when the number was written as printed — "45/094". */
  printedTotal?: number | null
  /** What the CSV said, before resolution overwrote `name`/`number` with the match. */
  claimedName?: string | null
  claimedNumber?: string | null
  rarity?: string | null
  imageSmall?: string | null
  marketPrice?: number | null
  variants: string[]
  quantity: number
  variant: string
  condition: string
  grade?: string | null
  language: string
  purchasePrice?: number | null
  purchaseDate?: string | null
  notes?: string | null
  candidates: CardCandidate[]
}

export interface ImportJob {
  id: string
  state: 'resolving' | 'ready' | 'failed' | 'committed'
  total: number
  processed: number
  error?: string | null
  unmappedColumns: string[]
  rows: ImportRow[]
}

/** What became of one row at commit time. `reason` is set only when `added` is false. */
export interface CommitOutcome {
  index: number
  cardId: string
  added: boolean
  reason?: string | null
}

export interface CommitResult {
  added: number
  rows: CommitOutcome[]
}

/** A CSV import that put cards in the collection, and what has become of them. */
export interface ImportBatchSummary {
  id: string
  createdAt: string
  /** Null until you've looked over what came in. */
  acknowledgedAt?: string | null
  /** Entries from this import still in the collection. */
  entries: number
  /** Cards, counting quantities rather than rows. */
  cards: number
  /** How many of those entries you've edited or partly sold since. */
  modified: number
}

/** Full card payload straight from pokemontcg.io, used by the detail panel. */
export interface FullCard extends Record<string, unknown> {
  id: string
  name: string
  attacks?: Array<{ name: string; cost?: string[]; damage?: string; text?: string }>
  abilities?: Array<{ name: string; type?: string; text?: string }>
  weaknesses?: Array<{ type: string; value: string }>
  resistances?: Array<{ type: string; value: string }>
  retreatCost?: string[]
  flavorText?: string
  evolvesFrom?: string
  nationalPokedexNumbers?: number[]
  tcgplayer?: {
    url?: string
    updatedAt?: string
    prices?: Record<string, { low?: number; mid?: number; high?: number; market?: number; directLow?: number }>
  }
  cardmarket?: { url?: string; updatedAt?: string; prices?: Record<string, number> }
}
