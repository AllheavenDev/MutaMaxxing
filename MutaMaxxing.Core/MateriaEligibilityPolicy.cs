namespace MutaMaxxing.Core;

public readonly record struct MateriaEligibilityPolicy(
    bool TradeAllExceptBlacklist,
    IReadOnlySet<uint> WhitelistMateriaIds,
    IReadOnlyList<MateriaRule> WhitelistRules,
    IReadOnlySet<uint> BlacklistMateriaIds,
    IReadOnlyList<MateriaRule> BlacklistRules)
{
    public bool IsEligible(MateriaDefinition materia)
    {
        if (BlacklistMateriaIds.Contains(materia.ItemId) || MatchesAny(BlacklistRules, materia))
            return false;

        if (TradeAllExceptBlacklist)
            return true;

        return WhitelistMateriaIds.Contains(materia.ItemId) || MatchesAny(WhitelistRules, materia);
    }

    private static bool MatchesAny(IEnumerable<MateriaRule> rules, MateriaDefinition materia) =>
        rules.Any(rule => rule.Category == materia.Category && (rule.Tier == 0 || rule.Tier == materia.Tier));
}
