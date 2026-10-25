using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Common.Models.Logging;

namespace BlackoutBlackDivision;

/// <summary>
/// One broken flea offer (no item, or no price at all) makes every flea search fail with "Object reference not set to
/// an instance of an object" (SPT's RagfairCategoriesService reads offer.Requirements.First() without a null check).
/// Such offers come from other mods' traders/items. Every 10 s this takes them off the flea and says which item it was,
/// so the mod behind it can be found.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class FleaGuard(RagfairOfferService offers, TemplateTable templates, SaveServer saves, MailSendService mail, ISptLogger<FleaGuard> logger) : IOnUpdate
{
    private readonly HashSet<string> _reported = new();

    /// <summary>
    /// Your own listing (e.g. an item the game couldn't price): take it out of your profile too - otherwise it comes back
    /// on every server start - and mail the item back to you.
    /// </summary>
    private void ReturnToPlayer(MongoId offerId)
    {
        try
        {
            foreach (var profileId in saves.GetProfiles().Keys)
            {
                var pmc = saves.GetProfile(profileId)?.CharacterData?.PmcData;
                var mine = pmc?.RagfairInfo?.Offers;
                var offer = mine?.FirstOrDefault(o => o != null && o.Id == offerId);
                if (offer == null) continue;
                mine.Remove(offer);
                var root = offer.Items?.FirstOrDefault();
                if (root != null)
                {
                    var copy = new Item { Id = new MongoId(), Template = root.Template, Upd = new Upd { StackObjectsCount = root.Upd?.StackObjectsCount ?? 1 } };
                    mail.SendSystemMessageToPlayer(profileId, "Flea market: this listing had no price and was taken down. Here is your item back.", new List<Item> { copy }, 604800L);
                }
                logger.Info($"[Blackout x BlackDiv] Flea guard: your listing {offerId} was removed from your profile and the item mailed back to you.");
                return;
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[Blackout x BlackDiv] Flea guard: couldn't return the listing ({ex.Message}).");
        }
    }

    public Task<bool> OnUpdateAsync(long secondsSinceLastRun, CancellationToken cancellationToken)
    {
        if (secondsSinceLastRun < 10) return Task.FromResult(false);
        try
        {
            var list = offers.GetOffers();
            if (list == null) return Task.FromResult(true);
            var bad = new List<(SPTarkov.Server.Core.Models.Common.MongoId Id, string Why, string Item, string Seller)>();
            foreach (var o in list)
            {
                if (o == null) continue;
                string why = null;
                var first = o.Items?.FirstOrDefault();
                if (first == null) why = "no item";
                else if (o.Requirements == null || !o.Requirements.Any()) why = "no price";
                else if (o.Requirements.First() == null) why = "broken price";
                else if (o.User == null) why = "no seller";
                if (why == null) continue;
                string item = first == null ? "-" : $"{first.Template}{(templates.Items.TryGetValue(first.Template, out var t) ? " (" + t.Name + ")" : " (not in the item database!)")}";
                string seller = o.User == null ? "?" : $"{o.User.Id} ({o.User.MemberType})";
                bad.Add((o.Id, why, item, seller));
            }
            foreach (var b in bad)
            {
                offers.RemoveOfferById(b.Id);
                ReturnToPlayer(b.Id);
                if (_reported.Add(b.Item + "|" + b.Why))
                    logger.Warning($"[Blackout x BlackDiv] Flea guard: removed a broken flea offer ({b.Why}) - item {b.Item}, seller {b.Seller}. " +
                                   "It breaks flea searches; the item or trader comes from another mod.");
            }
        }
        catch (Exception ex)
        {
            if (_reported.Add("err")) logger.Warning($"[Blackout x BlackDiv] Flea guard failed: {ex.Message}");
        }
        return Task.FromResult(true);
    }
}
