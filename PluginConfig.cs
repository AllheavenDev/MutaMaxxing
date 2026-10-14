using Dalamud.Configuration;

namespace MutaMaxxing;

public sealed class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool TradeAllExceptBlacklist { get; set; }

    public bool RequireFiveDifferentMateria { get; set; }

    public List<MateriaRule> WhitelistRules { get; set; } = [];

    public List<MateriaRule> BlacklistRules { get; set; } = [];

    public HashSet<uint> WhitelistMateriaIds { get; set; } = [];

    public HashSet<uint> BlacklistMateriaIds { get; set; } = [];
}

public sealed class MateriaRule
{
    public MateriaCategory Category { get; set; }

    // Tier zero means every tier, including tiers added by a future game patch.
    public int Tier { get; set; }
}

public enum MateriaCategory
{
    Battle,
    Crafter,
    Gatherer,
    Other,
}
