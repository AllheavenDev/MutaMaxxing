using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Numerics;
using GameActionType = FFXIVClientStructs.FFXIV.Client.Game.ActionType;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using LuminaAetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace MutaMaxxing.Services;

public sealed unsafe class MutamixRouteController
{
    private const string CentralThanalanName = "Central Thanalan";
    private const float MutamixMapX = 23.7f;
    private const float MutamixMapY = 13.6f;
    private const float DestinationFloorSearchRadius = 5f;
    private const float ArrivalDistance = 4f;
    private const float InteractionDistance = 5f;
    private const uint MountRouletteGeneralAction = 9;
    private const uint DismountGeneralAction = 23;
    private static readonly TimeSpan TerritoryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NavmeshTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PathTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DialogueTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TradeWindowTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PathProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DependencyProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PostDismountDelay = TimeSpan.FromMilliseconds(750);

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly TransmutationController transmutationController;
    private readonly IPluginLog log;
    private readonly uint centralThanalanTerritoryId;
    private readonly uint centralThanalanAetheryteId;
    private readonly Vector2? mutamixMapPosition;
    private readonly ICallGateSubscriber<bool> vnavmeshIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> pathfindAndMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> stopPath;
    private readonly ICallGateSubscriber<object> cancelAllPathfinding;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusy;
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleport;
    private readonly ICallGateSubscriber<uint> lifestreamRealTerritory;
    private readonly ICallGateSubscriber<object> lifestreamAbort;

    private RouteState state;
    private DateTime stateStartedAt;
    private DateTime nextPathProbeAt;
    private DateTime nextDependencyProbeAt;
    private DateTime nextActionAt;
    private bool pathWasObserved;
    private int actionAttempts;
    private Vector3 destination;
    private bool? vnavmeshReady;
    private bool lifestreamAvailable;
    private string status = "Ready to route to Mutamix.";

    public MutamixRouteController(
        IDataManager dataManager,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IGameGui gameGui,
        TransmutationController transmutationController,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.gameGui = gameGui;
        this.transmutationController = transmutationController;
        this.log = log;

        var territorySheet = dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        centralThanalanTerritoryId = territorySheet
            .FirstOrDefault(row => row.PlaceName.ValueNullable?.Name.ExtractText() == CentralThanalanName)
            .RowId;
        centralThanalanAetheryteId = dataManager.GetExcelSheet<LuminaAetheryte>()
            .Where(row => row.IsAetheryte && row.Territory.RowId == centralThanalanTerritoryId)
            .Select(row => row.RowId)
            .FirstOrDefault();

        var centralThanalanMap = dataManager.GetExcelSheet<Map>()
            .FirstOrDefault(row => row.TerritoryType.RowId == centralThanalanTerritoryId);
        if (centralThanalanMap.RowId != 0)
        {
            mutamixMapPosition = new Vector2(
                ConvertMapCoordinateToWorld(MutamixMapX, centralThanalanMap.SizeFactor, centralThanalanMap.OffsetX),
                ConvertMapCoordinateToWorld(MutamixMapY, centralThanalanMap.SizeFactor, centralThanalanMap.OffsetY));
        }

        vnavmeshIsReady = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        pathfindAndMoveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        pointOnFloor = pluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        pathfindInProgress = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        stopPath = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        cancelAllPathfinding = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll");
        lifestreamIsBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lifestreamTeleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        lifestreamRealTerritory = pluginInterface.GetIpcSubscriber<uint>("Lifestream.GetRealTerritoryType");
        lifestreamAbort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");

        RefreshDependencies(DateTime.UtcNow);
        if (centralThanalanTerritoryId == 0 || centralThanalanAetheryteId == 0 || mutamixMapPosition is null)
            status = "Could not resolve Mutamix's Central Thanalan destination from game data.";
    }

    public bool IsActive => state != RouteState.Idle;

    public string Status => status;

    public string StartBlockReason
    {
        get
        {
            if (IsActive)
                return "A route is already active.";
            if (transmutationController.IsRunning)
                return "Stop the active transmutation run before routing.";
            if (!clientState.IsLoggedIn)
                return "Log in before starting a route.";
            if (condition[ConditionFlag.RidingPillion])
                return "Leave the passenger seat before starting a route.";
            if (centralThanalanTerritoryId == 0 || centralThanalanAetheryteId == 0 || mutamixMapPosition is null)
                return status;
            if (vnavmeshReady != true)
                return "vnavmesh must be installed, enabled, and ready.";
            if (clientState.TerritoryType != centralThanalanTerritoryId && !lifestreamAvailable)
                return "Lifestream must be installed and available to travel to Central Thanalan.";
            return string.Empty;
        }
    }

    public bool CanStart => StartBlockReason.Length == 0;

    public bool Start()
    {
        var blockReason = StartBlockReason;
        if (blockReason.Length > 0)
        {
            status = blockReason;
            return false;
        }

        if (!IsNavigationIdle())
        {
            status = "vnavmesh is already navigating. Stop that route before starting this one.";
            return false;
        }

        stateStartedAt = DateTime.UtcNow;
        if (clientState.TerritoryType == centralThanalanTerritoryId)
        {
            SetState(RouteState.WaitingForVnavmesh, "Preparing the route to Mutamix.");
            return true;
        }

        if (!TryRequestTeleport(out var error))
        {
            status = error;
            return false;
        }

        SetState(RouteState.WaitingForTerritory, "Traveling to Central Thanalan.");
        log.Information("MutaMaxxing route requested Lifestream travel to territory {TerritoryId} via aetheryte {AetheryteId}", centralThanalanTerritoryId, centralThanalanAetheryteId);
        return true;
    }

    public void Update()
    {
        var now = DateTime.UtcNow;
        if (now >= nextDependencyProbeAt)
            RefreshDependencies(now);

        if (!IsActive)
            return;

        try
        {
            UpdateCore(now);
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing route update failed unexpectedly");
            Stop("The route encountered an unexpected error. Check the plugin log.", stopNavigation: true, abortTravel: true);
        }
    }

    public void StopByUser() => Stop("Ready to route to Mutamix.", stopNavigation: state == RouteState.Walking, abortTravel: state == RouteState.WaitingForTerritory);

    public void Dispose() => Stop("Route stopped because MutaMaxxing unloaded.", stopNavigation: state == RouteState.Walking, abortTravel: state == RouteState.WaitingForTerritory);

    private void UpdateCore(DateTime now)
    {
        if (!clientState.IsLoggedIn)
        {
            Stop("Route stopped because the character logged out.", abortTravel: true);
            return;
        }

        switch (state)
        {
            case RouteState.WaitingForTerritory:
                if (now - stateStartedAt > TerritoryTimeout)
                {
                    Stop("Lifestream did not complete the trip to Central Thanalan in time.", abortTravel: true);
                    return;
                }

                if (clientState.TerritoryType != centralThanalanTerritoryId)
                    return;

                if (!TryGetLifestreamArrival(out var arrived) || !arrived)
                {
                    status = "Waiting for Lifestream to finish arriving in Central Thanalan.";
                    return;
                }

                SetState(RouteState.WaitingForVnavmesh, "Arrived in Central Thanalan. Waiting for vnavmesh.");
                stateStartedAt = now;
                break;

            case RouteState.WaitingForVnavmesh:
                if (now - stateStartedAt > NavmeshTimeout)
                {
                    Stop("vnavmesh did not become ready after arrival in Central Thanalan.");
                    return;
                }

                if (clientState.TerritoryType != centralThanalanTerritoryId)
                {
                    Stop("The character left Central Thanalan before the route started.");
                    return;
                }

                if (objectTable.LocalPlayer is null || vnavmeshReady != true)
                {
                    status = "Waiting for the character and vnavmesh to be ready.";
                    return;
                }

                if (!IsNavigationIdle())
                {
                    Stop("vnavmesh is already navigating. Stop that route before starting this one.");
                    return;
                }

                if (!TryResolveDestination(out var resolvedDestination, out var destinationSource, out var error))
                {
                    Stop(error);
                    return;
                }

                destination = resolvedDestination;
                log.Information("MutaMaxxing route resolved destination {Destination} from {Source}; territory {TerritoryId}, vnavmesh ready={NavmeshReady}", destination, destinationSource, clientState.TerritoryType, vnavmeshReady);
                BeginMounting(now);
                break;

            case RouteState.Mounting:
                UpdateMounting(now);
                break;

            case RouteState.Walking:
                UpdateWalking(now);
                break;

            case RouteState.Dismounting:
                UpdateDismounting(now);
                break;

            case RouteState.WaitingForDialogue:
                UpdateWaitingForDialogue(now);
                break;

            case RouteState.WaitingForTradeWindow:
                UpdateWaitingForTradeWindow(now);
                break;
        }
    }

    private void BeginMounting(DateTime now)
    {
        SetState(RouteState.Mounting, IsMounted() ? "Waiting for the mount to be ready." : "Using Mount Roulette before flying to Mutamix.");
        stateStartedAt = now;
        nextActionAt = now;
        actionAttempts = 0;
        if (IsMounted() || IsMountTransition())
            nextActionAt = now + ActionRetryInterval;
        else
            TryMount(now);
    }

    private void UpdateMounting(DateTime now)
    {
        if (now - stateStartedAt > MountTimeout)
        {
            Stop("Mount Roulette did not mount you. Check that you can mount here and try again.");
            return;
        }

        if (IsMounted())
        {
            if (IsMountTransition())
                return;

            BeginWalk(destination);
            return;
        }

        if (IsMountTransition() || now < nextActionAt)
            return;

        if (actionAttempts >= 3)
        {
            Stop("Mount Roulette did not mount you. Check that you can mount here and try again.");
            return;
        }

        TryMount(now);
    }

    private void TryMount(DateTime now)
    {
        actionAttempts++;
        if (!TryUseGeneralAction(MountRouletteGeneralAction))
            log.Warning("MutaMaxxing could not use Mount Roulette on attempt {Attempt}", actionAttempts);
        nextActionAt = now + ActionRetryInterval;
    }

    private void UpdateWalking(DateTime now)
    {
        if (now - stateStartedAt > PathTimeout)
        {
            Stop("The vnavmesh route to Mutamix timed out.", stopNavigation: true);
            return;
        }

        if (now < nextPathProbeAt)
            return;

        nextPathProbeAt = now + PathProbeInterval;
        var pathfinding = ReadPathState(pathfindInProgress);
        var pathRunning = ReadPathState(pathIsRunning);
        if (pathfinding is null || pathRunning is null)
        {
            Stop("Could not read vnavmesh route status.", stopNavigation: true);
            return;
        }

        pathWasObserved |= pathfinding.Value || pathRunning.Value;
        if (pathfinding.Value || pathRunning.Value)
            return;

        if (IsWithinArrivalDistance(destination))
        {
            BeginDismounting(now);
            return;
        }

        if (pathWasObserved)
            Stop("vnavmesh stopped before reaching Mutamix. Check the route and try again.");
        else if (now - stateStartedAt > TimeSpan.FromSeconds(8))
            Stop("vnavmesh accepted the route but did not start moving.", stopNavigation: true);
    }

    private void BeginDismounting(DateTime now)
    {
        SetState(RouteState.Dismounting, "Arrived at Mutamix. Dismounting before interaction.");
        stateStartedAt = now;
        nextActionAt = now + PostDismountDelay;
        actionAttempts = 0;
    }

    private void UpdateDismounting(DateTime now)
    {
        if (now - stateStartedAt > MountTimeout)
        {
            Stop("Could not dismount near Mutamix. Dismount manually and start the route again.");
            return;
        }

        if (IsMountTransition())
            return;

        if (IsMounted())
        {
            if (now < nextActionAt)
                return;

            if (actionAttempts >= 3)
            {
                Stop("Could not dismount near Mutamix. Dismount manually and start the route again.");
                return;
            }

            actionAttempts++;
            if (!TryUseGeneralAction(DismountGeneralAction))
                log.Warning("MutaMaxxing could not use Dismount near Mutamix on attempt {Attempt}", actionAttempts);
            nextActionAt = now + ActionRetryInterval;
            return;
        }

        if (now < nextActionAt)
            return;

        BeginInteraction(now);
    }

    private void BeginInteraction(DateTime now)
    {
        var mutamix = FindMutamix();
        if (mutamix is null)
        {
            Stop("Arrived at Mutamix's location, but could not identify him to interact. Open the transmutation window manually.");
            return;
        }

        if (objectTable.LocalPlayer is not { } localPlayer || Vector3.Distance(localPlayer.Position, mutamix.Position) > InteractionDistance)
        {
            Stop("Arrived near Mutamix, but not within interaction range. Move closer and open the transmutation window manually.");
            return;
        }

        try
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
            {
                Stop("The game interaction system is unavailable. Open Mutamix's transmutation window manually.");
                return;
            }

            targetSystem->InteractWithObject((GameObject*)mutamix.Address, true);
            SetState(RouteState.WaitingForDialogue, "Interacting with Mutamix and waiting for the Materia Transmutation option.");
            stateStartedAt = now;
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not interact with Mutamix");
            Stop("Could not interact with Mutamix. Open the transmutation window manually.");
        }
    }

    private void UpdateWaitingForDialogue(DateTime now)
    {
        if (IsAddonVisible("TradeMultiple"))
        {
            Stop("Mutamix's Materia Transmutation window is open. Configure your run and press Start.");
            return;
        }

        if (now - stateStartedAt > DialogueTimeout)
        {
            Stop("Mutamix's transmutation option did not appear. Check TextAdvance and open the window manually.");
            return;
        }

        if (!TryGetSelectString(out var addon))
            return;

        var entryCount = addon->PopupMenu.PopupMenu.EntryCount;
        var matchingIndex = -1;
        for (var index = 0; index < entryCount; index++)
        {
            var entryName = addon->PopupMenu.PopupMenu.EntryNames[index].Value;
            if (entryName == null)
                continue;

            var entryText = MemoryHelper.ReadSeStringNullTerminated((nint)entryName).TextValue;
            if (entryText.Contains("Materia", StringComparison.OrdinalIgnoreCase) &&
                entryText.Contains("Transmutation", StringComparison.OrdinalIgnoreCase))
            {
                matchingIndex = index;
                break;
            }
        }

        if (matchingIndex < 0)
        {
            Stop("Mutamix's menu is open, but MutaMaxxing could not identify the Materia Transmutation option. Select it manually.");
            return;
        }

        var callbackValues = stackalloc AtkValue[1];
        callbackValues[0].Type = AtkValueType.Int;
        callbackValues[0].Int = matchingIndex;
        ((AtkUnitBase*)addon)->FireCallback(1, callbackValues, true);

        log.Information("MutaMaxxing selected Mutamix's Materia Transmutation menu entry {EntryIndex}", matchingIndex);
        SetState(RouteState.WaitingForTradeWindow, "Selected Materia Transmutation. Waiting for the transmutation window.");
        stateStartedAt = now;
    }

    private void UpdateWaitingForTradeWindow(DateTime now)
    {
        if (IsAddonVisible("TradeMultiple"))
        {
            Stop("Mutamix's Materia Transmutation window is open. Configure your run and press Start.");
            return;
        }

        if (now - stateStartedAt > TradeWindowTimeout)
            Stop("Selected Materia Transmutation, but the game window did not open. Open it manually and start your run.");
    }

    private bool TryGetSelectString(out AddonSelectString* addon)
    {
        var addonPointer = gameGui.GetAddonByName("SelectString");
        if (addonPointer.Address == 0)
        {
            addon = null;
            return false;
        }

        addon = (AddonSelectString*)addonPointer.Address;
        var baseAddon = (AtkUnitBase*)addon;
        return baseAddon->IsVisible && baseAddon->IsReady &&
               addon->PopupMenu.PopupMenu.EntryCount > 0 && addon->PopupMenu.PopupMenu.EntryNames != null;
    }

    private bool IsAddonVisible(string addonName)
    {
        var addonPointer = gameGui.GetAddonByName(addonName);
        return addonPointer.Address != 0 && ((AtkUnitBase*)addonPointer.Address)->IsVisible;
    }

    private bool BeginWalk(Vector3 target)
    {
        try
        {
            if (!pathfindAndMoveTo.InvokeFunc(target, true))
            {
                Stop("vnavmesh rejected the route to Mutamix.");
                return false;
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not request a vnavmesh route to Mutamix");
            Stop("Could not request a vnavmesh route to Mutamix.");
            return false;
        }

        SetState(RouteState.Walking, "vnavmesh is flying to Mutamix.");
        stateStartedAt = DateTime.UtcNow;
        nextPathProbeAt = stateStartedAt + TimeSpan.FromSeconds(1);
        pathWasObserved = false;
        return true;
    }

    private bool TryResolveDestination(out Vector3 resolvedDestination, out string source, out string error)
    {
        if (FindMutamix() is { } mutamix)
        {
            resolvedDestination = mutamix.Position;
            source = "the nearby Mutamix object";
            error = string.Empty;
            return true;
        }

        if (mutamixMapPosition is not { } mapPosition || objectTable.LocalPlayer is not { } localPlayer)
        {
            resolvedDestination = default;
            source = string.Empty;
            error = "Could not determine Mutamix's route destination.";
            return false;
        }

        var floorProbe = new Vector3(mapPosition.X, localPlayer.Position.Y + 500f, mapPosition.Y);
        try
        {
            var floorPosition = pointOnFloor.InvokeFunc(floorProbe, false, DestinationFloorSearchRadius);
            if (floorPosition is not { } destinationOnFloor)
            {
                resolvedDestination = default;
                source = string.Empty;
                error = "vnavmesh could not find walkable ground at Mutamix's map location.";
                return false;
            }

            resolvedDestination = destinationOnFloor;
            source = $"Mutamix's map position ({MutamixMapX:0.0}, {MutamixMapY:0.0})";
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not resolve Mutamix's map position through vnavmesh");
            resolvedDestination = default;
            source = string.Empty;
            error = "Could not resolve Mutamix's map position. Update vnavmesh and try again.";
            return false;
        }
    }

    private bool TryRequestTeleport(out string error)
    {
        try
        {
            if (!lifestreamIsBusy.HasFunction || !lifestreamTeleport.HasFunction || !lifestreamRealTerritory.HasFunction)
            {
                error = "Lifestream is unavailable. Install and enable it to travel to Central Thanalan.";
                return false;
            }

            if (lifestreamIsBusy.InvokeFunc())
            {
                error = "Lifestream is busy. Wait for its current travel request to finish.";
                return false;
            }

            if (!lifestreamTeleport.InvokeFunc(centralThanalanAetheryteId, 0))
            {
                error = "Lifestream rejected the trip to Black Brush Station.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not request Lifestream travel to Mutamix");
            error = "Could not request travel through Lifestream.";
            return false;
        }
    }

    private bool TryGetLifestreamArrival(out bool arrived)
    {
        try
        {
            if (!lifestreamIsBusy.HasFunction || !lifestreamRealTerritory.HasFunction)
            {
                arrived = false;
                return false;
            }

            arrived = !lifestreamIsBusy.InvokeFunc() && lifestreamRealTerritory.InvokeFunc() == centralThanalanTerritoryId;
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "MutaMaxxing is still waiting for Lifestream arrival confirmation");
            arrived = false;
            return false;
        }
    }

    private IGameObject? FindMutamix()
    {
        foreach (var gameObject in objectTable)
        {
            if (gameObject is not null &&
                gameObject.ObjectKind is DalamudObjectKind.EventNpc or DalamudObjectKind.BattleNpc &&
                gameObject.Name.TextValue.Contains("Mutamix", StringComparison.OrdinalIgnoreCase))
                return gameObject;
        }

        return null;
    }

    private void RefreshDependencies(DateTime now)
    {
        nextDependencyProbeAt = now + DependencyProbeInterval;
        vnavmeshReady = ProbeVnavmesh();
        lifestreamAvailable = ProbeLifestream();
    }

    private bool? ProbeVnavmesh()
    {
        try
        {
            return vnavmeshIsReady.InvokeFunc();
        }
        catch
        {
            return null;
        }
    }

    private bool ProbeLifestream()
    {
        try
        {
            return lifestreamIsBusy.HasFunction && lifestreamTeleport.HasFunction && lifestreamRealTerritory.HasFunction &&
                   !lifestreamIsBusy.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    private bool? ReadPathState(ICallGateSubscriber<bool> subscriber)
    {
        try
        {
            return subscriber.InvokeFunc();
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not read vnavmesh route status");
            return null;
        }
    }

    private bool IsNavigationIdle() => ReadPathState(pathfindInProgress) == false && ReadPathState(pathIsRunning) == false;

    private bool IsMounted() => condition[ConditionFlag.Mounted];

    private bool IsMountTransition() =>
        condition[ConditionFlag.Mounting] ||
        condition[ConditionFlag.Mounting71] ||
        condition[ConditionFlag.MountOrOrnamentTransition];

    private unsafe bool TryUseGeneralAction(uint actionId)
    {
        try
        {
            var actionManager = ActionManager.Instance();
            if (actionManager == null || actionManager->GetActionStatus(GameActionType.GeneralAction, actionId) != 0)
                return false;

            return actionManager->UseAction(GameActionType.GeneralAction, actionId);
        }
        catch (Exception exception)
        {
            log.Error(exception, "MutaMaxxing could not use general action {ActionId}", actionId);
            return false;
        }
    }

    private bool IsWithinArrivalDistance(Vector3 target) => objectTable.LocalPlayer is { } localPlayer &&
        Vector3.Distance(localPlayer.Position, target) <= ArrivalDistance;

    private void SetState(RouteState nextState, string nextStatus)
    {
        if (state != nextState)
            log.Information("MutaMaxxing route state: {PreviousState} -> {NextState}", state, nextState);
        state = nextState;
        status = nextStatus;
    }

    private void Stop(string message, bool stopNavigation = false, bool abortTravel = false)
    {
        var previousState = state;
        state = RouteState.Idle;
        if (stopNavigation)
        {
            try
            {
                if (cancelAllPathfinding.HasFunction)
                    cancelAllPathfinding.InvokeAction();
                if (stopPath.HasFunction)
                    stopPath.InvokeAction();
            }
            catch (Exception exception)
            {
                log.Warning(exception, "MutaMaxxing could not stop the vnavmesh route");
            }
        }

        if (abortTravel)
        {
            try
            {
                if (lifestreamAbort.HasFunction && lifestreamIsBusy.HasFunction && lifestreamIsBusy.InvokeFunc())
                    lifestreamAbort.InvokeAction();
            }
            catch (Exception exception)
            {
                log.Warning(exception, "MutaMaxxing could not abort Lifestream travel");
            }
        }

        status = message;
        if (previousState != RouteState.Idle)
        {
            log.Information("MutaMaxxing route state: {PreviousState} -> Idle ({Reason})", previousState, message);
            log.Information("MutaMaxxing route ended: {Reason}", message);
        }
    }

    private static float ConvertMapCoordinateToWorld(float coordinate, ushort sizeFactor, short offset)
    {
        var trueScale = sizeFactor / 100f;
        var position = (((coordinate - 1f) * trueScale / 41f * 2048f) - 1024f) / trueScale;
        position *= 1000f;
        return ((int)position - (offset * 1000)) / 1000f;
    }

    private enum RouteState
    {
        Idle,
        WaitingForTerritory,
        WaitingForVnavmesh,
        Mounting,
        Walking,
        Dismounting,
        WaitingForDialogue,
        WaitingForTradeWindow,
    }
}
