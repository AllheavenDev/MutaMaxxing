using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MutaMaxxing.Services;
using MutaMaxxing.Windows;

namespace MutaMaxxing;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mutamaxxing";

    private readonly WindowSystem windowSystem = new("MutaMaxxing");
    private readonly MateriaCatalog catalog;
    private readonly PluginConfig config;
    private readonly TransmutationController controller;
    private readonly MutamixRouteController routeController;
    private readonly MainWindow mainWindow;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static ICondition Condition { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        config.WhitelistRules ??= [];
        config.BlacklistRules ??= [];
        config.WhitelistMateriaIds ??= [];
        config.BlacklistMateriaIds ??= [];
        PluginInterface.SavePluginConfig(config);

        catalog = new MateriaCatalog(DataManager);
        var inventoryReader = new InventoryReader();
        var textAdvance = new TextAdvanceIpc(PluginInterface);
        controller = new TransmutationController(ClientState, ObjectTable, Log, catalog, inventoryReader, config, textAdvance);
        routeController = new MutamixRouteController(DataManager, PluginInterface, ClientState, ObjectTable, Condition, GameGui, controller, Log);
        mainWindow = new MainWindow(catalog, config, controller, routeController, () => PluginInterface.SavePluginConfig(config));

        windowSystem.AddWindow(mainWindow);
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        Framework.Update += OnFrameworkUpdate;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "TradeMultiple", OnTradeMultipleLifecycle);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TradeMultiple", OnTradeMultipleLifecycle);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open MutaMaxxing. Use /mutamaxxing stop to stop a run.",
        });
    }

    public void Dispose()
    {
        routeController.Dispose();
        controller.Dispose();
        CommandManager.RemoveHandler(CommandName);
        AddonLifecycle.UnregisterListener(OnTradeMultipleLifecycle);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        mainWindow.Dispose();
        windowSystem.RemoveAllWindows();
    }

    private void OpenWindow() => mainWindow.IsOpen = true;

    private void OnFrameworkUpdate(IFramework _)
    {
        controller.Update();
        routeController.Update();
    }

    private void OnTradeMultipleLifecycle(AddonEvent type, AddonArgs args)
    {
        controller.SetTradeWindowOpen(type == AddonEvent.PostSetup);
    }

    private void OnCommand(string command, string arguments)
    {
        if (string.Equals(arguments.Trim(), "stop", StringComparison.OrdinalIgnoreCase))
        {
            controller.StopByUser();
            mainWindow.IsOpen = true;
            return;
        }

        mainWindow.IsOpen = true;
    }
}
