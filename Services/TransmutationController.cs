using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MutaMaxxing.Core;
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
    private Dictionary<uint, uint> transactionMateriaBaseline = [];
    private Dictionary<uint, uint> transactionInputQuantities = [];
    private long transactionMateriaTotalBaseline;
    private readonly Dictionary<uint, int> receivedMateriaCounts = [];
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

    public IReadOnlyDictionary<uint, int> ReceivedMateriaCounts => receivedMateriaCounts;

    public int UnidentifiedResultCount { get; private set; }

    public bool HasRunSummary { get; private set; }

    public string LastRunSummary { get; private set; } = string.Empty;

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
            if (config.ControlTextAdvanceDuringRuns)
            {
                if (!textAdvance.CanAcquireExternalControl)
                    return "TextAdvance must be installed and responding to IPC.";
            }
            else if (!TextAdvanceEnabled)
            {
                return "Enable TextAdvance or let MutaMaxxing control it during runs.";
            }
            if (!Inventory.IsReady)
                return Inventory.Message;
            if (EligibleItemCount < 5)
                return $"Only {EligibleItemCount} eligible materia found; at least five are needed.";
            if (EligibleTierCount == 0)
                return config.RequireFiveDifferentMateria && !config.FallbackToMostCommonMateria
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

        if (config.ControlTextAdvanceDuringRuns && !textAdvance.TryAcquire())
        {
            TextAdvanceEnabled = textAdvance.IsAvailableAndEnabled();
            Status = "TextAdvance could not grant MutaMaxxing control. Another plugin may own its IPC control.";
            return false;
        }

        TextAdvanceEnabled = textAdvance.IsAvailableAndEnabled();

        selectedMateria.Clear();
        pendingSelection = null;
        ClearTransactionTracking();
        confirmationDelayAppliedForBatch = false;
        CompletedTransmutations = 0;
        receivedMateriaCounts.Clear();
        UnidentifiedResultCount = 0;
        HasRunSummary = false;
        LastRunSummary = string.Empty;
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
            var finished = selectedCount == 0 && selectedMateria.Count == 0 && CompletedTransmutations > 0;
            Stop(finished
                ? "Run complete: no complete eligible batch remains."
                : config.RequireFiveDifferentMateria && !config.FallbackToMostCommonMateria
                    ? "Stopped because no single grade has five distinct eligible materia types for this trade. Clear any partial selection in Mutamix before restarting."
                    : "Stopped because no single grade has five eligible materia for this trade. Clear any partial selection in Mutamix before restarting.");
            return;
        }

        var candidateTier = catalog.TryGet(candidate.ItemId, out var candidateDefinition) ? candidateDefinition.Tier : 0;
        var isFallbackBatch = config.RequireFiveDifferentMateria &&
                              config.FallbackToMostCommonMateria &&
                              IsFallbackTier(candidateTier);
        var quantity = Math.Min(5 - selectedCount, checked((int)candidate.Quantity));
        if (config.RequireFiveDifferentMateria)
        {
            if (!isFallbackBatch)
            {
                quantity = 1;
            }
            else
            {
                var plannedQuantity = GetFallbackBatchPlan(candidateTier)
                    .FirstOrDefault(item => item.ItemId == candidate.ItemId)
                    .Quantity;
                var alreadySelected = selectedMateria
                    .Where(item => item.ItemId == candidate.ItemId)
                    .Sum(item => (int)item.Quantity);
                quantity = Math.Min(quantity, plannedQuantity - alreadySelected);
            }
        }
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

        var fallbackBatch = IsFallbackTier(batchTier.Value);
        IReadOnlyList<MateriaBatchItem> fallbackPlan = fallbackBatch
            ? GetFallbackBatchPlan(batchTier.Value)
            : [];

        var selectedSlots = selectedMateria.Select(x => (x.Container, x.Slot)).ToHashSet();
        var selectedQuantities = selectedMateria
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (int)item.Quantity));
        if (pendingSelection is { } pending)
        {
            selectedSlots.Add((pending.Container, pending.Slot));
            selectedQuantities[pending.ItemId] = selectedQuantities.GetValueOrDefault(pending.ItemId) + checked((int)pending.Quantity);
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        var candidates = new List<InventoryStack>();
        foreach (var stack in Inventory.Stacks)
        {
            if (stack.Quantity == 0 || !catalog.TryGet(stack.ItemId, out var definition) || !IsEligible(definition))
                continue;

            if (definition.Tier != batchTier.Value)
                continue;

            if (config.RequireFiveDifferentMateria && !fallbackBatch && selectedQuantities.ContainsKey(stack.ItemId))
                continue;

            if (selectedSlots.Contains((stack.Container, stack.Slot)))
                continue;

            var container = manager->GetInventoryContainer(stack.Container);
            var slot = container == null ? null : container->GetInventorySlot(stack.Slot);
            if (slot == null || IsSelected(agent, stack.Container, stack.Slot))
                continue;

            candidates.Add(stack);
        }

        if (!fallbackBatch)
            return candidates.FirstOrDefault();

        var nextType = MateriaBatchPlanner.GetNextPlannedItemId(fallbackPlan, selectedQuantities);
        if (nextType is null)
            return null;

        return candidates
            .Where(stack => stack.ItemId == nextType.Value)
            .OrderByDescending(stack => stack.Quantity)
            .FirstOrDefault();
    }

    private void SubmitTrade(AgentTradeMultiple* agent, DateTime now)
    {
        if (selectedMateria.Sum(x => (int)x.Quantity) != 5)
        {
            Stop("Stopped because the selected materia did not match the five-item trade requirement.");
            return;
        }

        if (!SelectedMateriaShareTier())
        {
            Stop("Stopped because the selected batch contained more than one materia grade.");
            return;
        }

        if (config.RequireFiveDifferentMateria && !HasValidDifferentMateriaSelection())
        {
            Stop("Stopped because the selected batch did not follow the five-different materia rule or its fallback.");
            return;
        }

        Inventory = inventoryReader.Capture();
        if (!Inventory.IsReady)
        {
            Stop($"Stopped before trade because inventory could not be rechecked: {Inventory.Message}");
            return;
        }
        RefreshEligibility();

        if (config.RequireFiveDifferentMateria && !HasValidDifferentMateriaSelection())
        {
            Stop("Stopped before trade because inventory changed and the selected batch no longer matches the five-different rule or fallback.");
            return;
        }

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
        transactionMateriaBaseline = catalog.Entries
            .ToDictionary(item => item.ItemId, item => counts.GetValueOrDefault(item.ItemId));
        transactionInputQuantities = selectedMateria
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Aggregate(0u, (sum, item) => checked(sum + item.Quantity)));
        transactionMateriaTotalBaseline = transactionMateriaBaseline.Values.Aggregate(0L, (sum, count) => sum + count);

        try
        {
            if (!agent->Confirm())
            {
                ClearTransactionTracking();
                Stop("Stopped because the game rejected the transmutation confirmation.");
                return;
            }

            waitingSince = now;
            nextActionAt = now + InventoryRefreshInterval;
            Status = "Trade submitted. Waiting for inventory to confirm the exchange.";
            log.Information("MutaMaxxing submitted transmutation {TradeNumber}.", CompletedTransmutations + 1);
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not submit a transmutation.");
            ClearTransactionTracking();
            Stop("Stopped because the game rejected the transmutation request.");
        }
    }

    private void UpdateTransaction(AgentTradeMultiple* agent, int selectedCount, DateTime now)
    {
        var currentCounts = Inventory.CountByItemId();
        long removedCount = 0;
        foreach (var pair in transactionBaseline)
            removedCount += (long)pair.Value - currentCounts.GetValueOrDefault(pair.Key);

        var currentMateriaTotal = catalog.Entries.Aggregate(0L, (sum, item) => sum + currentCounts.GetValueOrDefault(item.ItemId));
        var materiaTotalRemoved = transactionMateriaTotalBaseline - currentMateriaTotal;

        if (removedCount > 5)
        {
            Stop("Stopped because inventory changed by more than the submitted five-item trade.");
            return;
        }

        if (materiaTotalRemoved == 4 && selectedCount == 0 && removedCount is 4 or 5)
        {
            RecordReceivedMateria(currentCounts);
            CompletedTransmutations++;
            selectedMateria.Clear();
            pendingSelection = null;
            confirmationDelayAppliedForBatch = false;
            ClearTransactionTracking();
            Status = UnidentifiedResultCount == 0
                ? $"Trade {CompletedTransmutations} complete. Checking for the next eligible batch."
                : $"Trade {CompletedTransmutations} complete. One or more results could not be identified; checking for the next eligible batch.";
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

    private void RecordReceivedMateria(IReadOnlyDictionary<uint, uint> currentCounts)
    {
        var receivedItemId = MateriaTransmutationResult.IdentifyReceivedMateria(
            catalog.Entries.Select(item => item.ItemId),
            currentCounts,
            transactionMateriaBaseline,
            transactionInputQuantities);

        if (receivedItemId is not { } itemId)
        {
            UnidentifiedResultCount++;
            return;
        }

        receivedMateriaCounts[itemId] = receivedMateriaCounts.GetValueOrDefault(itemId) + 1;
    }

    private void ClearTransactionTracking()
    {
        transactionBaseline.Clear();
        transactionMateriaBaseline.Clear();
        transactionInputQuantities.Clear();
        transactionMateriaTotalBaseline = 0;
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

    private bool HasValidDifferentMateriaSelection()
    {
        if (selectedMateria.Count == 0 || !catalog.TryGet(selectedMateria[0].ItemId, out var selectedDefinition))
            return false;

        var selectedQuantities = selectedMateria
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (int)item.Quantity));
        return MateriaBatchPlanner.IsValidDifferentMateriaSelection(
            GetMateriaInventoryEntries(selectedDefinition.Tier),
            selectedQuantities,
            config.FallbackToMostCommonMateria);
    }

    private int? GetBatchTier()
    {
        if (selectedMateria.Count > 0 && catalog.TryGet(selectedMateria[0].ItemId, out var selectedDefinition))
            return selectedDefinition.Tier;

        if (pendingSelection is { } pending && catalog.TryGet(pending.ItemId, out var pendingDefinition))
            return pendingDefinition.Tier;

        return MateriaBatchPlanner.SelectTier(
            GetMateriaInventoryEntries(),
            config.RequireFiveDifferentMateria,
            config.FallbackToMostCommonMateria);
    }

    private bool CanCompleteTrade(IGrouping<int, (MateriaDefinition Materia, uint Count)> tierGroup) =>
        MateriaBatchPlanner.CanCompleteTrade(
            tierGroup.Select(item => new MateriaInventoryEntry(item.Materia, item.Count)),
            config.RequireFiveDifferentMateria,
            config.FallbackToMostCommonMateria);

    private bool IsFallbackTier(int tier) => MateriaBatchPlanner.IsFallbackTier(
        GetMateriaInventoryEntries(tier),
        config.RequireFiveDifferentMateria,
        config.FallbackToMostCommonMateria);

    private IReadOnlyList<MateriaBatchItem> GetFallbackBatchPlan(int tier) =>
        MateriaBatchPlanner.CreateFallbackPlan(GetMateriaInventoryEntries(tier));

    private MateriaInventoryEntry[] GetMateriaInventoryEntries(int? tier = null) => EligibleMateria
        .Where(item => tier is null || item.Materia.Tier == tier.Value)
        .Select(item => new MateriaInventoryEntry(item.Materia, item.Count))
        .ToArray();

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

    private bool IsEligible(MateriaDefinition materia) => config.CreateEligibilityPolicy().IsEligible(materia);

    private void Stop(string message)
    {
        var wasActive = active;
        active = false;
        confirmationDelayAppliedForBatch = false;
        pendingSelection = null;
        selectedMateria.Clear();
        ClearTransactionTracking();
        textAdvance.Release();
        Status = message;
        if (wasActive)
        {
            HasRunSummary = true;
            LastRunSummary = $"Run ended after {CompletedTransmutations} completed trade(s). {message}";
            log.Information("MutaMaxxing stopped: {Reason}", message);
        }
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
