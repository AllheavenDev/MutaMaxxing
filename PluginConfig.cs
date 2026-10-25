using Dalamud.Configuration;
using MutaMaxxing.Core;

namespace MutaMaxxing;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool TradeAllExceptBlacklist { get; set; }

    public bool RequireFiveDifferentMateria { get; set; }

    public bool FallbackToMostCommonMateria { get; set; }

    public bool RandomizeInterTradeDelay { get; set; }

    public bool ControlTextAdvanceDuringRuns { get; set; } = true;

    public List<MateriaRule> WhitelistRules { get; set; } = [];

    public List<MateriaRule> BlacklistRules { get; set; } = [];

    public HashSet<uint> WhitelistMateriaIds { get; set; } = [];

    public HashSet<uint> BlacklistMateriaIds { get; set; } = [];

    internal MateriaEligibilityPolicy CreateEligibilityPolicy() => new(
        TradeAllExceptBlacklist,
        WhitelistMateriaIds,
        WhitelistRules,
        BlacklistMateriaIds,
        BlacklistRules);
}
