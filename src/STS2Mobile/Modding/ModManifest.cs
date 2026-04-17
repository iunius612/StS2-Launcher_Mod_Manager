using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2Mobile.Modding;

// POCO matching the StS2 mod_manifest.json schema. Parsed directly by the launcher
// for UI; the game's own ModManager reads the same file independently at game start.
public class ModManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("has_pck")]
    public bool HasPck { get; set; }

    [JsonPropertyName("has_dll")]
    public bool HasDll { get; set; }

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = new();

    [JsonPropertyName("affects_gameplay")]
    public bool AffectsGameplay { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ModManifest TryParse(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModManifest>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
