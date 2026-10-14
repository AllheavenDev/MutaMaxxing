using FFXIVClientStructs.FFXIV.Client.Game;

namespace MutaMaxxing.Services;

public sealed record InventoryStack(InventoryType Container, ushort Slot, uint ItemId, uint Quantity);

public sealed record InventorySnapshot(bool IsReady, string Message, IReadOnlyList<InventoryStack> Stacks)
{
    public static InventorySnapshot Unavailable(string message) => new(false, message, []);

    public IReadOnlyDictionary<uint, uint> CountByItemId() => Stacks
        .GroupBy(x => x.ItemId)
        .ToDictionary(group => group.Key, group => group.Aggregate(0u, (sum, stack) => sum + stack.Quantity));
}

public sealed unsafe class InventoryReader
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public InventorySnapshot Capture()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return InventorySnapshot.Unavailable("Player inventory is not ready.");

        var stacks = new List<InventoryStack>();
        foreach (var inventoryType in PlayerBags)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null)
                return InventorySnapshot.Unavailable("One or more player inventory bags are not ready.");

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;

                stacks.Add(new InventoryStack(inventoryType, (ushort)slotIndex, slot->ItemId, checked((uint)slot->Quantity)));
            }
        }

        return new InventorySnapshot(true, "Ready", stacks);
    }
}
