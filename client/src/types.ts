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
  purchasePrice?: number | null
  purchaseDate?: string | null
  notes?: string | null
  addedAt: string
  marketPrice?: number | null
  lowPrice?: number | null
  highPrice?: number | null
  lineValue?: number | null
  pricesUpdatedAt?: string | null
  /** Your own valuation, which wins over market price when set. */
  manualValue?: number | null
  /** True for sealed product and anything else entered by hand. */
  isCustom: boolean
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
  points: PricePoint[]
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
}

export interface UpdateEntryRequest extends Partial<Omit<AddEntryRequest, 'cardId'>> {
  /** Removes a manual value so the entry tracks market price again. */
  clearManualValue?: boolean
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
}

export interface ApiKeyStatus {
  configured: boolean
  /** Only the first and last few characters — the full key is never sent back. */
  masked?: string | null
  source: string
}

export interface BackupInfo {
  name: string
  sizeBytes: number
  createdUtc: string
  reason: string
}

export interface AppSettings {
  apiKey: ApiKeyStatus
  dataDirectory: string
  migratedFromLegacy: boolean
  legacyDirectory: string
  backups: BackupInfo[]
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

export type ImportStatus = 'Matched' | 'Ambiguous' | 'NotFound' | 'LookupFailed' | 'Invalid'

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
  rarity?: string | null
  imageSmall?: string | null
  marketPrice?: number | null
  variants: string[]
  quantity: number
  variant: string
  condition: string
  grade?: string | null
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
