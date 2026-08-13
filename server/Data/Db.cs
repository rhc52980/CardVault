using Microsoft.Data.Sqlite;

namespace PokemonVault.Data;

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

            -- One row per card+variant+day. Lets us chart what the collection is
            -- worth over time, which the API itself does not expose.
            CREATE TABLE IF NOT EXISTS price_history (
                card_id     TEXT NOT NULL,
                variant     TEXT NOT NULL,
                captured_on TEXT NOT NULL,
                market      REAL,
                low         REAL,
                mid         REAL,
                high        REAL,
                PRIMARY KEY (card_id, variant, captured_on)
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

            -- Small key/value store for the API key, last-run version and similar.
            -- Living in the database means settings are backed up with everything else.
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
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
