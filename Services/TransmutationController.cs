using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MutaMaxxing.Services;

namespace MutaMaxxing.Services;

public sealed unsafe class TransmutationController
{
    private static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SelectionInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TextAdvanceProbeInterval = TimeSpan.FromSeconds(2);

    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly MateriaCatalog catalog;
    private readonly InventoryReader inventoryReader;
    private readonly PluginConfig config;
    private readonly TextAdvanceIpc textAdvance;
    private readonly List<SelectedMateria> selectedMateria = [];

    private DateTime inventoryRefreshAt;
    private DateTime nextActionAt;
    private DateTime textAdvanceProbeAt;
    private DateTime waitingSince;
    private PendingSelection? pendingSelection;
    private Dictionary<uint, uint> transactionBaseline = [];
    private IReadOnlyList<(MateriaDefinition Materia, uint Count)> eligibleMateria = [];
    private bool tradeWindowOpen;
    private bool active;

    public TransmutationController(
        IClientState clientState,
        IPluginLog log,
        MateriaCatalog catalog,
        InventoryReader inventoryReader,
        PluginConfig config,
        TextAdvanceIpc textAdvance)
    {
        this.clientState = clientState;
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

    public bool TextAdvanceEnabled { get; private set; }

    public int CompletedTransmutations { get; private set; }

    public IReadOnlyList<(MateriaDefinition Materia, uint Count)> EligibleMateria => eligibleMateria;

    public uint EligibleItemCount => EligibleMateria.Aggregate(0u, (sum, item) => sum + item.Count);

    public string StartBlockReason
    {
        get
        {
            if (active)
                return "A run is already active.";
            if (!clientState.IsLoggedIn)
                return "Log in before starting.";
            if (!tradeWindowOpen)
                return "Open Mutamix's Materia Transmutation window first.";
            if (!TextAdvanceEnabled)
                return "TextAdvance must be installed, enabled, and responding to IPC.";
            if (!Inventory.IsReady)
                return Inventory.Message;
            if (EligibleItemCount < 5)
                return $"Only {EligibleItemCount} eligible materia found; at least five are needed.";

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
            RefreshEligibility();
            return;
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
            SubmitTrade(agent, now);
            return;
        }

        var candidate = FindNextStack(agent);
        if (candidate is null)
        {
            Stop($"Stopped with {EligibleItemCount} eligible materia remaining; fewer than five can be selected safely.");
            return;
        }

        var quantity = Math.Min(5 - selectedCount, checked((int)candidate.Quantity));
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
            nextActionAt = now + SelectionInterval;
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not select materia.");
            Stop("Stopped because the game rejected a materia selection.");
        }
    }

    private InventoryStack? FindNextStack(AgentTradeMultiple* agent)
    {
        var selectedSlots = selectedMateria.Select(x => (x.Container, x.Slot)).ToHashSet();
        if (pendingSelection is { } pending)
            selectedSlots.Add((pending.Container, pending.Slot));

        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;

        foreach (var stack in Inventory.Stacks)
        {
            if (stack.Quantity == 0 || !catalog.TryGet(stack.ItemId, out var definition) || !IsEligible(definition))
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
            transactionBaseline.Clear();
            Status = $"Trade {CompletedTransmutations} complete. Checking for the next eligible batch.";
            nextActionAt = now + TimeSpan.FromMilliseconds(350);
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
