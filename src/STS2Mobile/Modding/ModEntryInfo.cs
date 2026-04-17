namespace STS2Mobile.Modding;

// One mod discovered on disk: the folder path, its parsed manifest, and an
// optional README snippet for the info panel.
public class ModEntryInfo
{
    public string Path { get; set; }
    public ModManifest Manifest { get; set; }
    public string ReadmeSnippet { get; set; }

    public string Id => Manifest?.Id;
}
