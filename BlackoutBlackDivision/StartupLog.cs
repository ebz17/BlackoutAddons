using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Common.Models.Logging;

namespace BlackoutBlackDivision;

/// <summary>Prints the active settings once the server has loaded (runs after Blackout and Black Division).</summary>
[Injectable(TypePriority = int.MaxValue)]
public class StartupLog(ISptLogger<StartupLog> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cfg = ModConfig.Load(out var error);
        if (error != null)
        {
            logger.Warning($"[Blackout x BlackDiv] {error}");
        }

        if (!cfg.Enabled)
        {
            logger.Warning("[Blackout x BlackDiv] Loaded but DISABLED in config.jsonc.");
            return Task.CompletedTask;
        }

        logger.Success(
            $"[Blackout x BlackDiv] Loaded - blackout Labs raids get Wedge ({cfg.WedgeChance}%) + {cfg.MinGuards}-{cfg.MaxGuards} Black Division guards" +
            (cfg.WedgeOnlyDuringBlackout ? "; Wedge disabled on normal Labs raids." : "."));
        return Task.CompletedTask;
    }
}
