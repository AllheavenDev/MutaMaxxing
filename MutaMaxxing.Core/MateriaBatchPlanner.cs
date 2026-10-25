namespace MutaMaxxing.Core;

public readonly record struct MateriaInventoryEntry(MateriaDefinition Materia, uint Count);

public readonly record struct MateriaBatchItem(uint ItemId, int Quantity);

public static class MateriaBatchPlanner
{
    public const int TradeSize = 5;

    public static int? SelectTier(
        IEnumerable<MateriaInventoryEntry> eligibleMateria,
        bool requireFiveDifferent,
        bool fallbackToMostCommon)
    {
        var candidates = eligibleMateria
            .GroupBy(item => item.Materia.Tier)
            .Where(group => CanCompleteTrade(group, requireFiveDifferent, fallbackToMostCommon))
            .Select(group => new
            {
                Tier = group.Key,
                ItemCount = group.Sum(item => (long)item.Count),
                DistinctTypeCount = group.Select(item => item.Materia.ItemId).Distinct().Count(),
            });

        return candidates
            .OrderByDescending(group => requireFiveDifferent ? group.DistinctTypeCount : group.ItemCount)
            .ThenByDescending(group => group.ItemCount)
            .ThenByDescending(group => group.Tier)
            .Select(group => (int?)group.Tier)
            .FirstOrDefault();
    }

    public static bool CanCompleteTrade(
        IEnumerable<MateriaInventoryEntry> sameTierMateria,
        bool requireFiveDifferent,
        bool fallbackToMostCommon)
    {
        var entries = sameTierMateria.ToArray();
        if (entries.Length == 0 || entries.Any(item => item.Materia.Tier != entries[0].Materia.Tier))
            return false;

        var itemCount = entries.Sum(item => (long)item.Count);
        if (!requireFiveDifferent)
            return itemCount >= TradeSize;

        var distinctTypeCount = entries.Select(item => item.Materia.ItemId).Distinct().Take(TradeSize).Count();
        return distinctTypeCount == TradeSize ||
               fallbackToMostCommon && distinctTypeCount < TradeSize && itemCount >= TradeSize;
    }

    public static bool IsFallbackTier(
        IEnumerable<MateriaInventoryEntry> sameTierMateria,
        bool requireFiveDifferent,
        bool fallbackToMostCommon)
    {
        if (!requireFiveDifferent || !fallbackToMostCommon)
            return false;

        var entries = sameTierMateria.ToArray();
        return entries.Length > 0 &&
               entries.All(item => item.Materia.Tier == entries[0].Materia.Tier) &&
               entries.Select(item => item.Materia.ItemId).Distinct().Take(TradeSize).Count() < TradeSize;
    }

    public static IReadOnlyList<MateriaBatchItem> CreateFallbackPlan(IEnumerable<MateriaInventoryEntry> sameTierMateria)
    {
        var materia = sameTierMateria
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Materia.ItemId)
            .ToArray();
        if (materia.Length == 0 ||
            materia.Length >= TradeSize ||
            materia.Any(item => item.Materia.Tier != materia[0].Materia.Tier) ||
            materia.Sum(item => (long)item.Count) < TradeSize)
            return [];

        var remainingCopies = TradeSize - materia.Length;
        var plan = materia.Select(item => new MateriaBatchItem(item.Materia.ItemId, 1)).ToArray();
        for (var index = 0; index < materia.Length && remainingCopies > 0; index++)
        {
            var availableCopies = materia[index].Count - 1;
            var extraCopies = (int)Math.Min(availableCopies, (uint)remainingCopies);
            plan[index] = plan[index] with { Quantity = plan[index].Quantity + extraCopies };
            remainingCopies -= extraCopies;
        }

        return remainingCopies == 0 ? plan : [];
    }

    public static bool IsValidDifferentMateriaSelection(
        IEnumerable<MateriaInventoryEntry> sameTierEligibleMateria,
        IReadOnlyDictionary<uint, int> selectedQuantities,
        bool fallbackToMostCommon)
    {
        var eligible = sameTierEligibleMateria.ToArray();
        var totalSelected = selectedQuantities.Values.Sum();
        if (eligible.Length == 0 ||
            eligible.Any(item => item.Materia.Tier != eligible[0].Materia.Tier) ||
            totalSelected != TradeSize ||
            selectedQuantities.Count == 0 ||
            selectedQuantities.Any(pair => pair.Value <= 0) ||
            !selectedQuantities.Keys.All(itemId => eligible.Any(item => item.Materia.ItemId == itemId)))
            return false;

        var distinctTypeCount = eligible.Select(item => item.Materia.ItemId).Distinct().Take(TradeSize).Count();
        if (distinctTypeCount >= TradeSize)
            return selectedQuantities.Count == TradeSize && selectedQuantities.Values.All(quantity => quantity == 1);

        if (!fallbackToMostCommon)
            return false;

        var fallbackPlan = CreateFallbackPlan(eligible).ToDictionary(item => item.ItemId, item => item.Quantity);
        return fallbackPlan.Count == selectedQuantities.Count &&
               fallbackPlan.All(pair => selectedQuantities.GetValueOrDefault(pair.Key) == pair.Value);
    }

    public static uint? GetNextPlannedItemId(
        IReadOnlyList<MateriaBatchItem> plan,
        IReadOnlyDictionary<uint, int> selectedQuantities)
    {
        var nextUnselectedType = plan.FirstOrDefault(item =>
            item.Quantity > 0 && selectedQuantities.GetValueOrDefault(item.ItemId) == 0);
        if (nextUnselectedType.ItemId != 0)
            return nextUnselectedType.ItemId;

        var remainingType = plan.FirstOrDefault(item =>
            selectedQuantities.GetValueOrDefault(item.ItemId) < item.Quantity);
        return remainingType.ItemId == 0 ? null : remainingType.ItemId;
    }
}
