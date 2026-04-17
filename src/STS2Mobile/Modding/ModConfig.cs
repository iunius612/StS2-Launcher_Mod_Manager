using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2Mobile.Modding;

// User-editable enable/order state for installed mods. Persisted alongside the
// mods themselves at AppPaths.ExternalModConfigFile so it survives app reinstalls
// (same storage as the Mods/ folder).
public class ModConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("mods")]
    public List<ModConfigEntry> Mods { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ModConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ExternalModConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ExternalModConfigFile);
                var cfg = JsonSerializer.Deserialize<ModConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Mods ??= new List<ModConfigEntry>();
                    return cfg;
                }
            }
        }
        catch { }
        return new ModConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ExternalModsDir);
            var json = JsonSerializer.Serialize(this, Options);
            File.WriteAllText(AppPaths.ExternalModConfigFile, json);
        }
        catch (System.Exception ex)
        {
            PatchHelper.Log($"[Mods] Failed to save mod_config.json: {ex.Message}");
        }
    }

    // Reconciles the on-disk scan with saved config: config entries that no longer
    // exist are dropped, newly scanned mods are appended as enabled at the end.
    // Returns the reconciled (and saved) entries sorted by Order, filtered to mods
    // that exist on disk.
    public List<ModConfigEntry> Reconcile(IEnumerable<string> scannedIds)
    {
        var present = new HashSet<string>(scannedIds);

        Mods = Mods.Where(m => present.Contains(m.Id)).ToList();

        var known = new HashSet<string>(Mods.Select(m => m.Id));
        var nextOrder = Mods.Count == 0 ? 0 : Mods.Max(m => m.Order) + 1;
        foreach (var id in scannedIds)
        {
            if (known.Add(id))
                Mods.Add(new ModConfigEntry { Id = id, Enabled = true, Order = nextOrder++ });
        }

        Mods = Mods.OrderBy(m => m.Order).ToList();
        for (int i = 0; i < Mods.Count; i++)
            Mods[i].Order = i;

        Save();
        return Mods;
    }

    public ModConfigEntry Get(string id) => Mods.FirstOrDefault(m => m.Id == id);

    public void Add(string id, bool enabled)
    {
        var existing = Get(id);
        if (existing != null)
        {
            existing.Enabled = enabled;
            return;
        }
        var nextOrder = Mods.Count == 0 ? 0 : Mods.Max(m => m.Order) + 1;
        Mods.Add(new ModConfigEntry { Id = id, Enabled = enabled, Order = nextOrder });
    }

    public void Remove(string id) => Mods.RemoveAll(m => m.Id == id);

    public void Move(string id, int delta)
    {
        Mods = Mods.OrderBy(m => m.Order).ToList();
        var idx = Mods.FindIndex(m => m.Id == id);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= Mods.Count)
            return;
        (Mods[idx], Mods[target]) = (Mods[target], Mods[idx]);
        for (int i = 0; i < Mods.Count; i++)
            Mods[i].Order = i;
    }
}

public class ModConfigEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("order")]
    public int Order { get; set; }
}
