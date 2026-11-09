using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TexasPoker;

/// <summary>
/// Notices finished trades where gil changed hands. When the trade window opens it notes who you're trading with
/// and your gil; after it closes, a change in your gil is what the trade moved (a cancelled trade changes nothing).
/// It reads only your own gil and the trade partner, never anything else in the trade.
/// For a payout it can also ask the player for a trade and, once they accept, type the gil in (<see cref="Offer"/>).
/// It never presses Trade or Confirm: the dealer checks the amount and does that.
/// </summary>
public sealed unsafe class TradeWatcher : IDisposable
{
    private const string TradeAddon = "Trade";

    // The game updates your gil a moment after the window closes; give up after this.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    // How long a trade request waits for the player to accept, and how long after the window opens the gil goes in.
    private static readonly TimeSpan OfferWait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FillDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PartnerWait = TimeSpan.FromSeconds(3);

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Action<string, string, long> finished;
    private readonly Action<string, bool> notice;
    private readonly Func<bool> tracking;

    private bool open;
    private DateTime openedAt;
    private (string Name, uint Amount, DateTime Until)? offer;
    private DateTime? fillAt;
    private DateTime? closedAt;
    private uint gilBefore;
    private string partnerName = string.Empty;
    private string partnerWorld = string.Empty;

    /// <param name="finished">Called with the partner's name, home world (may be empty) and the gil you gained (below 0: gave).</param>
    /// <param name="notice">Called with what happened to a payout offer, and whether it's a warning.</param>
    /// <param name="tracking">
    /// Whether to watch trades now (the poker window is open). Checked when a trade window opens, so a trade
    /// already open counts even if the poker window closes before it's done.
    /// </param>
    public TradeWatcher(IAddonLifecycle lifecycle, IFramework framework, IObjectTable objects, Action<string, string, long> finished,
                        Action<string, bool> notice, Func<bool> tracking)
    {
        this.tracking = tracking;
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.objects = objects;
        this.finished = finished;
        this.notice = notice;
        lifecycle.RegisterListener(AddonEvent.PostSetup, TradeAddon, OnOpened);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, TradeAddon, OnClosed);
        framework.Update += OnUpdate;
    }

    /// <summary>The trade window is open.</summary>
    public bool Trading => open;

    /// <summary>
    /// Asks the player for a trade. When they accept, and only if the trade is with them, the gil is typed in for
    /// the dealer to check; the dealer presses Trade and Confirm.
    /// </summary>
    public void Offer(uint entityId, string name, uint amount)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            throw new InvalidOperationException("The game isn't ready to trade.");
        if (open)
            throw new InvalidOperationException("Finish or cancel the trade that's open first.");
        if (inventory->GetGil() < amount)
            throw new InvalidOperationException($"You have {inventory->GetGil():N0} gil, not enough to pay {amount:N0}.");

        offer = (name, amount, DateTime.UtcNow + OfferWait);
        fillAt = null;
        inventory->SendTradeRequest(entityId);
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PostSetup, TradeAddon, OnOpened);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, TradeAddon, OnClosed);
        framework.Update -= OnUpdate;
    }

    private void OnOpened(AddonEvent type, AddonArgs args)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return;

        // Trades while you're not running the table are your own business: not watched, not logged.
        if (!tracking() && offer is null)
            return;

        open = true;
        closedAt = null;
        gilBefore = inventory->GetGil();
        partnerName = string.Empty;
        partnerWorld = string.Empty;
        ReadPartner(inventory);

        openedAt = DateTime.UtcNow;
        if (offer is { } o && DateTime.UtcNow < o.Until)
            fillAt = openedAt + FillDelay;
        else
            offer = null;
    }

    private void OnClosed(AddonEvent type, AddonArgs args)
    {
        if (!open)
            return;

        open = false;
        closedAt = DateTime.UtcNow;
        offer = null;
        fillAt = null;
    }

    private void OnUpdate(IFramework _)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return;

        // The partner may only be known once the window is filled in.
        if (open && partnerName.Length == 0)
            ReadPartner(inventory);

        if (open && fillAt is { } due && DateTime.UtcNow >= due)
            FillOffer(inventory);
        else if (!open && offer is { } waiting && DateTime.UtcNow > waiting.Until)
            offer = null; // they never accepted

        if (closedAt is not { } closed)
            return;

        var gil = inventory->GetGil();
        if (gil != gilBefore)
        {
            closedAt = null;
            if (partnerName.Length > 0)
                finished(partnerName, partnerWorld, (long)gil - gilBefore);
        }
        else if (DateTime.UtcNow - closed > SettleTime)
        {
            closedAt = null;
        }
    }

    /// <summary>Types the payout's gil into the open trade, if it's with the right player.</summary>
    private void FillOffer(InventoryManager* inventory)
    {
        if (offer is not { } o)
        {
            fillAt = null;
            return;
        }

        // The partner's name can take a moment to fill in.
        if (partnerName.Length == 0 && DateTime.UtcNow - openedAt < PartnerWait)
        {
            fillAt = DateTime.UtcNow + FillDelay;
            return;
        }

        fillAt = null;
        offer = null;
        if (!partnerName.Equals(o.Name, StringComparison.OrdinalIgnoreCase))
        {
            notice($"The trade opened with {(partnerName.Length > 0 ? partnerName : "someone else")}, not {o.Name}: no gil was typed in.", true);
            return;
        }

        if (inventory->GetGil() < o.Amount)
        {
            notice($"You have {inventory->GetGil():N0} gil, not enough to pay {o.Name} {o.Amount:N0}: no gil was typed in.", true);
            return;
        }

        inventory->SetTradeGilAmount(o.Amount);
        notice($"{o.Amount:N0} gil typed in for {o.Name}. Check the amount, then press Trade and Confirm.", false);
    }

    private void ReadPartner(InventoryManager* inventory)
    {
        // The partner's character gives their home world; their name alone is the fallback.
        if (objects.SearchByEntityId(inventory->TradePartnerEntityId) is IPlayerCharacter player)
        {
            partnerName = player.Name.TextValue;
            partnerWorld = player.HomeWorld.Value.Name.ToString();
            return;
        }

        partnerName = inventory->TradePartnerNameString?.Trim() ?? string.Empty;
    }
}
