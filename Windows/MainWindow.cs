using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MutaMaxxing.Services;

namespace MutaMaxxing.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly MateriaCategory[] Categories = Enum.GetValues<MateriaCategory>();

    private readonly MateriaCatalog catalog;
    private readonly PluginConfig config;
    private readonly TransmutationController controller;
    private readonly Action saveConfiguration;
    private string itemSearch = string.Empty;

    public MainWindow(MateriaCatalog catalog, PluginConfig config, TransmutationController controller, Action saveConfiguration)
        : base("MutaMaxxing###MutaMaxxingMainWindow")
    {
        this.catalog = catalog;
        this.config = config;
        this.controller = controller;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(1000, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Preview and transmute materia from your four player inventory bags. Each trade uses five materia. The game determines the result; these rules control which inventory items may be used.");
        ImGui.Separator();

        DrawRunControls();
        ImGui.Separator();
        DrawMateriaResults();
        ImGui.Separator();
        DrawInventoryPreview();
        ImGui.Separator();
        var rulesLocked = controller.IsRunning;
        if (rulesLocked)
            ImGui.BeginDisabled();
        DrawTradeRules();
        ImGui.Separator();
        DrawMateriaOverrides();
        if (rulesLocked)
            ImGui.EndDisabled();
    }

    public void Dispose()
    {
    }

    private void DrawMateriaResults()
    {
        if (!ImGui.CollapsingHeader("Materia received tracker", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (controller.IsRunning)
            ImGui.TextDisabled($"Live results · {controller.CompletedTransmutations} trades completed this run.");
        else if (controller.HasRunSummary)
            ImGui.TextWrapped(controller.LastRunSummary);
        else
            ImGui.TextDisabled("Received materia and the final run summary will appear here.");

        var rows = new List<(MateriaDefinition Materia, int Count)>();
        foreach (var result in controller.ReceivedMateriaCounts)
        {
            if (catalog.TryGet(result.Key, out var materia))
                rows.Add((materia, result.Value));
        }

        if (rows.Count == 0)
            ImGui.TextDisabled("No materia results recorded yet.");
        else
        {
            ImGui.TextDisabled($"Materia results recorded: {rows.Sum(row => (long)row.Count)} total");
            if (ImGui.BeginTable("received_materia_results", 2,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                    new Vector2(0, 160)))
            {
                ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Times received", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();

                foreach (var row in rows.OrderByDescending(row => row.Count).ThenBy(row => row.Materia.Name, StringComparer.CurrentCulture))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(row.Materia.Name);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(row.Count.ToString());
                }

                ImGui.EndTable();
            }
        }

        if (controller.UnidentifiedResultCount > 0)
            ImGui.TextWrapped($"Could not identify the result from inventory changes for {controller.UnidentifiedResultCount} completed trade(s).");
    }

    private void DrawRunControls()
    {
        ImGui.Text("Run control");
        ImGui.TextDisabled(controller.TradeWindowOpen
            ? "Mutamix's transmutation window is open."
            : "Open Mutamix's Materia Transmutation window before starting.");
        ImGui.SameLine();
        ImGui.TextDisabled(controller.TextAdvanceEnabled ? "TextAdvance ready" : "TextAdvance required");
        if (controller.MutamixDistance is { } mutamixDistance)
        {
            ImGui.TextDisabled(controller.MutamixNearby
                ? $"Mutamix nearby ({mutamixDistance:0.0} yalms)."
                : $"Mutamix is {mutamixDistance:0.0} yalms away; move within {TransmutationController.MutamixInteractionRange:0} yalms to start.");
        }
        else
        {
            ImGui.TextDisabled($"Mutamix not found nearby; stand within {TransmutationController.MutamixInteractionRange:0} yalms to start.");
        }

        if (controller.IsRunning)
        {
            if (ImGui.Button("Stop run"))
                controller.StopByUser();
        }
        else
        {
            var canStart = controller.CanStart(out var startBlockReason);
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start run"))
                controller.Start();
            ImGui.EndDisabled();
            if (!canStart)
                ImGui.TextDisabled(startBlockReason);
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{controller.CompletedTransmutations} trades completed this run");
        ImGui.TextWrapped(controller.Status);
        ImGui.TextDisabled("Stop prevents another trade. A trade already submitted to the game may still finish.");
    }

    private void DrawInventoryPreview()
    {
        ImGui.Text("Eligible inventory preview");
        if (!controller.Inventory.IsReady)
        {
            ImGui.TextDisabled(controller.Inventory.Message);
            return;
        }

        var eligible = controller.EligibleMateria;
        ImGui.Text($"Eligible materia: {controller.EligibleItemCount}  |  Grades with a complete batch: {controller.EligibleTierCount}");
        if (eligible.Count == 0)
        {
            ImGui.TextDisabled("No inventory materia currently match these rules.");
            return;
        }

        if (!ImGui.BeginTable("eligible_materia_preview", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new Vector2(0, 180)))
            return;

        ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthFixed, 95);
        ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableHeadersRow();

        foreach (var row in eligible)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(row.Materia.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text(CategoryName(row.Materia.Category));
            ImGui.TableSetColumnIndex(2);
            ImGui.Text(ToRoman(row.Materia.Tier));
            ImGui.TableSetColumnIndex(3);
            ImGui.Text(row.Count.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawTradeRules()
    {
        ImGui.Text("Trade rules");
        var tradeAll = config.TradeAllExceptBlacklist;
        if (ImGui.Checkbox("Trade all materia except blacklisted rules", ref tradeAll))
        {
            config.TradeAllExceptBlacklist = tradeAll;
            SaveConfiguration();
        }

        ImGui.TextDisabled(tradeAll
            ? "The whitelist is ignored. Every materia in the inventory is eligible unless a blacklist rule excludes it."
            : "Only whitelist matches are eligible. A blacklist match always wins.");

        var requireFiveDifferent = config.RequireFiveDifferentMateria;
        if (ImGui.Checkbox("Require five different materia of one grade", ref requireFiveDifferent))
        {
            config.RequireFiveDifferentMateria = requireFiveDifferent;
            SaveConfiguration();
        }
        ImGui.TextDisabled("Every trade uses one grade only. When enabled, all five inputs must also be different materia types.");

        var randomizeInterTradeDelay = config.RandomizeInterTradeDelay;
        if (ImGui.Checkbox("Add randomized delay between trades (50–70 ms)", ref randomizeInterTradeDelay))
        {
            config.RandomizeInterTradeDelay = randomizeInterTradeDelay;
            SaveConfiguration();
        }
        ImGui.TextDisabled("Adds a random 50–70 ms pause after each completed trade (average 60 ms).");

        if (!tradeAll)
        {
            if (DrawRuleMatrix("Whitelist category and grade", "whitelist", config.WhitelistRules))
                SaveConfiguration();
        }

        if (ImGui.CollapsingHeader("Blacklist category and grade", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Blacklisted matches are never selected, even if they also match the whitelist.");
            if (DrawRuleMatrix("Blacklist", "blacklist", config.BlacklistRules))
                SaveConfiguration();
        }
    }

    private void DrawMateriaOverrides()
    {
        if (!ImGui.CollapsingHeader("Specific materia overrides", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled("Whitelist item checks apply only in whitelist mode. Blacklist item checks always apply and take priority.");
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##materia_search", "Search materia name or stat", ref itemSearch, 128);

        if (!ImGui.BeginChild("materia_overrides_list", new Vector2(0, 280), true))
        {
            ImGui.EndChild();
            return;
        }

        if (ImGui.BeginTable("materia_overrides_table", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthFixed, 95);
            ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, 55);
            ImGui.TableSetupColumn("Whitelist", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Blacklist", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableHeadersRow();

            var counts = controller.Inventory.IsReady
                ? controller.Inventory.CountByItemId()
                : new Dictionary<uint, uint>();
            foreach (var materia in catalog.Entries)
            {
                if (itemSearch.Length > 0 &&
                    !materia.Name.Contains(itemSearch, StringComparison.CurrentCultureIgnoreCase) &&
                    !materia.BaseParamName.Contains(itemSearch, StringComparison.CurrentCultureIgnoreCase))
                    continue;

                ImGui.PushID((int)materia.ItemId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(materia.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(CategoryName(materia.Category));
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(ToRoman(materia.Tier));
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(counts.GetValueOrDefault(materia.ItemId).ToString());

                ImGui.TableSetColumnIndex(4);
                var isWhitelisted = config.WhitelistMateriaIds.Contains(materia.ItemId);
                if (ImGui.Checkbox("##whitelist", ref isWhitelisted))
                {
                    if (isWhitelisted)
                        config.WhitelistMateriaIds.Add(materia.ItemId);
                    else
                        config.WhitelistMateriaIds.Remove(materia.ItemId);
                    SaveConfiguration();
                }

                ImGui.TableSetColumnIndex(5);
                var isBlacklisted = config.BlacklistMateriaIds.Contains(materia.ItemId);
                if (ImGui.Checkbox("##blacklist", ref isBlacklisted))
                {
                    if (isBlacklisted)
                        config.BlacklistMateriaIds.Add(materia.ItemId);
                    else
                        config.BlacklistMateriaIds.Remove(materia.ItemId);
                    SaveConfiguration();
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private bool DrawRuleMatrix(string label, string id, List<MateriaRule> rules)
    {
        var changed = false;
        ImGui.Text(label);
        ImGui.TextDisabled("Any tier also includes grades added by future game patches.");

        var tiers = catalog.AvailableTiers;
        if (!ImGui.BeginTable($"{id}_rule_matrix", tiers.Count + 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return false;

        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Any tier", ImGuiTableColumnFlags.WidthFixed, 62);
        foreach (var tier in tiers)
            ImGui.TableSetupColumn(ToRoman(tier), ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled("All groups");
        ImGui.TableSetColumnIndex(1);
        var allAny = Categories.All(category => HasRule(rules, category, 0));
        if (ImGui.Checkbox($"##{id}_all_any", ref allAny))
        {
            foreach (var category in Categories)
                SetRule(rules, category, 0, allAny);
            changed = true;
        }

        for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
        {
            var tier = tiers[tierIndex];
            ImGui.TableSetColumnIndex(tierIndex + 2);
            var allGroups = Categories.All(category => HasRule(rules, category, tier));
            if (ImGui.Checkbox($"##{id}_all_{tier}", ref allGroups))
            {
                foreach (var category in Categories)
                    SetRule(rules, category, tier, allGroups);
                changed = true;
            }
        }

        foreach (var category in Categories)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(CategoryName(category));

            ImGui.TableSetColumnIndex(1);
            var anyTier = HasRule(rules, category, 0);
            if (ImGui.Checkbox($"##{id}_{category}_any", ref anyTier))
            {
                SetRule(rules, category, 0, anyTier);
                changed = true;
            }

            for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
            {
                var tier = tiers[tierIndex];
                ImGui.TableSetColumnIndex(tierIndex + 2);
                var selected = HasRule(rules, category, tier);
                if (ImGui.Checkbox($"##{id}_{category}_{tier}", ref selected))
                {
                    SetRule(rules, category, tier, selected);
                    changed = true;
                }
            }
        }

        ImGui.EndTable();
        return changed;
    }

    private static bool HasRule(IEnumerable<MateriaRule> rules, MateriaCategory category, int tier) =>
        rules.Any(rule => rule.Category == category && rule.Tier == tier);

    private void SaveConfiguration()
    {
        saveConfiguration();
        controller.RefreshEligibility();
    }

    private static void SetRule(List<MateriaRule> rules, MateriaCategory category, int tier, bool selected)
    {
        rules.RemoveAll(rule => rule.Category == category && rule.Tier == tier);
        if (selected)
            rules.Add(new MateriaRule { Category = category, Tier = tier });
    }

    private static string CategoryName(MateriaCategory category) => category switch
    {
        MateriaCategory.Battle => "Battle",
        MateriaCategory.Crafter => "Crafter",
        MateriaCategory.Gatherer => "Gatherer",
        _ => "Other",
    };

    private static string ToRoman(int number)
    {
        if (number <= 0)
            return number.ToString();

        ReadOnlySpan<(int Value, string Numeral)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];

        var result = new System.Text.StringBuilder();
        foreach (var pair in numerals)
        {
            while (number >= pair.Value)
            {
                result.Append(pair.Numeral);
                number -= pair.Value;
            }
        }

        return result.ToString();
    }
}
