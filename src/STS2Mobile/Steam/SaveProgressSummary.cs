using System;
using System.Text.Json;

namespace STS2Mobile.Steam;

// UI-friendly summary of a progress.save snapshot. Fed into ConflictDialog so
// the user can pick the "right" copy in a cloud-vs-local conflict. Defensive
// against schema changes — every field that fails to parse falls back to a
// stable raw value (file size + last-modified time) so the dialog still works.
public class SaveProgressSummary
{
    public bool ParseSucceeded { get; private set; }
    public int TotalWins { get; private set; }
    public int TotalLosses { get; private set; }
    public int MaxAscension { get; private set; }
    public int FloorsClimbed { get; private set; }
    public int TotalPlaytimeSeconds { get; private set; }
    public int CharactersTracked { get; private set; }
    public int RelicsDiscovered { get; private set; }
    public int CardsDiscovered { get; private set; }
    public int SchemaVersion { get; private set; }
    public int RawSize { get; private set; }
    public DateTimeOffset LastModified { get; private set; }

    // Empty source: file does not exist or zero-length string.
    public bool IsEmpty => RawSize == 0;

    public static SaveProgressSummary FromContent(
        string content,
        int rawSize,
        DateTimeOffset lastModified
    )
    {
        var summary = new SaveProgressSummary { RawSize = rawSize, LastModified = lastModified };

        if (string.IsNullOrEmpty(content))
            return summary;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return summary;

            if (root.TryGetProperty("schema_version", out var sv) && sv.ValueKind == JsonValueKind.Number)
                summary.SchemaVersion = sv.GetInt32();

            if (root.TryGetProperty("floors_climbed", out var fc) && fc.ValueKind == JsonValueKind.Number)
                summary.FloorsClimbed = fc.GetInt32();

            if (root.TryGetProperty("total_playtime", out var tp) && tp.ValueKind == JsonValueKind.Number)
                summary.TotalPlaytimeSeconds = tp.GetInt32();

            if (root.TryGetProperty("character_stats", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                summary.CharactersTracked = cs.GetArrayLength();
                int wins = 0, losses = 0, maxAsc = 0;
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object)
                        continue;
                    if (c.TryGetProperty("total_wins", out var w) && w.ValueKind == JsonValueKind.Number)
                        wins += w.GetInt32();
                    if (c.TryGetProperty("total_losses", out var l) && l.ValueKind == JsonValueKind.Number)
                        losses += l.GetInt32();
                    if (c.TryGetProperty("max_ascension", out var a) && a.ValueKind == JsonValueKind.Number)
                        maxAsc = Math.Max(maxAsc, a.GetInt32());
                }
                summary.TotalWins = wins;
                summary.TotalLosses = losses;
                summary.MaxAscension = maxAsc;
            }

            if (
                root.TryGetProperty("discovered_relics", out var dr)
                && dr.ValueKind == JsonValueKind.Array
            )
                summary.RelicsDiscovered = dr.GetArrayLength();

            if (
                root.TryGetProperty("discovered_cards", out var dc)
                && dc.ValueKind == JsonValueKind.Array
            )
                summary.CardsDiscovered = dc.GetArrayLength();

            summary.ParseSucceeded = true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] Progress summary parse failed: {ex.Message}");
        }

        return summary;
    }

    // "Xh Ym" format. Handles 0 cleanly; no seconds shown to keep the dialog tidy.
    public string FormatPlaytime()
    {
        if (TotalPlaytimeSeconds <= 0)
            return "—";
        var hours = TotalPlaytimeSeconds / 3600;
        var minutes = (TotalPlaytimeSeconds % 3600) / 60;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    public string FormatSize()
    {
        if (RawSize == 0)
            return "0 B";
        if (RawSize < 1024)
            return $"{RawSize} B";
        if (RawSize < 1024 * 1024)
            return $"{RawSize / 1024.0:F1} KB";
        return $"{RawSize / (1024.0 * 1024.0):F1} MB";
    }

    // Local-time string, no seconds. Matches the granularity Steam Cloud
    // timestamps land at (uint Unix seconds → minute resolution is what users care about).
    public string FormatLastModified()
    {
        if (LastModified == DateTimeOffset.MinValue)
            return "—";
        return LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
