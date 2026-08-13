using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CardVault.Services;

/// <summary>
/// Gets your collection back out of the app.
///
/// Two formats on purpose. The CSV uses exactly the column names the importer
/// understands, so an export can be re-imported — into this app, a spreadsheet, or
/// whatever you move to next. The JSON is the complete picture including sales
/// history and hand-entered items, for when you want nothing lost.
/// </summary>
public sealed class ExportService(CollectionService collection, SalesService sales)
{
    /// <summary>
    /// Column headers deliberately match the importer's aliases so a round trip
    /// works without editing anything.
    /// </summary>
    public string CollectionCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Card ID", "Name", "Set", "Number", "Quantity", "Variant", "Condition", "Grade",
            "Purchase Price", "Purchase Date", "Location", "Notes", "Market Price", "Your Value",
            "Line Value", "Rarity", "Hand Entered"));

        foreach (var i in collection.List().OrderBy(i => i.SetName).ThenBy(i => i.Name))
        {
            sb.AppendLine(string.Join(",",
                Q(i.CardId), Q(i.Name), Q(i.SetName), Q(i.Number), N(i.Quantity), Q(i.Variant),
                Q(i.Condition), Q(i.Grade), N(i.PurchasePrice), Q(i.PurchaseDate), Q(i.Location),
                Q(i.Notes), N(i.MarketPrice), N(i.ManualValue), N(i.LineValue), Q(i.Rarity),
                i.IsCustom ? "yes" : "no"));
        }

        return sb.ToString();
    }

    public string SalesCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Sale Date", "Name", "Set", "Number", "Quantity", "Variant", "Condition", "Grade",
            "Purchase Price", "Sale Price", "Fees", "Proceeds", "Realised Gain", "Notes"));

        foreach (var s in sales.List())
        {
            sb.AppendLine(string.Join(",",
                Q(s.SaleDate), Q(s.CardName), Q(s.SetName), Q(s.Number), N(s.Quantity), Q(s.Variant),
                Q(s.Condition), Q(s.Grade), N(s.PurchasePrice), N(s.SalePrice), N(s.Fees),
                N(s.Proceeds), N(s.RealisedGain), Q(s.Notes)));
        }

        return sb.ToString();
    }

    /// <summary>Everything, in one file — collection, sales and the totals.</summary>
    public string EverythingJson()
    {
        var payload = new
        {
            exportedAt = DateTime.UtcNow.ToString("o"),
            format = 1,
            note = "Hand-entered items have ids beginning 'custom-' that only mean something "
                   + "in the vault that created them.",
            stats = collection.Stats(),
            collection = collection.List(),
            sales = sales.List(),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    /// <summary>Quotes a CSV field, doubling any embedded quotes.</summary>
    private static string Q(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string N(double? value)
        => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
