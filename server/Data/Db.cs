using Microsoft.Data.Sqlite;

namespace CardVault.Data;

/// <summary>
/// Owns the SQLite file and its schema. Everything the app knows lives here:
/// cached card metadata from pokemontcg.io, the cards you actually own, and a
/// daily price snapshot so we can chart collection value over time.
/// </summary>
public sealed class Db
{
    private readonly string _connectionString;

    public Db(DataPaths paths)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cards (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                set_id        TEXT,
                set_name      TEXT,
                set_series    TEXT,
                number        TEXT,
                rarity        TEXT,
                supertype     TEXT,
                subtypes      TEXT,
                types         TEXT,
                hp            TEXT,
                artist        TEXT,
                release_date  TEXT,
                image_small   TEXT,
                image_large   TEXT,
                payload       TEXT NOT NULL,
                cached_at     TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_cards_name ON cards(name);
            CREATE INDEX IF NOT EXISTS idx_cards_set  ON cards(set_id);

            CREATE TABLE IF NOT EXISTS collection (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id        TEXT NOT NULL REFERENCES cards(id),
                quantity       INTEGER NOT NULL DEFAULT 1,
                variant        TEXT NOT NULL DEFAULT 'normal',
                condition      TEXT NOT NULL DEFAULT 'NM',
                grade          TEXT,
                purchase_price REAL,
                purchase_date  TEXT,
                notes          TEXT,
                added_at       TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_collection_card ON collection(card_id);

            -- One row per CSV import that added anything, so a batch of cards can be
            -- reviewed as a unit and removed as one if it turns out to be wrong.
            --
            -- Deliberately holds no card list of its own: the membership lives on
            -- collection.import_batch, so selling or deleting a single card keeps the
            -- batch honest for free rather than needing a second place kept in step.
            CREATE TABLE IF NOT EXISTS import_batches (
                id              TEXT PRIMARY KEY,
                created_at      TEXT NOT NULL,
                -- Null until you've looked over what came in. Cards from an
                -- unacknowledged batch are marked in the vault.
                acknowledged_at TEXT
            );

            -- One row per card + printing + source + day, which is what lets the
            -- collection be charted over time — the API itself exposes only today.
            --
            -- Source is part of the key so several markets can be tracked side by
            -- side. Currency travels with the row rather than being assumed: these
            -- are different markets in different currencies, and quietly adding a
            -- euro figure to a dollar one would be worse than having no figure.
            CREATE TABLE IF NOT EXISTS price_history (
                card_id     TEXT NOT NULL,
                variant     TEXT NOT NULL,
                source      TEXT NOT NULL DEFAULT 'tcgplayer',
                currency    TEXT NOT NULL DEFAULT 'USD',
                captured_on TEXT NOT NULL,
                market      REAL,
                low         REAL,
                mid         REAL,
                high        REAL,
                PRIMARY KEY (card_id, variant, source, captured_on)
            );

            CREATE TABLE IF NOT EXISTS sets (
                id           TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                series       TEXT,
                printed_total INTEGER,
                total        INTEGER,
                release_date TEXT,
                logo         TEXT,
                symbol       TEXT,
                cached_at    TEXT NOT NULL
            );

            -- Cards you're hunting for. One row per card + printing, with the price
            -- you'd be willing to pay so the app can tell you when the market
            -- reaches it.
            CREATE TABLE IF NOT EXISTS wants (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id      TEXT NOT NULL REFERENCES cards(id),
                variant      TEXT NOT NULL DEFAULT 'normal',
                target_price REAL,
                quantity     INTEGER NOT NULL DEFAULT 1,
                notes        TEXT,
                added_at     TEXT NOT NULL,
                UNIQUE(card_id, variant)
            );

            -- Cards you've sold or otherwise parted with.
            --
            -- Card details are copied in rather than joined: a sold custom item's
            -- synthetic card row gets cleaned up once nothing owns it, and the sale
            -- record still needs to say what was sold. This is a ledger, so it has
            -- to survive the thing it refers to.
            CREATE TABLE IF NOT EXISTS sales (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id        TEXT NOT NULL,
                card_name      TEXT NOT NULL,
                set_name       TEXT,
                number         TEXT,
                image_small    TEXT,
                quantity       INTEGER NOT NULL,
                variant        TEXT,
                condition      TEXT,
                grade          TEXT,
                purchase_price REAL,
                sale_price     REAL NOT NULL,
                fees           REAL,
                sale_date      TEXT NOT NULL,
                notes          TEXT,
                recorded_at    TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_sales_date ON sales(sale_date);

            -- Signed-in sessions. Kept server-side rather than in a self-contained
            -- cookie so signing out actually revokes access everywhere, including
            -- from a device you no longer have.
            -- Decks you're building, which is a different question from what you own:
            -- a deck can call for cards you haven't got, and that gap is the point of
            -- keeping one here rather than in a spreadsheet.
            CREATE TABLE IF NOT EXISTS decks (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                -- standard, expanded or unlimited. Drives the legality check and
                -- whether deck rules are applied at all.
                format     TEXT NOT NULL DEFAULT 'standard',
                notes      TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT
            );

            -- A deck's list, by card rather than by the copy you own. Deliberately not
            -- pointing at collection rows: a deck is what you intend to play, and it
            -- has to survive selling a copy, buying a better one, or not owning it yet.
            CREATE TABLE IF NOT EXISTS deck_cards (
                deck_id  INTEGER NOT NULL REFERENCES decks(id) ON DELETE CASCADE,
                card_id  TEXT NOT NULL REFERENCES cards(id),
                quantity INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (deck_id, card_id)
            );

            CREATE TABLE IF NOT EXISTS sessions (
                token       TEXT PRIMARY KEY,
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                last_seen   TEXT NOT NULL,
                user_agent  TEXT,
                created_ip  TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_expiry ON sessions(expires_at);

            -- Small key/value store for the API key, last-run version and similar.
            -- Living in the database means settings are backed up with everything else.
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );

            -- An optional offline copy of the whole card catalogue, downloaded from
            -- the pokemon-tcg-data repository so that finding a card never waits on
            -- pokemontcg.io. Deliberately its OWN table rather than rows in `cards`:
            -- `cards` means "we hold a real payload for this", which the add path and
            -- the price sources both rely on, and filling it with twenty thousand
            -- priceless rows would quietly break both. Keeping them apart also makes
            -- deleting the catalogue a DELETE of one table.
            --
            -- It carries no prices, and cannot: the source data has no price block in
            -- it at all. That is the point — this speeds up identifying a card, and
            -- has nothing to say about what one is worth.
            CREATE TABLE IF NOT EXISTS catalogue (
                id             TEXT PRIMARY KEY,
                name           TEXT NOT NULL,
                set_id         TEXT,
                set_name       TEXT,
                set_series     TEXT,
                number         TEXT,
                printed_total  INTEGER,
                rarity         TEXT,
                supertype      TEXT,
                subtypes       TEXT,
                types          TEXT,
                artist         TEXT,
                release_date   TEXT,
                image_url      TEXT,
                -- 1 once the artwork has been fetched and stored as WebP on disk.
                has_image      INTEGER NOT NULL DEFAULT 0
            );

            -- Searching by name is the common case; the number pair is what you type
            -- off the card itself, and both want an index at twenty thousand rows.
            CREATE INDEX IF NOT EXISTS idx_catalogue_name   ON catalogue(name);
            CREATE INDEX IF NOT EXISTS idx_catalogue_number ON catalogue(number);
            CREATE INDEX IF NOT EXISTS idx_catalogue_set    ON catalogue(set_id);
            """;
        cmd.ExecuteNonQuery();

        Migrate(conn);
    }

    /// <summary>
    /// Additive migrations for databases created by earlier versions. SQLite has no
    /// "ADD COLUMN IF NOT EXISTS", so we check the table info first.
    /// </summary>
    private static void Migrate(SqliteConnection conn)
    {
        // Lets a graded slab carry its own worth: a PSA 10 bears no relation to the
        // raw market price the API reports, and sealed product has no price at all.
        AddColumn(conn, "collection", "manual_value", "REAL");

        // Marks synthetic cards (sealed product, anything not in the catalogue) so
        // they can be skipped by price refreshes and set completion.
        AddColumn(conn, "cards", "is_custom", "INTEGER NOT NULL DEFAULT 0");

        // Where the card physically is — "Binder 3, page 4", "Box A", "Safe".
        // The app knows what you own; this is so you can also find it.
        AddColumn(conn, "collection", "location", "TEXT");

        // Which import a card arrived in. Null for everything added by hand and for
        // everything that predates this, both of which are correct: neither belongs
        // to a batch, so neither is ever marked as unreviewed or swept up by a removal.
        AddColumn(conn, "collection", "import_batch", "TEXT");

        // Stamped whenever an entry is edited, which a partial sale also routes
        // through. Removing an import can then say how many of those cards you have
        // since touched, instead of discarding your corrections without mentioning it.
        AddColumn(conn, "collection", "modified_at", "TEXT");

        // What language the copy you own was printed in. Defaulting existing rows to
        // English is not a guess: the catalogue every one of them was resolved
        // against is English-only, so that is the printing they were entered as.
        //
        // It lives on the collection rather than on cards because a Japanese card is
        // the same card in another language, not a different card - the catalogue
        // entry, and the set completion built on it, are shared.
        AddColumn(conn, "collection", "language", "TEXT NOT NULL DEFAULT 'en'");

        // What a later look at your scans says this card should have been, when that
        // disagrees with what got imported. A note, never a correction: the entry keeps
        // the card it was imported as, and changing it stays your decision.
        //
        // Set only by a reconciliation run and cleared by dismissing it or running
        // another. Null for everything that agrees, which is most of it.
        AddColumn(conn, "collection", "flagged", "TEXT");
        AddColumn(conn, "collection", "flagged_at", "TEXT");

        // The file name of your own photograph of this copy, if you've attached one.
        // Per entry rather than per card on purpose: the whole point is telling two
        // copies of the same card apart, which a shared image could never do.
        AddColumn(conn, "collection", "photo", "TEXT");

        // When the market first came down to your target and stayed there, so the want
        // list can say how long a card has been at your price rather than only that it
        // is. A drop that happened this morning and one that has sat there a month are
        // different situations, and the difference is the whole reason to look.
        //
        // Cleared the moment the price goes back above, so it always describes the
        // current run rather than the best it ever did.
        AddColumn(conn, "wants", "met_since", "TEXT");

        // Copied onto the sale for the same reason as condition and grade: the ledger
        // has to keep saying what was sold once the collection row is gone. Nullable
        // because sales recorded before this column existed genuinely don't know.
        AddColumn(conn, "sales", "language", "TEXT");

        // Created here rather than alongside the table: on an existing database the
        // column above doesn't exist until the line above runs.
        using (var index = conn.CreateCommand())
        {
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS idx_collection_batch ON collection(import_batch)";
            index.ExecuteNonQuery();
        }

        MigratePriceHistoryToSources(conn);
    }

    /// <summary>
    /// Rebuilds price_history with source and currency in the primary key.
    ///
    /// This can't be an ALTER: SQLite won't change a primary key, so the table has
    /// to be recreated and the rows copied. Everything already recorded came from
    /// TCGplayer via pokemontcg.io, so that's what existing rows are labelled — and
    /// they're carried across rather than discarded, since price history is the one
    /// thing in here that can't be re-fetched.
    /// </summary>
    private static void MigratePriceHistoryToSources(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('price_history') WHERE name = 'source'";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }

        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                CREATE TABLE price_history_new (
                    card_id     TEXT NOT NULL,
                    variant     TEXT NOT NULL,
                    source      TEXT NOT NULL DEFAULT 'tcgplayer',
                    currency    TEXT NOT NULL DEFAULT 'USD',
                    captured_on TEXT NOT NULL,
                    market      REAL,
                    low         REAL,
                    mid         REAL,
                    high        REAL,
                    PRIMARY KEY (card_id, variant, source, captured_on)
                );

                INSERT INTO price_history_new
                    (card_id, variant, source, currency, captured_on, market, low, mid, high)
                SELECT card_id, variant, 'tcgplayer', 'USD', captured_on, market, low, mid, high
                FROM price_history;

                DROP TABLE price_history;
                ALTER TABLE price_history_new RENAME TO price_history;
                """;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void AddColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        check.Parameters.AddWithValue("$name", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }
}
