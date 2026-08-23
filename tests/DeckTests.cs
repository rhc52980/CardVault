using System.Text.Json;
using CardVault.Data;
using CardVault.Models;
using CardVault.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardVault.Tests;

/// <summary>
/// A deck is a list of cards you intend to play, which is a different thing from the
/// cards you own. These pin down that separation — a deck can call for what you
/// haven't got and survives you selling what you had — and that the rules are
/// reported rather than enforced, because a half-built deck is not a mistake.
/// </summary>
public sealed class DeckTests : IDisposable
{
    private readonly string _dir;
    private readonly Db _db;
    private readonly CollectionService _collection;
    private readonly DeckService _decks;

    public DeckTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cardvault-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "vault.db"), []);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CardVault:DataDirectory"] = _dir,
            })
            .Build();

        var paths = new DataPaths(config, NullLogger<DataPaths>.Instance);
        _db = new Db(paths);
        _db.Initialize();

        var settings = new SettingsService(_db, config);
        _collection = new CollectionService(_db, settings, []);
        _decks = new DeckService(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    // ------------------------------------------------------------------- the list

    [Fact]
    public void A_new_deck_is_empty_and_named()
    {
        var id = _decks.Create(new DeckRequest("Lost Box", Formats.Standard));

        var deck = _decks.Get(id)!;
        Assert.Equal("Lost Box", deck.Summary.Name);
        Assert.Equal(Formats.Standard, deck.Summary.Format);
        Assert.Empty(deck.Cards);
    }

    [Fact]
    public void A_deck_with_no_name_still_gets_one()
        => Assert.Equal("Untitled deck", _decks.Get(_decks.Create(new DeckRequest("   ")))!.Summary.Name);

    [Fact]
    public void Setting_a_count_to_zero_takes_the_card_out()
    {
        SeedCard("sv1-1", "Pikachu");
        var id = _decks.Create(new DeckRequest("Test"));

        _decks.SetCard(id, "sv1-1", 4);
        Assert.Single(_decks.Get(id)!.Cards);

        _decks.SetCard(id, "sv1-1", 0);
        Assert.Empty(_decks.Get(id)!.Cards);
    }

    [Fact]
    public void Setting_a_count_twice_replaces_rather_than_adds()
    {
        SeedCard("sv1-1", "Pikachu");
        var id = _decks.Create(new DeckRequest("Test"));

        _decks.SetCard(id, "sv1-1", 4);
        _decks.SetCard(id, "sv1-1", 2);

        Assert.Equal(2, Assert.Single(_decks.Get(id)!.Cards).Needed);
    }

    [Fact]
    public void Deleting_a_deck_takes_its_list_with_it()
    {
        SeedCard("sv1-1", "Pikachu");
        var id = _decks.Create(new DeckRequest("Test"));
        _decks.SetCard(id, "sv1-1", 4);

        Assert.True(_decks.Delete(id));
        Assert.Null(_decks.Get(id));
        Assert.Equal(0, CountDeckCards());
    }

    // -------------------------------------------------------------- what you lack

    /// <summary>The reason a deck lives here rather than in a spreadsheet.</summary>
    [Fact]
    public void A_deck_says_what_you_would_still_have_to_find()
    {
        SeedCard("sv1-1", "Pikachu");
        _collection.Add(new AddEntryRequest(CardId: "sv1-1", Quantity: 1));

        var id = _decks.Create(new DeckRequest("Test"));
        _decks.SetCard(id, "sv1-1", 4);

        var card = Assert.Single(_decks.Get(id)!.Cards);
        Assert.Equal(4, card.Needed);
        Assert.Equal(1, card.Owned);
        Assert.Equal(3, card.Short);
        Assert.Equal(3, _decks.Get(id)!.Summary.Missing);
    }

    [Fact]
    public void Owning_more_than_enough_leaves_nothing_short()
    {
        SeedCard("sv1-1", "Pikachu");
        _collection.Add(new AddEntryRequest(CardId: "sv1-1", Quantity: 9));

        var id = _decks.Create(new DeckRequest("Test"));
        _decks.SetCard(id, "sv1-1", 4);

        Assert.Equal(0, Assert.Single(_decks.Get(id)!.Cards).Short);
    }

    /// <summary>
    /// A deck names a card, not a copy of one. Selling the copy leaves the deck
    /// intact and simply short — anything else would quietly edit your decklist
    /// because you sold a spare.
    /// </summary>
    [Fact]
    public void Selling_the_card_leaves_the_deck_intact_and_short()
    {
        SeedCard("sv1-1", "Pikachu");
        var entry = _collection.Add(new AddEntryRequest(CardId: "sv1-1", Quantity: 2));

        var id = _decks.Create(new DeckRequest("Test"));
        _decks.SetCard(id, "sv1-1", 2);
        Assert.Equal(0, _decks.Get(id)!.Summary.Missing);

        _collection.Delete(entry);

        var card = Assert.Single(_decks.Get(id)!.Cards);
        Assert.Equal(2, card.Needed);
        Assert.Equal(0, card.Owned);
        Assert.Equal(2, card.Short);
    }

    /// <summary>
    /// Two decks can call for the same card without competing for it. You build one
    /// deck at a time and move cards between them, so counting a copy against both is
    /// what a paper player expects.
    /// </summary>
    [Fact]
    public void Two_decks_may_both_count_on_the_same_copy()
    {
        SeedCard("sv1-1", "Pikachu");
        _collection.Add(new AddEntryRequest(CardId: "sv1-1", Quantity: 4));

        var a = _decks.Create(new DeckRequest("A"));
        var b = _decks.Create(new DeckRequest("B"));
        _decks.SetCard(a, "sv1-1", 4);
        _decks.SetCard(b, "sv1-1", 4);

        Assert.Equal(0, _decks.Get(a)!.Summary.Missing);
        Assert.Equal(0, _decks.Get(b)!.Summary.Missing);
    }

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void A_short_deck_is_told_how_many_it_needs()
    {
        SeedCard("sv1-1", "Pikachu");
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "sv1-1", 4);

        Assert.Contains(_decks.Get(id)!.Problems, p => p.Contains("56 cards short"));
    }

    [Fact]
    public void More_than_four_copies_is_reported()
    {
        SeedCard("sv1-1", "Pikachu");
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "sv1-1", 5);

        var deck = _decks.Get(id)!;
        Assert.True(Assert.Single(deck.Cards).OverCopyLimit);
        Assert.Contains(deck.Problems, p => p.Contains("5 copies of Pikachu"));
    }

    /// <summary>
    /// Basic Energy is the exception that makes the rule worth encoding: twenty Fire
    /// Energy is a legal deck and twenty Pikachu is not.
    /// </summary>
    [Fact]
    public void Basic_energy_has_no_copy_limit()
    {
        SeedCard("sve-2", "Fire Energy", supertype: "Energy", subtypes: ["Basic"]);
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "sve-2", 20);

        var card = Assert.Single(_decks.Get(id)!.Cards);
        Assert.True(card.BasicEnergy);
        Assert.False(card.OverCopyLimit);
    }

    /// <summary>"Twin Energy" is an Energy card and is emphatically not Basic Energy.</summary>
    [Fact]
    public void Special_energy_still_has_a_copy_limit()
    {
        SeedCard("swsh5-155", "Twin Energy", supertype: "Energy", subtypes: ["Special"]);
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "swsh5-155", 5);

        Assert.True(Assert.Single(_decks.Get(id)!.Cards).OverCopyLimit);
    }

    [Fact]
    public void A_card_not_legal_in_the_format_is_reported()
    {
        SeedCard("base1-4", "Charizard", standard: "Banned");
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "base1-4", 1);

        var deck = _decks.Get(id)!;
        Assert.False(Assert.Single(deck.Cards).Legal);
        Assert.Contains(deck.Problems, p => p.Contains("isn't legal in Standard"));
    }

    /// <summary>
    /// A card the API says nothing about has rotated. Treating silence as permission
    /// would bless a deck that can't actually be played.
    /// </summary>
    [Fact]
    public void A_card_the_api_says_nothing_about_is_not_legal()
    {
        SeedCard("base1-4", "Charizard", standard: null);
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "base1-4", 1);

        Assert.False(Assert.Single(_decks.Get(id)!.Cards).Legal);
    }

    [Fact]
    public void Unlimited_applies_no_rules_at_all()
    {
        SeedCard("base1-4", "Charizard", standard: null);
        var id = _decks.Create(new DeckRequest("Test", Formats.Unlimited));
        _decks.SetCard(id, "base1-4", 9);

        var deck = _decks.Get(id)!;
        Assert.True(Assert.Single(deck.Cards).Legal);
        Assert.Empty(deck.Problems);
    }

    /// <summary>
    /// Changing format re-judges the same list. A deck legal in Expanded and not in
    /// Standard is the ordinary case, not an edge one.
    /// </summary>
    [Fact]
    public void Changing_format_re_judges_the_same_cards()
    {
        SeedCard("base1-4", "Charizard", standard: null, expanded: "Legal");
        var id = _decks.Create(new DeckRequest("Test", Formats.Standard));
        _decks.SetCard(id, "base1-4", 1);
        Assert.False(_decks.Get(id)!.Cards[0].Legal);

        _decks.Update(id, new DeckRequest(Format: Formats.Expanded));

        Assert.True(_decks.Get(id)!.Cards[0].Legal);
    }

    // ------------------------------------------------------------------- fixtures

    private long CountDeckCards()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deck_cards";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void SeedCard(
        string cardId, string name, string? supertype = "Pokémon", string[]? subtypes = null,
        string? standard = "Legal", string? expanded = "Legal")
    {
        // Built with the serialiser rather than by interpolating braces into a raw
        // string: the legalities object nests, and the escaping stops being readable.
        var legalities = new Dictionary<string, object>();
        if (standard is not null) legalities["standard"] = standard;
        if (expanded is not null) legalities["expanded"] = expanded;

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = cardId,
            ["name"] = name,
            ["supertype"] = supertype,
            ["subtypes"] = subtypes ?? [],
            ["legalities"] = legalities,
        });

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cards (id, name, number, supertype, payload, cached_at)
            VALUES ($id, $name, '1', $supertype, $payload, $now)
            """;
        cmd.Parameters.AddWithValue("$id", cardId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$supertype", (object?)supertype ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
