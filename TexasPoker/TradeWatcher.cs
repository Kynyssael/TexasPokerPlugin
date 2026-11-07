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
/// </summary>
public sealed unsafe class TradeWatcher : IDisposable
{
    private const string TradeAddon = "Trade";

    // The game updates your gil a moment after the window closes; give up after this.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Action<string, string, long> finished;

    private bool open;
    private DateTime? closedAt;
    private uint gilBefore;
    private string partnerName = string.Empty;
    private string partnerWorld = string.Empty;

    /// <param name="finished">Called with the partner's name, home world (may be empty) and the gil you gained (below 0: gave).</param>
    public TradeWatcher(IAddonLifecycle lifecycle, IFramework framework, IObjectTable objects, Action<string, string, long> finished)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.objects = objects;
        this.finished = finished;
        lifecycle.RegisterListener(AddonEvent.PostSetup, TradeAddon, OnOpened);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, TradeAddon, OnClosed);
        framework.Update += OnUpdate;
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

        open = true;
        closedAt = null;
        gilBefore = inventory->GetGil();
        partnerName = string.Empty;
        partnerWorld = string.Empty;
        ReadPartner(inventory);
    }

    private void OnClosed(AddonEvent type, AddonArgs args)
    {
        if (!open)
            return;

        open = false;
        closedAt = DateTime.UtcNow;
    }

    private void OnUpdate(IFramework _)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return;

        // The partner may only be known once the window is filled in.
        if (open && partnerName.Length == 0)
            ReadPartner(inventory);

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
