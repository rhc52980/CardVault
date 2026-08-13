# Pokémon Vault

A self-hosted app for browsing, valuing and tracking a Pokémon TCG collection.
Card artwork, set details, attack stats and market prices come from
[pokemontcg.io](https://pokemontcg.io).

- **ASP.NET Core 10 + SQLite** backend, published as a single self-contained binary
- **React + TypeScript + Tailwind** frontend, built into the server's `wwwroot`
- Runs on Windows, Linux and macOS, and is reachable from a phone on the same wifi

## What it does

- **Search the full catalogue** by name, or with the API's query syntax
  (`set.id:base1 rarity:"Rare Holo"`), and add cards with printing, condition,
  grade, quantity and what you paid.
- **Values your collection** against TCGplayer market prices, including
  unrealised gain against purchase price.
- **Keeps a sold ledger** so profit you actually banked isn't lost when a card
  leaves the collection.
- **Exports everything** as CSV or JSON, so your data is never trapped in here.
- **Handles sealed product and slabs** — things the catalogue can't price, entered
  by hand with your own photo and valuation.
- **Tracks set completion.** Browse all 174 sets with a progress bar each, then
  open one to see the full checklist as a binder page — cards you own in colour,
  everything missing greyed out, with what it would cost to finish.
- **Bulk imports a CSV** of an existing collection, matching each row against the
  catalogue and letting you review every match before anything is saved.
- **Charts each card's price over time**, built from history the app records
  itself.
- **Records its own price history.** The API only ever reports today's price, so
  the app snapshots prices daily into SQLite. After a few days the value trend
  chart fills in — that's data the API cannot give you retroactively.
- **Caches everything locally.** Card metadata goes in SQLite and artwork on disk
  on first fetch, so the collection loads instantly and works offline.

## Price charts

Every card's detail view charts its market price over time, one line per
printing, with a crosshair and tooltip reading out all printings at the hovered
date. Printings you own are drawn first. A **show table** toggle lists the same
numbers, so nothing is reachable only by hovering.

A card gets its first price point the moment you add it, rather than waiting for
the next daily cycle — otherwise a card added in the morning would show an empty
chart all day. Until there are two days the chart shows today's figure and says
when the line will start.

**History only goes back as far as you do.** pokemontcg.io publishes today's
price and nothing else, so these charts begin the day a card enters your
collection and get more useful the longer the app runs. There's no way to
backfill.

The chart's four series colours are darker steps of the app's palette, validated
for lightness, chroma, colourblind separation and contrast against the chart
surface rather than picked by eye. A card with more than four priced printings
shows the first four and says so.

## Selling, and the sold ledger

When a card sells, use **Sell** on it rather than Remove. Remove deletes it
outright with no record; Sell records what you got and takes it out of the vault.

Enter quantity, sale price each, fees and a date, and the dialog shows the
proceeds and profit before you commit. Selling part of a stack leaves the
remainder in place.

Sold cards live under **My vault → Sold**, with totals for proceeds, realised
profit and cards sold. Once you've sold anything, the stats bar swaps its "best
performer" tile for **realised profit** — money actually banked rather than
paper gains.

Sales where no purchase price was ever recorded count towards proceeds but not
profit, and say so. Treating an unknown cost as zero would report the full sale
price as profit and quietly overstate how well you'd done.

The ledger stores its own copy of the card's details rather than pointing at the
card record, so a sale stays readable even after the thing it refers to is gone —
which matters for hand-entered items, whose synthetic card is cleaned up once
nothing owns it.

Deleting a sale record removes it from history only; it does not put the card
back in your vault.

## Export

Everything can leave the app, from the buttons above the collection:

- **Collection CSV** — spreadsheet-friendly, and the column headers are exactly
  the ones the importer accepts, so an export re-imports cleanly. Computed
  columns (market price, line value, rarity) come along for reading and are
  ignored on the way back in.
- **Sales CSV** — the sold ledger with proceeds and realised profit per sale.
- **JSON** — the complete picture: collection, sales, hand-entered items and
  totals in one file.

Hand-entered items carry ids beginning `custom-` that only mean anything in the
vault that created them, so they won't match if a CSV is imported into a fresh
database. The JSON export keeps their full details regardless.

## Sealed product and graded slabs

These are two different problems, so they have two different answers.

**A slab of a card the catalogue already has.** Add the card normally, set the
grade, then use **Set your own value** on the entry. A PSA 10 bears no relation
to the raw market price, so your figure drives valuation, gains and the
collection total. The detail view still shows the ungraded market price
underneath for reference. Clear the field to go back to tracking market.

**Anything the catalogue doesn't list** — sealed boxes, ETBs, tins, Japanese
promos, error cards. Use **+ By hand** in the vault toolbar. Pick a type (the
form adapts: sealed product has no condition, a slab asks for the grade), give
it a name and a value, and optionally upload a photo or paste an image URL.

Hand-entered items are stored as synthetic cards, so they sort, filter and value
like everything else, and they carry a gold **By hand** badge in the grid. They
are deliberately excluded from two places where they'd be wrong:

- **Daily price refresh** — there's nothing upstream to refresh, so your value stands
- **Set completion** — a sealed box isn't a card in a set, so it can't inflate progress

Deleting the last copy of a hand-entered item removes its synthetic card and its
uploaded photo.

## Set completion

The **Sets** tab lists every set with a completion bar. It defaults to sets
you've started; switch to **All** to browse the full catalogue.

Completion is counted two ways, because "complete" means different things:

- **Printed set** (default) — the card numbers printed on the cards themselves
- **Master set** — everything including secret rares, which is why a set can
  show 84 printed but 120 total

Opening a set shows its whole checklist in card-number order. Cards you own are
in full colour with a tick; everything missing is greyed out, so gaps are
obvious at a glance. A **show only what I'm missing** toggle turns it into a
want list, and the header totals what finishing the set would cost at current
market prices. Adding a card from here updates the progress bar immediately.

Set metadata is mirrored into SQLite, so the browser still works when the API is
down. Card details for a set are fetched once and then served from the local
cache — which means prices for cards you *don't* own are from whenever the set
was first opened. Prices for cards you do own stay current via the daily
snapshot.

## CSV import

Drop a file on the **Import CSV** tab, or paste rows directly. Column headers are
matched loosely, so most exports work unmodified — `Card Name`, `card_name` and
`Product Name` all map to the same field. Download a starter template from the
import screen.

Include either a **card name** or a **set and number**; everything else is
optional:

| Field | Also accepts |
| --- | --- |
| Name | card name, product name, title |
| Set | set name, edition, expansion |
| Number | card number, collector number, # |
| Quantity | qty, count, copies |
| Variant | printing, finish, foil |
| Condition | cond |
| Purchase Price | paid, cost, buy price |
| Purchase Date | acquired, date added |
| Card ID | a pokemontcg.io id such as `base1-4` |
| Notes | note, comment |

Values are normalised on the way in: `$250.00` becomes 250, `Lightly Played` and
`Excellent` both become `LP`, `Holo` becomes `holofoil`. If a row asks for a
printing the card was never issued in (a reverse holo that doesn't exist), the
import falls back to a real printing and says so.

Every row lands in one of five states, and **nothing is written to your
collection until you press the button**:

- **Matched** — resolved to exactly one card, ticked and ready
- **Needs a choice** — several cards fit, so pick the right printing from a dropdown
- **Not found** — nothing in the catalogue matched
- **Lookup failed** — the API was unreachable for that row; re-run to retry it
- **Incomplete** — the row had nothing to search on

Resolution runs as a background job with a progress bar, because a large file
means one API lookup per unseen card. Rows whose cards are already in the local
cache resolve instantly, so re-importing is fast.

## Setup

Get a free key from [dev.pokemontcg.io](https://dev.pokemontcg.io) and paste it
into the **Settings** tab. It's stored in your local database, takes effect
immediately, and is only ever displayed back partly masked. The app runs without
a key but is heavily rate limited.

If you'd rather not use the UI, `server/appsettings.Local.json` (gitignored) and
the `POKEMONTCG_API_KEY` environment variable both still work:

```json
{
  "PokemonTcg": {
    "ApiKey": "your-key-from-dev.pokemontcg.io"
  }
}
```

A key saved through the UI takes precedence over both.

> **The API can't verify keys.** A correct key, a mistyped key and no key at all
> all get an identical 200 response with no rate-limit headers to compare, so
> nothing can tell you a key is genuine — an invalid one is silently treated as
> unauthenticated. Saving only confirms the service answered. If searches feel
> slow or rate-limited, re-check what you pasted.

## Your data and backups

**Your collection is stored outside the application folder**, so updating,
moving or reinstalling the app cannot touch it:

| Platform | Location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\PokemonVault` |
| Linux / macOS | `~/.local/share/PokemonVault` |

Override with `PokemonVault:DataDirectory` in config or the
`POKEMONVAULT_DATA_DIR` environment variable. The exact path in use is shown on
the Settings tab.

Earlier builds kept the database next to the binary, where replacing the app
folder on update would have destroyed it. If such a collection is found it's
**copied** to the new location on startup — the original is left untouched so
you have a fallback.

Backups are taken automatically:

- **Whenever the app version changes** — the update case, and the one most likely
  to lose data. Taken before anything else touches the database.
- **Once a day** otherwise.
- The ten most recent are kept; older ones are pruned.

They're written with SQLite's backup API rather than copied, because with
write-ahead logging on, the `.db` file alone can be an incomplete picture of a
live database. Back up on demand, download, or delete from the Settings tab.

**To restore one:** stop the app, replace `vault.db` in the data folder with the
backup (renamed to `vault.db`), delete any `vault.db-wal` and `vault.db-shm`
next to it, then start the app again.

## Running it

Build the frontend, then start the server:

```bash
cd client && npm install && npm run build && cd ../server && dotnet run
```

Then open <http://localhost:5188>.

For frontend work, run Vite's dev server for hot reload — it proxies `/api` and
`/img` to the backend on 5188:

```bash
cd client && npm run dev
```

## Publishing a standalone build

```bash
cd client && npm run build && cd ../server && dotnet publish -c Release -r win-x64 --self-contained
```

Swap `win-x64` for `linux-x64` or `osx-arm64` as needed. The output folder is
fully self-contained — no .NET runtime install required on the target machine.

## Data and network notes

- The server binds to `0.0.0.0:5188` so other devices on your network can reach
  it. **There is no authentication** — it assumes a trusted home network. To keep
  it local-only, set `ASPNETCORE_URLS=http://localhost:5188`.
- pokemontcg.io returns intermittent 500/502 errors even on valid requests. The
  client retries transient failures automatically, and upstream outages surface
  as a clear message rather than breaking the page — your saved collection is
  served entirely from local data and is unaffected.

## Layout

```
server/            ASP.NET Core API + static host
  Data/DataPaths.cs  Resolves the per-user data directory, migrates old installs
  Data/Db.cs       SQLite schema: cards, collection, sales, price_history,
                   sets, settings, plus additive migrations for existing
                   databases
  Services/        API client, card cache, collection, pricing, image cache,
                   daily price snapshots, sets + completion, custom items,
                   CSV parser and import jobs, sales ledger, export,
                   settings, backups
  Program.cs       Minimal API endpoints
client/            React frontend (builds into server/wwwroot)
  src/components/  Card grid, search, set browser, detail modal, price chart,
                   stats, CSV import, manual entry, sell + sold ledger, settings
```
