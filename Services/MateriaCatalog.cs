using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace MutaMaxxing.Services;

public sealed class MateriaDefinition
{
    public required uint ItemId { get; init; }

    public required string Name { get; init; }

    public required string BaseParamName { get; init; }

    public required MateriaCategory Category { get; init; }

    public required int Tier { get; init; }
}

public sealed class MateriaCatalog
{
    private static readonly HashSet<string> CrafterParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "Craftsmanship",
        "Control",
        "CP",
        "Crafting Points",
    };

    private static readonly HashSet<string> GathererParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gathering",
        "Perception",
        "GP",
        "Gathering Points",
    };

    private static readonly HashSet<string> BattleParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "Strength",
        "Dexterity",
        "Vitality",
        "Intelligence",
        "Mind",
        "Piety",
        "Critical Hit",
        "Determination",
        "Direct Hit Rate",
        "Skill Speed",
        "Spell Speed",
        "Tenacity",
    };

    private readonly Dictionary<uint, MateriaDefinition> byItemId;

    public MateriaCatalog(IDataManager dataManager)
    {
        var materiaSheet = dataManager.GetExcelSheet<Materia>();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        var englishBaseParams = dataManager.GetExcelSheet<BaseParam>(ClientLanguage.English);
        var definitions = new List<MateriaDefinition>();

        foreach (var materia in materiaSheet)
        {
            var baseParamId = materia.BaseParam.RowId;
            if (baseParamId == 0)
                continue;

            var baseParam = englishBaseParams.GetRowOrDefault(baseParamId);
            var baseParamName = baseParam?.Name.ExtractText() ?? string.Empty;
            var category = GetCategory(baseParamName);

            // Materia.Item is a grade-indexed array in Lumina. Reading the available
            // rows here keeps the catalog current when the game adds another grade.
            for (var index = 0; index < materia.Item.Count; index++)
            {
                var itemId = materia.Item[index].RowId;
                if (itemId == 0)
                    continue;

                var item = itemSheet.GetRowOrDefault(itemId);
                var name = item?.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                definitions.Add(new MateriaDefinition
                {
                    ItemId = itemId,
                    Name = name,
                    BaseParamName = baseParamName,
                    Category = category,
                    Tier = index + 1,
                });
            }
        }

        Entries = definitions.OrderBy(x => x.Category).ThenBy(x => x.Tier).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
        byItemId = Entries.ToDictionary(x => x.ItemId);
        AvailableTiers = Entries.Select(x => x.Tier).Distinct().Order().ToArray();
    }

    public IReadOnlyList<MateriaDefinition> Entries { get; }

    public IReadOnlyList<int> AvailableTiers { get; }

    public bool TryGet(uint itemId, out MateriaDefinition definition) => byItemId.TryGetValue(itemId, out definition!);

    private static MateriaCategory GetCategory(string baseParamName)
    {
        if (CrafterParams.Contains(baseParamName))
            return MateriaCategory.Crafter;

        if (GathererParams.Contains(baseParamName))
            return MateriaCategory.Gatherer;

        return BattleParams.Contains(baseParamName) ? MateriaCategory.Battle : MateriaCategory.Other;
    }
}
