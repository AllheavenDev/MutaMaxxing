using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MutaMaxxing.Services;

namespace MutaMaxxing.Services;

public sealed unsafe class TransmutationController
{
    private static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MutamixProximityRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SelectionInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TextAdvanceProbeInterval = TimeSpan.FromSeconds(2);
    public const float MutamixInteractionRange = 5f;

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly MateriaCatalog catalog;
    private readonly InventoryReader inventoryReader;
    private readonly PluginConfig config;
    private readonly TextAdvanceIpc textAdvance;
    private readonly List<SelectedMateria> selectedMateria = [];

    private DateTime inventoryRefreshAt;
    private DateTime mutamixProximityRefreshAt;
    private DateTime nextActionAt;
    private DateTime textAdvanceProbeAt;
    private DateTime waitingSince;
    private PendingSelection? pendingSelection;
    private Dictionary<uint, uint> transactionBaseline = [];
    private IReadOnlyList<(MateriaDefinition Materia, uint Count)> eligibleMateria = [];
    private bool tradeWindowOpen;
    private bool active;
    private bool confirmationDelayAppliedForBatch;
    private float? mutamixDistance;

    public TransmutationController(
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log,
        MateriaCatalog catalog,
        InventoryReader inventoryReader,
        PluginConfig config,
        TextAdvanceIpc textAdvance)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
        this.catalog = catalog;
        this.inventoryReader = inventoryReader;
        this.config = config;
        this.textAdvance = textAdvance;
        Inventory = InventorySnapshot.Unavailable("Player inventory is not ready.");
        Status = "Open Mutamix's Materia Transmutation window to preview eligible materia.";
    }

    public InventorySnapshot Inventory { get; private set; }

    public string Status { get; private set; }

    public bool IsRunning => active;

    public bool TradeWindowOpen => tradeWindowOpen;

    public float? MutamixDistance => mutamixDistance;

    public bool MutamixNearby => mutamixDistance is <= MutamixInteractionRange;

    public bool TextAdvanceEnabled { get; private set; }

    public int CompletedTransmutations { get; private set; }

    public IReadOnlyList<(MateriaDefinition Materia, uint Count)> EligibleMateria => eligibleMateria;

    public uint EligibleItemCount => EligibleMateria.Aggregate(0u, (sum, item) => sum + item.Count);

    public int EligibleTierCount => EligibleMateria
        .GroupBy(item => item.Materia.Tier)
        .Count(CanCompleteTrade);

    public string StartBlockReason
    {
        get
        {
            if (active)
                return "A run is already active.";
            if (!clientState.IsLoggedIn)
                return "Log in before starting.";
            if (!MutamixNearby)
                return mutamixDistance is { } distance
                    ? $"Move within {MutamixInteractionRange:0} yalms of Mutamix (currently {distance:0.0})."
                    : $"Mutamix was not found nearby. Stand within {MutamixInteractionRange:0} yalms before starting.";
            if (!tradeWindowOpen)
                return "Open Mutamix's Materia Transmutation window first.";
            if (!TextAdvanceEnabled)
                return "TextAdvance must be installed, enabled, and responding to IPC.";
            if (!Inventory.IsReady)
                return Inventory.Message;
            if (EligibleItemCount < 5)
                return $"Only {EligibleItemCount} eligible materia found; at least five are needed.";
            if (EligibleTierCount == 0)
                return config.RequireFiveDifferentMateria
                    ? "No single materia grade has five distinct eligible types. A trade cannot mix grades."
                    : "No single materia grade has five eligible materia. A trade cannot mix grades.";

            var agent = AgentTradeMultiple.Instance();
            if (agent == null)
                return "The transmutation agent is not ready yet.";
            if (GetSelectedMateriaCount(agent) != 0)
                return "Clear any materia already selected in the game window before starting.";

            return string.Empty;
        }
    }

    public void SetTradeWindowOpen(bool isOpen)
    {
        tradeWindowOpen = isOpen;
        if (!isOpen && active)
            Stop("Stopped because Mutamix's transmutation window closed.");
    }

    public void Update()
    {
        var now = DateTime.UtcNow;
        if (!clientState.IsLoggedIn)
        {
            if (active)
                Stop("Stopped because the character logged out.");
            Inventory = InventorySnapshot.Unavailable("Log in to scan player inventory.");
            mutamixDistance = null;
            RefreshEligibility();
            return;
        }

        if (now >= mutamixProximityRefreshAt)
        {
            mutamixProximityRefreshAt = now + MutamixProximityRefreshInterval;
            RefreshMutamixProximity();
            if (active && !MutamixNearby)
            {
                Stop("Stopped because you moved outside Mutamix's 5-yalm range.");
                return;
            }
        }

        if (now >= textAdvanceProbeAt)
        {
            textAdvanceProbeAt = now + TextAdvanceProbeInterval;
            TextAdvanceEnabled = textAdvance.IsAvailableAndEnabled();
            if (active && !TextAdvanceEnabled)
            {
                Stop("Stopped because TextAdvance is no longer available and enabled.");
                return;
            }
        }

        if (now >= inventoryRefreshAt)
        {
            inventoryRefreshAt = now + InventoryRefreshInterval;
            Inventory = inventoryReader.Capture();
            RefreshEligibility();
            if (active && !Inventory.IsReady)
            {
                Stop($"Stopped because inventory could not be read: {Inventory.Message}");
                return;
            }
        }

        if (!active || now < nextActionAt)
            return;

        if (!tradeWindowOpen)
        {
            Stop("Stopped because Mutamix's transmutation window closed.");
            return;
        }

        var agent = AgentTradeMultiple.Instance();
        if (agent == null)
        {
            Stop("Stopped because the transmutation agent is unavailable.");
            return;
        }

        var selectedCount = GetSelectedMateriaCount(agent);
        if (selectedCount < 0 || selectedCount > 5)
        {
            Stop("Stopped because the game reported an unexpected materia selection count.");
            return;
        }

        if (transactionBaseline.Count > 0)
        {
            UpdateTransaction(agent, selectedCount, now);
            return;
        }

        UpdateSelection(agent, selectedCount, now);
    }

    public bool CanStart(out string reason)
    {
        reason = StartBlockReason;
        return reason.Length == 0;
    }

    public bool Start()
    {
        RefreshMutamixProximity();
        TextAdvanceEnabled = textAdvance.IsAvailableAndEnabled();
        Inventory = inventoryReader.Capture();
        RefreshEligibility();
        if (!CanStart(out var reason))
        {
            Status = reason;
            return false;
        }

        if (!textAdvance.TryAcquire())
        {
            TextAdvanceEnabled = textAdvance.IsAvailableAndEnabled();
            Status = "TextAdvance could not grant MutaMaxxing control. It may be controlled by another plugin.";
            return false;
        }

        selectedMateria.Clear();
        pendingSelection = null;
        transactionBaseline.Clear();
        confirmationDelayAppliedForBatch = false;
        CompletedTransmutations = 0;
        active = true;
        nextActionAt = DateTime.UtcNow;
        Status = $"Preparing a trade. {EligibleItemCount} eligible materia are in player inventory.";
        log.Information("MutaMaxxing run started with {EligibleCount} eligible materia.", EligibleItemCount);
        return true;
    }

    public void StopByUser()
    {
        var submitted = transactionBaseline.Count > 0;
        var hasSelection = selectedMateria.Count > 0 || pendingSelection != null;
        Stop(submitted
            ? "Stopped by user. A submitted transmutation may still finish; no further trades will be started."
            : hasSelection
                ? "Stopped by user. Clear the unsubmitted materia selection in the game window if you want to change it."
                : "Stopped by user.");
    }

    private void UpdateSelection(AgentTradeMultiple* agent, int selectedCount, DateTime now)
    {
        if (pendingSelection is { } pending)
        {
            if (selectedCount == pending.ExpectedCount)
            {
                selectedMateria.Add(new SelectedMateria(pending.ItemId, pending.Quantity, pending.Container, pending.Slot));
                pendingSelection = null;
            }
            else if (selectedCount > pending.ExpectedCount)
            {
                Stop("Stopped because the game selected more materia than MutaMaxxing requested.");
                return;
            }
            else if (now - pending.RequestedAt > SelectionTimeout)
            {
                Stop("Stopped because the game did not confirm the materia selection.");
                return;
            }
            else
            {
                nextActionAt = now + SelectionInterval;
                return;
            }
        }

        if (selectedCount != selectedMateria.Sum(x => (int)x.Quantity))
        {
            Stop("Stopped because the game selection changed outside MutaMaxxing.");
            return;
        }

        if (selectedCount == 5)
        {
            if (!confirmationDelayAppliedForBatch)
            {
                confirmationDelayAppliedForBatch = true;
                nextActionAt = now + ConfirmationDelay;
                Status = "Five materia selected. Waiting briefly before confirmation.";
                return;
            }

            confirmationDelayAppliedForBatch = false;
            SubmitTrade(agent, now);
            return;
        }

        var candidate = FindNextStack(agent);
        if (candidate is null)
        {
            Stop(config.RequireFiveDifferentMateria
                ? "Stopped because no single grade has five distinct eligible materia types for this trade. Clear any partial selection in Mutamix before restarting."
                : "Stopped because no single grade has five eligible materia for this trade. Clear any partial selection in Mutamix before restarting.");
            return;
        }

        var quantity = config.RequireFiveDifferentMateria
            ? 1
            : Math.Min(5 - selectedCount, checked((int)candidate.Quantity));
        if (quantity <= 0)
        {
            Stop("Stopped because an eligible inventory stack had an invalid quantity.");
            return;
        }

        var before = selectedCount;
        try
        {
            var inventoryManager = InventoryManager.Instance();
            var container = inventoryManager == null ? null : inventoryManager->GetInventoryContainer(candidate.Container);
            var slot = container == null ? null : container->GetInventorySlot(candidate.Slot);
            if (slot == null || slot->ItemId != candidate.ItemId || slot->Quantity < quantity)
            {
                Stop("Stopped because an inventory slot changed while the preview was being used.");
                return;
            }

            agent->AddItemQuantity(slot, (uint)quantity);
            var expected = before + quantity;
            pendingSelection = new PendingSelection(candidate.ItemId, (uint)quantity, candidate.Container, candidate.Slot, expected, now);
            var microDelay = TimeSpan.FromMilliseconds(Random.Shared.Next(5, 11));
            nextActionAt = now + SelectionInterval + microDelay;
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not select materia.");
            Stop("Stopped because the game rejected a materia selection.");
        }
    }

    private InventoryStack? FindNextStack(AgentTradeMultiple* agent)
    {
        var batchTier = GetBatchTier();
        if (batchTier is null)
            return null;

        var selectedSlots = selectedMateria.Select(x => (x.Container, x.Slot)).ToHashSet();
        var selectedItemIds = selectedMateria.Select(x => x.ItemId).ToHashSet();
        if (pendingSelection is { } pending)
        {
            selectedSlots.Add((pending.Container, pending.Slot));
            selectedItemIds.Add(pending.ItemId);
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        foreach (var stack in Inventory.Stacks)
        {
            if (stack.Quantity == 0 || !catalog.TryGet(stack.ItemId, out var definition) || !IsEligible(definition))
                continue;

            if (definition.Tier != batchTier.Value)
                continue;

            if (config.RequireFiveDifferentMateria && selectedItemIds.Contains(stack.ItemId))
                continue;

            if (selectedSlots.Contains((stack.Container, stack.Slot)))
                continue;

            var container = manager->GetInventoryContainer(stack.Container);
            var slot = container == null ? null : container->GetInventorySlot(stack.Slot);
            if (slot == null || IsSelected(agent, stack.Container, stack.Slot))
                continue;

            return stack;
        }

        return null;
    }

    private void SubmitTrade(AgentTradeMultiple* agent, DateTime now)
    {
        if (selectedMateria.Sum(x => (int)x.Quantity) != 5)
        {
            Stop("Stopped because the selected materia did not match the five-item trade requirement.");
            return;
        }

        if (config.RequireFiveDifferentMateria &&
            (selectedMateria.Any(x => x.Quantity != 1) || selectedMateria.Select(x => x.ItemId).Distinct().Count() != 5))
        {
            Stop("Stopped because the selected batch did not contain five different materia types.");
            return;
        }

        if (!SelectedMateriaShareTier())
        {
            Stop("Stopped because the selected batch contained more than one materia grade.");
            return;
        }

        Inventory = inventoryReader.Capture();
        if (!Inventory.IsReady)
        {
            Stop($"Stopped before trade because inventory could not be rechecked: {Inventory.Message}");
            return;
        }
        RefreshEligibility();

        foreach (var selected in selectedMateria)
        {
            var currentStack = Inventory.Stacks.FirstOrDefault(stack =>
                stack.Container == selected.Container &&
                stack.Slot == selected.Slot &&
                stack.ItemId == selected.ItemId);
            if (currentStack is null || currentStack.Quantity < selected.Quantity || !catalog.TryGet(selected.ItemId, out var definition) || !IsEligible(definition))
            {
                Stop("Stopped before trade because a selected inventory item changed or no longer matches the active rules.");
                return;
            }
        }

        if (GetSelectedMateriaCount(agent) != 5)
        {
            Stop("Stopped before trade because the game selection no longer contains exactly five materia.");
            return;
        }

        var counts = Inventory.CountByItemId();
        transactionBaseline = selectedMateria
            .Select(x => x.ItemId)
            .Distinct()
            .ToDictionary(itemId => itemId, itemId => counts.GetValueOrDefault(itemId));

        try
        {
            if (!agent->Confirm())
            {
                transactionBaseline.Clear();
                Stop("Stopped because the game rejected the transmutation confirmation.");
                return;
            }

            waitingSince = now;
            nextActionAt = now + InventoryRefreshInterval;
            Status = "Trade submitted. Waiting for the five selected materia to leave inventory.";
            log.Information("MutaMaxxing submitted transmutation {TradeNumber}.", CompletedTransmutations + 1);
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not submit a transmutation.");
            transactionBaseline.Clear();
            Stop("Stopped because the game rejected the transmutation request.");
        }
    }

    private void UpdateTransaction(AgentTradeMultiple* agent, int selectedCount, DateTime now)
    {
        var currentCounts = Inventory.CountByItemId();
        long removedCount = 0;
        foreach (var pair in transactionBaseline)
            removedCount += (long)pair.Value - currentCounts.GetValueOrDefault(pair.Key);

        if (removedCount > 5)
        {
            Stop("Stopped because inventory changed by more than the submitted five-item trade.");
            return;
        }

        if (removedCount == 5 && selectedCount == 0)
        {
            CompletedTransmutations++;
            selectedMateria.Clear();
            pendingSelection = null;
            confirmationDelayAppliedForBatch = false;
            transactionBaseline.Clear();
            Status = $"Trade {CompletedTransmutations} complete. Checking for the next eligible batch.";
            var interTradeDelay = config.RandomizeInterTradeDelay
                ? TimeSpan.FromMilliseconds(Random.Shared.Next(50, 71))
                : TimeSpan.Zero;
            nextActionAt = now + TimeSpan.FromMilliseconds(350) + interTradeDelay;
            return;
        }

        if (now - waitingSince > TransactionTimeout)
        {
            Stop("Stopped because inventory did not confirm the submitted trade. MutaMaxxing will not retry it automatically.");
            return;
        }

        nextActionAt = now + InventoryRefreshInterval;
    }

    public void Dispose() => Stop("Plugin unloaded.");

    public void RefreshEligibility()
    {
        if (!Inventory.IsReady)
        {
            eligibleMateria = [];
            return;
        }

        var counts = Inventory.CountByItemId();
        eligibleMateria = catalog.Entries
            .Where(IsEligible)
            .Select(item => (Materia: item, Count: counts.GetValueOrDefault(item.ItemId)))
            .Where(pair => pair.Count > 0)
            .ToArray();
    }

    private bool CanCompleteTrade(IGrouping<int, (MateriaDefinition Materia, uint Count)> tierGroup) =>
        config.RequireFiveDifferentMateria
            ? tierGroup.Select(item => item.Materia.ItemId).Distinct().Take(5).Count() == 5
            : tierGroup.Sum(item => (long)item.Count) >= 5;

    private int? GetBatchTier()
    {
        if (selectedMateria.Count > 0 && catalog.TryGet(selectedMateria[0].ItemId, out var selectedDefinition))
            return selectedDefinition.Tier;

        if (pendingSelection is { } pending && catalog.TryGet(pending.ItemId, out var pendingDefinition))
            return pendingDefinition.Tier;

        return EligibleMateria
            .GroupBy(item => item.Materia.Tier)
            .Where(CanCompleteTrade)
            .Select(group => new
            {
                Tier = group.Key,
                ItemCount = group.Sum(item => (long)item.Count),
                DistinctTypeCount = group.Select(item => item.Materia.ItemId).Distinct().Count(),
            })
            .OrderByDescending(group => config.RequireFiveDifferentMateria ? group.DistinctTypeCount : group.ItemCount)
            .ThenByDescending(group => group.ItemCount)
            .ThenByDescending(group => group.Tier)
            .Select(group => (int?)group.Tier)
            .FirstOrDefault();
    }

    private bool SelectedMateriaShareTier()
    {
        int? selectedTier = null;
        foreach (var selected in selectedMateria)
        {
            if (!catalog.TryGet(selected.ItemId, out var definition))
                return false;

            if (selectedTier is { } tier && tier != definition.Tier)
                return false;

            selectedTier = definition.Tier;
        }

        return selectedTier.HasValue;
    }

    private void RefreshMutamixProximity()
    {
        mutamixDistance = null;
        if (!clientState.IsLoggedIn || objectTable[0] is not { } player)
            return;

        var closestDistanceSquared = float.PositiveInfinity;
        foreach (var gameObject in objectTable)
        {
            if (gameObject is null ||
                gameObject.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc) ||
                !gameObject.Name.TextValue.Contains("Mutamix", StringComparison.OrdinalIgnoreCase))
                continue;

            var distanceSquared = Vector3.DistanceSquared(player.Position, gameObject.Position);
            if (distanceSquared < closestDistanceSquared)
                closestDistanceSquared = distanceSquared;
        }

        if (float.IsFinite(closestDistanceSquared))
            mutamixDistance = MathF.Sqrt(closestDistanceSquared);
    }

    private bool IsEligible(MateriaDefinition materia)
    {
        if (config.BlacklistMateriaIds.Contains(materia.ItemId) || MatchesAny(config.BlacklistRules, materia))
            return false;

        if (config.TradeAllExceptBlacklist)
            return true;

        return config.WhitelistMateriaIds.Contains(materia.ItemId) || MatchesAny(config.WhitelistRules, materia);
    }

    private static bool MatchesAny(IEnumerable<MateriaRule> rules, MateriaDefinition materia) =>
        rules.Any(rule => rule.Category == materia.Category && (rule.Tier == 0 || rule.Tier == materia.Tier));

    private void Stop(string message)
    {
        var wasActive = active;
        active = false;
        confirmationDelayAppliedForBatch = false;
        pendingSelection = null;
        selectedMateria.Clear();
        transactionBaseline.Clear();
        textAdvance.Release();
        Status = message;
        if (wasActive)
            log.Information("MutaMaxxing stopped: {Reason}", message);
    }

    private sealed record SelectedMateria(uint ItemId, uint Quantity, InventoryType Container, ushort Slot);

    private sealed record PendingSelection(uint ItemId, uint Quantity, InventoryType Container, ushort Slot, int ExpectedCount, DateTime RequestedAt);

    private static int GetSelectedMateriaCount(AgentTradeMultiple* agent)
    {
        var selectedCount = 0;
        foreach (var slot in agent->Slots)
        {
            if (slot.ItemId != 0 && slot.Quantity > 0)
                selectedCount += slot.Quantity;
        }

        return selectedCount;
    }

    private static bool IsSelected(AgentTradeMultiple* agent, InventoryType container, ushort slotIndex)
    {
        foreach (var slot in agent->Slots)
        {
            if (slot.ItemId != 0 && slot.Container == container && slot.Slot == (short)slotIndex)
                return true;
        }

        return false;
    }
}
