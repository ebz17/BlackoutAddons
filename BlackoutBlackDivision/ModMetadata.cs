using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace BlackoutBlackDivision;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.blackoutaddons.blackdivision";
    public string Name { get; init; } = "Blackout x Black Division";
    public string Author { get; init; } = "Blackout Addons";
    public List<string>? Contributors { get; init; } = new();
    public Version Version { get; init; } = new("1.8.3");
    public Range SptVersion { get; init; } = new("~4.1.1");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; } = new();
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        { "com.vultify.blackout", new Range(">=4.1.1") },
        { "com.blackdiv.tacticaltoaster", new Range(">=1.3.0") },
    };
    public string? Url { get; init; } = "";
    public string License { get; init; } = "MIT";
}
