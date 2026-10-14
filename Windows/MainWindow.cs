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
    private readonly MutamixRouteController routeController;
    private readonly Action saveConfiguration;
    private string itemSearch = string.Empty;
    private bool onlyShowMateriaInBags = true;
    private int overrideSortColumn = 3;
    private ImGuiSortDirection overrideSortDirection = ImGuiSortDirection.Descending;

    public MainWindow(MateriaCatalog catalog, PluginConfig config, TransmutationController controller, MutamixRouteController routeController, Action saveConfiguration)
        : base("MutaMaxxing###MutaMaxxingMainWindow")
    {
        this.catalog = catalog;
        this.config = config;
        this.controller = controller;
        this.routeController = routeController;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(1000, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("mutamaxxing_tabs"))
            return;

        if (ImGui.BeginTabItem("Run"))
        {
            DrawRunControls();
            ImGui.Separator();
            DrawMateriaResults();
            ImGui.Separator();
            DrawInventoryPreview();
            ImGui.EndTabItem();
        }

        var rulesLocked = controller.IsRunning;
        if (ImGui.BeginTabItem("Rules"))
        {
            if (rulesLocked)
                ImGui.BeginDisabled();
            DrawTradeRules();
            if (rulesLocked)
                ImGui.EndDisabled();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Overrides"))
        {
            if (rulesLocked)
                ImGui.BeginDisabled();
            DrawMateriaOverrides();
            if (rulesLocked)
                ImGui.EndDisabled();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
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
        ImGui.TextDisabled(config.ControlTextAdvanceDuringRuns
            ? "TextAdvance is managed during the run and restored afterward."
            : controller.TextAdvanceEnabled ? "TextAdvance is enabled manually." : "Enable TextAdvance manually before starting.");

        var controlTextAdvance = config.ControlTextAdvanceDuringRuns;
        if (controller.IsRunning)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Let MutaMaxxing manage TextAdvance during runs", ref controlTextAdvance))
        {
            config.ControlTextAdvanceDuringRuns = controlTextAdvance;
            SaveConfiguration();
        }
        if (controller.IsRunning)
            ImGui.EndDisabled();

        if (routeController.IsActive)
        {
            if (ImGui.Button("Stop route"))
                routeController.StopByUser();
        }
        else
        {
            var routeBlockReason = routeController.StartBlockReason;
            var canRoute = routeBlockReason.Length == 0;
            ImGui.BeginDisabled(!canRoute);
            if (ImGui.Button("Walk to Mutamix"))
                routeController.Start();
            ImGui.EndDisabled();
            if (!canRoute)
                ImGui.TextWrapped(routeBlockReason);
        }
        if (routeController.IsActive || !string.Equals(routeController.Status, "Ready to route to Mutamix.", StringComparison.Ordinal))
            ImGui.TextWrapped(routeController.Status);

        if (controller.IsRunning)
        {
            if (ImGui.Button("Stop run"))
                controller.StopByUser();
            ImGui.TextDisabled($"{controller.CompletedTransmutations} trades completed this run.");
        }
        else
        {
            var canStart = controller.CanStart(out var startBlockReason);
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button("Start run", new Vector2(-1, 0)))
                controller.Start();
            ImGui.EndDisabled();
            if (!canStart)
                ImGui.TextWrapped(startBlockReason);
        }

        if (controller.IsRunning || !controller.HasRunSummary)
            ImGui.TextWrapped(controller.Status);
        ImGui.TextDisabled("Stop prevents another trade. A trade already submitted to the game may still finish.");
    }

    private void DrawInventoryPreview()
    {
        if (!ImGui.CollapsingHeader("Eligible inventory preview"))
            return;

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

        ImGui.TextWrapped(tradeAll
            ? "Trade all materia in your bags except blacklisted matches."
            : "Only whitelist matches are eligible. Blacklist rules always win.");

        var requireFiveDifferent = config.RequireFiveDifferentMateria;
        if (ImGui.Checkbox("Require five different materia of one grade", ref requireFiveDifferent))
        {
            config.RequireFiveDifferentMateria = requireFiveDifferent;
            SaveConfiguration();
        }
        ImGui.TextWrapped("Trades never mix grades. Prefer five different types, or require five when fallback is off.");

        if (requireFiveDifferent)
        {
            ImGui.Indent();
            var fallbackToMostCommon = config.FallbackToMostCommonMateria;
            if (ImGui.Checkbox("Fallback to the most common type when needed", ref fallbackToMostCommon))
            {
                config.FallbackToMostCommonMateria = fallbackToMostCommon;
                SaveConfiguration();
            }
            ImGui.TextWrapped("When five types are unavailable, use each eligible type once, then add copies from the most common type first.");
            ImGui.Unindent();
        }

        var randomizeInterTradeDelay = config.RandomizeInterTradeDelay;
        if (ImGui.Checkbox("Add randomized delay between trades (50–70 ms)", ref randomizeInterTradeDelay))
        {
            config.RandomizeInterTradeDelay = randomizeInterTradeDelay;
            SaveConfiguration();
        }
        ImGui.TextWrapped("Waits a random 50–70 ms after each completed trade.");

        if (!tradeAll)
        {
            if (DrawRuleMatrix("Whitelist category and grade", "whitelist", config.WhitelistRules))
                SaveConfiguration();
        }

        if (ImGui.CollapsingHeader("Blacklist category and grade"))
        {
            ImGui.TextWrapped("Blacklisted matches are never selected, even when whitelisted.");
            if (DrawRuleMatrix("Blacklist", "blacklist", config.BlacklistRules))
                SaveConfiguration();
        }
    }

    private void DrawMateriaOverrides()
    {
        ImGui.Text("Specific materia overrides");
        ImGui.TextWrapped("Item whitelist checks apply in whitelist mode. Blacklist checks always take priority.");
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##materia_search", "Search materia name or stat", ref itemSearch, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Only show materia in bags", ref onlyShowMateriaInBags);

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("materia_overrides_table", 6, tableFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Materia", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("Grade", ImGuiTableColumnFlags.WidthFixed, 58);
            ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 75);
            ImGui.TableSetupColumn("Whitelist", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 75);
            ImGui.TableSetupColumn("Blacklist", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 75);
            ImGui.TableHeadersRow();

            var sortSpecs = ImGui.TableGetSortSpecs();
            if (sortSpecs.SpecsDirty && sortSpecs.SpecsCount > 0)
            {
                overrideSortColumn = sortSpecs.Specs[0].ColumnIndex;
                overrideSortDirection = sortSpecs.Specs[0].SortDirection;
                sortSpecs.SpecsDirty = false;
            }

            var counts = controller.Inventory.IsReady
                ? controller.Inventory.CountByItemId()
                : new Dictionary<uint, uint>();
            var rows = catalog.Entries
                .Where(materia => itemSearch.Length == 0 ||
                                  materia.Name.Contains(itemSearch, StringComparison.CurrentCultureIgnoreCase) ||
                                  materia.BaseParamName.Contains(itemSearch, StringComparison.CurrentCultureIgnoreCase))
                .Select(materia => (Materia: materia, Count: counts.GetValueOrDefault(materia.ItemId)))
                .Where(row => !onlyShowMateriaInBags || row.Count > 0)
                .ToList();
            IEnumerable<(MateriaDefinition Materia, uint Count)> sortedRows = overrideSortColumn switch
            {
                0 when overrideSortDirection == ImGuiSortDirection.Descending => rows.OrderByDescending(row => row.Materia.Name, StringComparer.CurrentCultureIgnoreCase),
                1 when overrideSortDirection == ImGuiSortDirection.Descending => rows.OrderByDescending(row => row.Materia.Category),
                2 when overrideSortDirection == ImGuiSortDirection.Descending => rows.OrderByDescending(row => row.Materia.Tier),
                3 when overrideSortDirection == ImGuiSortDirection.Ascending => rows.OrderBy(row => row.Count),
                3 => rows.OrderByDescending(row => row.Count),
                2 => rows.OrderBy(row => row.Materia.Tier),
                1 => rows.OrderBy(row => row.Materia.Category),
                _ => rows.OrderBy(row => row.Materia.Name, StringComparer.CurrentCultureIgnoreCase),
            };

            foreach (var row in sortedRows)
            {
                var materia = row.Materia;
                ImGui.PushID((int)materia.ItemId);
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.Text(materia.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(CategoryName(materia.Category));
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(ToRoman(materia.Tier));
                ImGui.TableSetColumnIndex(3);
                ImGui.Text(row.Count.ToString());

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
