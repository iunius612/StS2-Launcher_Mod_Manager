using System;
using System.Text.Json;

namespace STS2Mobile.Steam;

// UI-friendly summary of a progress.save snapshot, optionally augmented with
// in-progress current_run.save state. Fed into ConflictDialog so the user can
// pick the "right" copy in a cloud-vs-local conflict. Defensive against schema
// changes — every field that fails to parse falls back to a stable raw value
// (file size + last-modified time) so the dialog still works.
//
// Issue #7 verification (2026-04-30) showed that progress.save accumulators
// alone are insufficient: a player who has just finished one floor of an
// in-progress run can have identical progress.save on both sides while
// current_run.save differs by hundreds of bytes. Without surfacing the
// in-progress run state in the dialog, KeepCloud silently destroys progress.
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

    // Augmented from current_run.save when a run is in progress. CurrentRunAct
    // and CurrentRunFloor follow the game's own UI convention — what the user
    // sees as "1막 3층" maps to Act=1, Floor=3 (next-floor / floors-climbed-in-act
    // + 1). When no current_run.save exists or it parses empty, HasCurrentRun
    // stays false and the dialog renders "—".
    public bool HasCurrentRun { get; private set; }
    public int CurrentRunAct { get; private set; }
    public int CurrentRunFloor { get; private set; }
    public int CurrentRunRawSize { get; private set; }
    public DateTimeOffset CurrentRunLastModified { get; private set; }

    // Identifies which profile this summary represents — the dialog renders
    // it as a small subtitle under the card title so the user understands why
    // a profile with empty stats can show up (e.g. "(프로필 3)" when an
    // in-progress run on a fresh slot is the conflict trigger, while the rich
    // accumulator data lives on profile1). Set by CloudSyncDecisions.BuildSummary.
    public int ProfileNumber { get; set; }
    public bool IsModded { get; set; }
    public string ProfileLabel
    {
        get
        {
            if (ProfileNumber <= 0)
                return null;
            var moddedTag = IsModded ? " · 모드" : "";
            return $"프로필 {ProfileNumber}{moddedTag}";
        }
    }

    // Empty source: NEITHER progress.save nor current_run.save has content.
    // Pre-#7 this was just `RawSize == 0`, which incorrectly hid in-progress
    // runs on brand-new profiles where progress.save is empty/default.
    public bool IsEmpty => RawSize == 0 && !HasCurrentRun;

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

    // Augments an existing summary with in-progress run state parsed from
    // current_run.save. Safe to call with null/empty content — leaves
    // HasCurrentRun=false in that case. Per game convention, Act/Floor mirror
    // what the player sees on screen: map_point_history is an array of acts
    // and each act is an array of cleared map points; the in-progress floor
    // they're about to enter is `cleared count + 1` of the current act.
    public void MergeCurrentRun(string content, int rawSize, DateTimeOffset lastModified)
    {
        CurrentRunRawSize = rawSize;
        CurrentRunLastModified = lastModified;

        if (rawSize == 0 || string.IsNullOrEmpty(content))
            return;

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            int act = 0;
            int floorInAct = 0;

            if (
                root.TryGetProperty("map_point_history", out var history)
                && history.ValueKind == JsonValueKind.Array
                && history.GetArrayLength() > 0
            )
            {
                act = history.GetArrayLength();
                var lastAct = history[history.GetArrayLength() - 1];
                if (lastAct.ValueKind == JsonValueKind.Array)
                    floorInAct = lastAct.GetArrayLength();
            }

            if (act == 0 && floorInAct == 0)
                return; // No actual progression yet — treat as no current run.

            CurrentRunAct = act;
            // Show the floor the player is about to enter (or just entered) —
            // matches the game's "1막 N층" indicator. Cleared 2 nodes ⇒ "3층".
            CurrentRunFloor = floorInAct + 1;
            HasCurrentRun = true;
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Cloud] current_run.save parse failed: {ex.Message}");
        }
    }

    // Convenience formatter for the dialog — "1막 3층" or "—".
    public string FormatCurrentRun()
    {
        if (!HasCurrentRun)
            return "—";
        return $"{CurrentRunAct}막 {CurrentRunFloor}층";
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
