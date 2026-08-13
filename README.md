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
- **Bulk imports a CSV** of an existing collection, matching each row against the
  catalogue and letting you review every match before anything is saved.
- **Records its own price history.** The API only ever reports today's price, so
  the app snapshots prices daily into SQLite. After a few days the value trend
  chart fills in — that's data the API cannot give you retroactively.
- **Caches everything locally.** Card metadata goes in SQLite and artwork on disk
  on first fetch, so the collection loads instantly and works offline.

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

The API key lives in `server/appsettings.Local.json`, which is gitignored:

```json
{
  "PokemonTcg": {
    "ApiKey": "your-key-from-dev.pokemontcg.io"
  }
}
```

`POKEMONTCG_API_KEY` works as an environment variable too. The app runs without a
key but is heavily rate limited.

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

- Your collection lives in `server/data/vault.db` (alongside cached images in
  `server/data/images/`). Back that file up; everything else can be rebuilt.
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
  Data/Db.cs       SQLite schema: cards, collection, price_history, sets
  Services/        API client, card cache, collection, pricing, image cache,
                   daily price snapshots, CSV parser + import jobs
  Program.cs       Minimal API endpoints
client/            React frontend (builds into server/wwwroot)
  src/components/  Card grid, search, detail modal, stats, CSV import
```
