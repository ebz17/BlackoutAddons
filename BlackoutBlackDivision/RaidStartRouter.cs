using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Utils;

namespace BlackoutBlackDivision;

/// <summary>
/// Hooks /client/match/local/start BEFORE SPT's own MatchStaticRouter (TypePriority 400000),
/// so the Labs spawn table is already correct when SPT clones it for the raid.
/// The core router runs afterwards and produces the real response.
/// </summary>
[Injectable(TypePriority = 399000)]
public class RaidStartRouter(JsonUtil jsonUtil, BlackoutWedgeSync sync, ISptLogger<RaidStartRouter> logger)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<StartLocalRaidRequestData>(
            "/client/match/local/start",
            (url, info, sessionId, output, cancellationToken) =>
            {
                try
                {
                    sync.Apply(info?.Location);
                }
                catch (Exception ex)
                {
                    // Never block a raid from starting because of this mod.
                    logger.Error($"[Blackout x BlackDiv] Failed to sync Labs spawns: {ex}");
                }

                return new ValueTask<string>(output ?? string.Empty);
            })
    ]);
