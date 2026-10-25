namespace MutaMaxxing.Core;

public static class MateriaTransmutationResult
{
    public static uint? IdentifyReceivedMateria(
        IEnumerable<uint> materiaItemIds,
        IReadOnlyDictionary<uint, uint> currentCounts,
        IReadOnlyDictionary<uint, uint> baselineCounts,
        IReadOnlyDictionary<uint, uint> inputQuantities)
    {
        var gained = materiaItemIds
            .Distinct()
            .Select(itemId => new
            {
                ItemId = itemId,
                Quantity = (long)currentCounts.GetValueOrDefault(itemId)
                           - baselineCounts.GetValueOrDefault(itemId)
                           + inputQuantities.GetValueOrDefault(itemId),
            })
            .Where(item => item.Quantity > 0)
            .ToArray();

        return gained.Length == 1 && gained[0].Quantity == 1 ? gained[0].ItemId : null;
    }
}
