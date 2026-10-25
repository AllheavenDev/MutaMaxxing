using MutaMaxxing.Core;

namespace MutaMaxxing.Core.Tests;

[TestClass]
public sealed class MateriaEligibilityPolicyTests
{
    [TestMethod]
    public void BlacklistedItemIsNeverEligibleInTradeAllMode()
    {
        var materia = CreateMateria(1001, 9, MateriaCategory.Battle);
        var policy = CreatePolicy(
            tradeAllExceptBlacklist: true,
            blacklistMateriaIds: [materia.ItemId]);

        Assert.IsFalse(policy.IsEligible(materia));
    }

    [TestMethod]
    public void BlacklistCategoryAndTierOverrideWhitelist()
    {
        var materia = CreateMateria(1001, 9, MateriaCategory.Gatherer);
        var policy = CreatePolicy(
            whitelistRules: [new MateriaRule { Category = MateriaCategory.Gatherer, Tier = 9 }],
            blacklistRules: [new MateriaRule { Category = MateriaCategory.Gatherer, Tier = 9 }]);

        Assert.IsFalse(policy.IsEligible(materia));
    }

    [TestMethod]
    public void WhitelistMatchesExactItemOrCategoryAndTier()
    {
        var exactItem = CreateMateria(1001, 7, MateriaCategory.Battle);
        var categoryAndTier = CreateMateria(1002, 9, MateriaCategory.Gatherer);
        var policy = CreatePolicy(
            whitelistMateriaIds: [exactItem.ItemId],
            whitelistRules: [new MateriaRule { Category = MateriaCategory.Gatherer, Tier = 9 }]);

        Assert.IsTrue(policy.IsEligible(exactItem));
        Assert.IsTrue(policy.IsEligible(categoryAndTier));
    }

    [TestMethod]
    public void TierZeroWhitelistRuleIncludesFutureMateriaGrades()
    {
        var futureMateria = CreateMateria(1001, 13, MateriaCategory.Crafter);
        var policy = CreatePolicy(
            whitelistRules: [new MateriaRule { Category = MateriaCategory.Crafter, Tier = 0 }]);

        Assert.IsTrue(policy.IsEligible(futureMateria));
    }

    [TestMethod]
    public void WhitelistRuleDoesNotMatchAnotherCategoryOrTier()
    {
        var wrongCategory = CreateMateria(1001, 9, MateriaCategory.Battle);
        var wrongTier = CreateMateria(1002, 10, MateriaCategory.Gatherer);
        var policy = CreatePolicy(
            whitelistRules: [new MateriaRule { Category = MateriaCategory.Gatherer, Tier = 9 }]);

        Assert.IsFalse(policy.IsEligible(wrongCategory));
        Assert.IsFalse(policy.IsEligible(wrongTier));
    }

    private static MateriaEligibilityPolicy CreatePolicy(
        bool tradeAllExceptBlacklist = false,
        HashSet<uint>? whitelistMateriaIds = null,
        List<MateriaRule>? whitelistRules = null,
        HashSet<uint>? blacklistMateriaIds = null,
        List<MateriaRule>? blacklistRules = null) =>
        new(
            tradeAllExceptBlacklist,
            whitelistMateriaIds ?? [],
            whitelistRules ?? [],
            blacklistMateriaIds ?? [],
            blacklistRules ?? []);

    private static MateriaDefinition CreateMateria(uint itemId, int tier, MateriaCategory category) => new()
    {
        ItemId = itemId,
        Name = $"Materia {itemId}",
        BaseParamName = category.ToString(),
        Category = category,
        Tier = tier,
    };
}
