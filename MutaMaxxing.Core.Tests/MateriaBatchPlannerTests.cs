using MutaMaxxing.Core;

namespace MutaMaxxing.Core.Tests;

[TestClass]
public sealed class MateriaBatchPlannerTests
{
    [TestMethod]
    public void BatchTierNeverCombinesMateriaGrades()
    {
        var inventory = new[]
        {
            Entry(1001, 11, 3),
            Entry(1002, 11, 1),
            Entry(1003, 10, 1),
        };

        Assert.IsNull(MateriaBatchPlanner.SelectTier(inventory, requireFiveDifferent: false, fallbackToMostCommon: false));
    }

    [TestMethod]
    public void FiveDifferentModePrefersMoreDistinctMateriaTypes()
    {
        var inventory = new[]
        {
            Entry(1001, 10, 12),
            Entry(1002, 10, 1),
            Entry(1003, 10, 1),
            Entry(1004, 10, 1),
            Entry(2001, 11, 1),
            Entry(2002, 11, 1),
            Entry(2003, 11, 1),
            Entry(2004, 11, 1),
            Entry(2005, 11, 1),
        };

        Assert.AreEqual(11, MateriaBatchPlanner.SelectTier(inventory, requireFiveDifferent: true, fallbackToMostCommon: false));
    }

    [TestMethod]
    public void FallbackRequiresFiveItemsWithinOneGrade()
    {
        var incomplete = new[] { Entry(1001, 11, 2), Entry(1002, 11, 2), Entry(1003, 10, 1) };
        var complete = new[] { Entry(1001, 11, 2), Entry(1002, 11, 3) };

        Assert.IsNull(MateriaBatchPlanner.SelectTier(incomplete, requireFiveDifferent: true, fallbackToMostCommon: false));
        Assert.IsNull(MateriaBatchPlanner.SelectTier(incomplete, requireFiveDifferent: true, fallbackToMostCommon: true));
        Assert.AreEqual(11, MateriaBatchPlanner.SelectTier(complete, requireFiveDifferent: true, fallbackToMostCommon: true));
    }

    [TestMethod]
    public void FallbackAllocatesDuplicatesToMostCommonMateria()
    {
        var inventory = new[] { Entry(1001, 11, 4), Entry(1002, 11, 2), Entry(1003, 11, 1) };

        var plan = MateriaBatchPlanner.CreateFallbackPlan(inventory);

        CollectionAssert.AreEqual(
            new[] { new MateriaBatchItem(1001, 3), new MateriaBatchItem(1002, 1), new MateriaBatchItem(1003, 1) },
            plan.ToArray());
    }

    [TestMethod]
    public void FallbackBreaksEqualInventoryCountsByItemId()
    {
        var inventory = new[] { Entry(1002, 11, 3), Entry(1001, 11, 3) };

        var plan = MateriaBatchPlanner.CreateFallbackPlan(inventory);

        CollectionAssert.AreEqual(
            new[] { new MateriaBatchItem(1001, 3), new MateriaBatchItem(1002, 2) },
            plan.ToArray());
    }

    [TestMethod]
    public void FallbackSelectsEveryAvailableTypeBeforeDuplicateCopies()
    {
        var plan = new[] { new MateriaBatchItem(1001, 3), new MateriaBatchItem(1002, 2) };

        Assert.AreEqual(1001u, MateriaBatchPlanner.GetNextPlannedItemId(plan, new Dictionary<uint, int>()));
        Assert.AreEqual(1002u, MateriaBatchPlanner.GetNextPlannedItemId(plan, new Dictionary<uint, int> { [1001] = 1 }));
        Assert.AreEqual(1001u, MateriaBatchPlanner.GetNextPlannedItemId(plan, new Dictionary<uint, int> { [1001] = 1, [1002] = 1 }));
    }

    [TestMethod]
    public void FallbackSelectionMustMatchTheExactPlanAndGrade()
    {
        var inventory = new[] { Entry(1001, 11, 3), Entry(1002, 11, 2) };
        var valid = new Dictionary<uint, int> { [1001] = 3, [1002] = 2 };
        var wrongQuantity = new Dictionary<uint, int> { [1001] = 4, [1002] = 1 };
        var crossGrade = new[] { Entry(1001, 11, 3), Entry(1002, 10, 2) };

        Assert.IsTrue(MateriaBatchPlanner.IsValidDifferentMateriaSelection(inventory, valid, fallbackToMostCommon: true));
        Assert.IsFalse(MateriaBatchPlanner.IsValidDifferentMateriaSelection(inventory, wrongQuantity, fallbackToMostCommon: true));
        Assert.IsFalse(MateriaBatchPlanner.IsValidDifferentMateriaSelection(crossGrade, valid, fallbackToMostCommon: true));
    }

    [TestMethod]
    public void FiveDifferentSelectionRequiresOneOfEachEligibleType()
    {
        var inventory = Enumerable.Range(1, 5).Select(index => Entry((uint)(1000 + index), 9, 1)).ToArray();
        var valid = Enumerable.Range(1, 5).ToDictionary(index => (uint)(1000 + index), _ => 1);
        var duplicate = new Dictionary<uint, int> { [1001] = 2, [1002] = 1, [1003] = 1, [1004] = 1 };

        Assert.IsTrue(MateriaBatchPlanner.IsValidDifferentMateriaSelection(inventory, valid, fallbackToMostCommon: false));
        Assert.IsFalse(MateriaBatchPlanner.IsValidDifferentMateriaSelection(inventory, duplicate, fallbackToMostCommon: false));
    }

    private static MateriaInventoryEntry Entry(uint itemId, int tier, uint count) => new(
        new MateriaDefinition
        {
            ItemId = itemId,
            Name = $"Materia {itemId}",
            BaseParamName = "Test",
            Category = MateriaCategory.Battle,
            Tier = tier,
        },
        count);
}
