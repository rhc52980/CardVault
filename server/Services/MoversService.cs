using CardVault.Data;
using CardVault.Models;

namespace CardVault.Services;

/// <summary>
/// Cards whose price has moved enough to be worth knowing about.
///
/// The threshold is a percentage <em>and</em> an amount, not either. Percentage alone
/// floods the list: a 50p common doubling is up 100%, and there are dozens of those
/// every week. Amount alone misses everything cheap that is genuinely running.
///
/// The amount is measured against the <em>holding</em>, not one card. Measured on a
/// real bulk collection — 1,773 cards worth £864 — the largest move on any single
/// card over a fortnight was 90p, so a per-card floor of anything meaningful would
/// have shown that collection nothing, ever. What does carry weight there is eight
/// copies of a card shifting 6p each. The holding is the figure that changes what the
/// collection is worth, which is the question being asked.
///
/// Uses exactly the price rule the grid uses: the chosen market, in its currency, with
/// your own valuations excluded and slabs and non-English printings left out because
/// they have no market price at all. A list of risers that disagreed with the totals
/// above it would be worse than no list.
/// </summary>
public sealed class MoversService(Db db, SettingsService settings, IEnumerable<PriceSources.IPriceSource> sources)
{
    /// <summary>
    /// What counts as worth flagging, and how far back to look. Defaults chosen to be
    /// quiet: most collections have a handful of real movers in a month, not pages.
    /// </summary>
    /// <summary>
    /// What counts as worth flagging, and how far back to look.
    ///
    /// The defaults are deliberately small. An earlier draft used 20% and £5 per card,
    /// which sounds sensible and returns an empty list for any collection made of
    /// commons — most of them. Better to start with something that shows you your own
    /// data and let you raise the bar than to ship a feature that looks broken.
    /// </summary>
    public MoverSettings Settings => new(
        Days: Number("movers_days", 30),
        MinPercent: Number("movers_min_percent", 25),
        MinAmount: Decimal("movers_min_amount", 0.25));

    public void Save(MoverSettings s)
    {
        settings.Set("movers_days", Math.Clamp(s.Days, 1, 365).ToString());
        settings.Set("movers_min_percent", Math.Clamp(s.MinPercent, 0, 10_000).ToString());
        settings.Set("movers_min_amount",
            Math.Max(0, s.MinAmount).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Cards that have moved past the threshold, biggest change to your holding first.
    ///
    /// Falls are found the same way and returned alongside: the arithmetic is identical
    /// and the decision they inform — sell before it slides further — is the mirror of
    /// the one a rise informs.
    /// </summary>
    public List<Mover> Find()
    {
        var s = Settings;
        var since = DateTime.UtcNow.AddDays(-s.Days).ToString("yyyy-MM-dd");
        var currency = sources.FirstOrDefault(p => p.Id == settings.PreferredPriceSource)?.Currency ?? "USD";

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        // Earliest and latest reading inside the window, per card and printing. Cards
        // with only one reading can't have moved and drop out on the HAVING.
        //
        // Manual valuations are excluded: they're your opinion, and "your estimate went
        // up because you raised it" is not news. Slabs and non-English printings are
        // excluded for the reason the grid excludes them — the figures aren't theirs.
        // Each side is collapsed to one row per card and printing *before* they meet.
        // Joining the raw tables fans the collection row out once per price reading,
        // and summing quantity across that counts a holding of eight as sixteen.
        cmd.CommandText = """
            WITH span AS (
                SELECT card_id, variant,
                       MIN(captured_on) AS first_day,
                       MAX(captured_on) AS last_day
                FROM price_history
                WHERE market IS NOT NULL
                  AND currency = $currency
                  AND source = $source
                  AND captured_on >= $since
                GROUP BY card_id, variant
                -- One reading is not a movement, whatever else is true of the card.
                HAVING COUNT(*) > 1
            ),
            held AS (
                SELECT c.card_id, c.variant, SUM(c.quantity) AS owned
                FROM collection c
                JOIN cards k ON k.id = c.card_id
                WHERE COALESCE(k.is_custom, 0) = 0
                  -- The same exclusions the grid applies: your own valuation isn't a
                  -- market moving, and a slab or a non-English printing has no market
                  -- price of its own to move.
                  AND c.manual_value IS NULL
                  AND (c.grade IS NULL OR TRIM(c.grade) = '')
                  AND c.language = $language
                GROUP BY c.card_id, c.variant
            )
            SELECT s.card_id, s.variant, k.name, k.set_name, k.number, k.rarity, k.image_small,
                   h.owned,
                   (SELECT market FROM price_history p
                     WHERE p.card_id = s.card_id AND p.variant = s.variant
                       AND p.source = $source AND p.currency = $currency
                       AND p.captured_on = s.first_day) AS was,
                   (SELECT market FROM price_history p
                     WHERE p.card_id = s.card_id AND p.variant = s.variant
                       AND p.source = $source AND p.currency = $currency
                       AND p.captured_on = s.last_day) AS now,
                   s.first_day, s.last_day
            FROM span s
            JOIN held h ON h.card_id = s.card_id AND h.variant = s.variant
            JOIN cards k ON k.id = s.card_id
            """;

        cmd.Parameters.AddWithValue("$currency", currency);
        cmd.Parameters.AddWithValue("$source", settings.PreferredPriceSource);
        cmd.Parameters.AddWithValue("$since", since);
        cmd.Parameters.AddWithValue("$language", Languages.Default);

        var movers = new List<Mover>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(8) || r.IsDBNull(9)) continue;

            var was = r.GetDouble(8);
            var now = r.GetDouble(9);
            var change = now - was;

            if (was <= 0 || Math.Abs(change) < 0.005) continue;

            var owned = r.GetInt32(7);
            var lineChange = change * owned;

            // The amount is judged on the holding, not the card. Eight copies moving
            // 6p each is 48p off your collection; one card moving 6p is nothing, and
            // a floor that can't tell them apart is a floor set against the wrong number.
            var percent = change / was * 100;
            if (Math.Abs(percent) < s.MinPercent || Math.Abs(lineChange) < s.MinAmount) continue;
            movers.Add(new Mover(
                CardId: r.GetString(0),
                Variant: r.GetString(1),
                Name: r.GetString(2),
                SetName: r.IsDBNull(3) ? null : r.GetString(3),
                Number: r.IsDBNull(4) ? null : r.GetString(4),
                Rarity: r.IsDBNull(5) ? null : r.GetString(5),
                ImageSmall: r.IsDBNull(6) ? null : r.GetString(6),
                Owned: owned,
                Was: Math.Round(was, 2),
                Now: Math.Round(now, 2),
                Change: Math.Round(change, 2),
                PercentChange: Math.Round(percent, 1),
                // What it did to your holding, which is the figure that actually
                // changes what the collection is worth — and the one the amount
                // threshold is measured against.
                LineChange: Math.Round(lineChange, 2),
                From: r.GetString(10),
                To: r.GetString(11)));
        }

        return [.. movers.OrderByDescending(m => Math.Abs(m.LineChange))];
    }

    private int Number(string key, int fallback)
        => int.TryParse(settings.Get(key), out var n) ? n : fallback;

    /// <summary>
    /// Stored and read with the invariant culture. A machine whose locale writes
    /// "0,25" would otherwise save a number it could not read back.
    /// </summary>
    private double Decimal(string key, double fallback)
        => double.TryParse(settings.Get(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
