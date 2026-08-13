using System.Text;

namespace CardVault.Services;

/// <summary>
/// Minimal RFC 4180 CSV reader. Collection exports come from all sorts of places
/// (TCGplayer, Deckbox, hand-rolled spreadsheets), so it handles quoted fields,
/// embedded commas and newlines, doubled quotes, and tab/semicolon delimiters.
/// </summary>
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrWhiteSpace(text)) return rows;

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.StartsWith('﻿')) text = text[1..];

        var delimiter = DetectDelimiter(text);

        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is a literal quote.
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            if (c == '"') inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f.Trim().Length > 0)) rows.Add(row.ToArray());
                row.Clear();
            }
            else field.Append(c);
        }

        row.Add(field.ToString());
        if (row.Any(f => f.Trim().Length > 0)) rows.Add(row.ToArray());

        return rows;
    }

    /// <summary>Picks whichever separator appears most often outside quoted text.</summary>
    private static char DetectDelimiter(string text)
    {
        var sample = text.Split('\n').FirstOrDefault() ?? "";
        var counts = new Dictionary<char, int> { [','] = 0, ['\t'] = 0, [';'] = 0 };
        var inQuotes = false;

        foreach (var c in sample)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }

        var best = counts.MaxBy(kv => kv.Value);
        return best.Value > 0 ? best.Key : ',';
    }
}
