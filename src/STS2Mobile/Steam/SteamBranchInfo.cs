using System;

namespace STS2Mobile.Steam;

// One branch entry from Steam's depots/branches KeyValue tree
// (e.g. public, public-beta, password-gated betas).
public class SteamBranchInfo
{
    public string Name;
    public string Description;
    public string BuildId;
    public DateTime TimeUpdatedUtc;
    public bool IsPasswordProtected;
}
