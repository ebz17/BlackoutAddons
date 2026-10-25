using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Common.Models.Logging;

namespace BlackoutBlackDivision;

/// <summary>
/// Blackout's key ("Admin's key" - Wedge carries it in blackout raids) has no handbook entry and no price. The game
/// then logs "Item price is Zero: Admin's key" over and over, and the flea "sell" window breaks on it. Give it a
/// handbook entry (next to the other mechanical keys) and a price, only if it doesn't have one already.
/// </summary>
[Injectable(TypePriority = int.MaxValue - 10)]
public class KeyPriceFix(TemplateTable templates, ISptLogger<KeyPriceFix> logger) : IOnLoad
{
    private static readonly MongoId BlackoutKey = new("6a33c17933cff6b88c08902e");
    private static readonly MongoId MechanicalKeyBase = new("5c99f98d86f7745c314214b3");
    private const double Price = 75000;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!templates.Items.ContainsKey(BlackoutKey))
            {
                logger.Info("[Blackout x BlackDiv] Blackout's key isn't in the item database - no price fix needed.");
                return Task.CompletedTask;
            }
            var book = templates.Handbook.Items;
            bool fixedSomething = false;
            if (!book.Any(h => h.Id == BlackoutKey))
            {
                // the handbook folder other mechanical keys use
                MongoId? folder = null;
                foreach (var h in book)
                {
                    if (templates.Items.TryGetValue(h.Id, out var t) && t.Parent == MechanicalKeyBase) { folder = h.ParentId; break; }
                }
                if (folder.HasValue)
                {
                    book.Add(new HandbookItem { Id = BlackoutKey, ParentId = folder.Value, Price = Price });
                    fixedSomething = true;
                }
            }
            if (!templates.Prices.TryGetValue(BlackoutKey, out var p) || p <= 0)
            {
                templates.Prices[BlackoutKey] = Price;
                fixedSomething = true;
            }
            if (fixedSomething) logger.Info($"[Blackout x BlackDiv] Blackout's key had no price - set to {Price:N0} roubles (handbook + flea).");
        }
        catch (Exception ex)
        {
            logger.Warning($"[Blackout x BlackDiv] key price fix failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }
}
