namespace MutaMaxxing.Core;

public sealed class MateriaDefinition
{
    public required uint ItemId { get; init; }

    public required string Name { get; init; }

    public required string BaseParamName { get; init; }

    public required MateriaCategory Category { get; init; }

    public required int Tier { get; init; }
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
