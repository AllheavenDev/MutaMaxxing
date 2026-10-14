using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace MutaMaxxing.Services;

public sealed class MutamixRouteController
{
    private const string CentralThanalanName = "Central Thanalan";
    private const float MutamixMapX = 23.7f;
    private const float MutamixMapY = 13.6f;
    private const float DestinationFloorSearchRadius = 5f;
    private const float ArrivalDistance = 7f;
    private static readonly TimeSpan TerritoryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NavmeshTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PathTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PathProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DependencyProbeInterval = TimeSpan.FromSeconds(1);

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
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
    private bool pathWasObserved;
    private Vector3 destination;
    private bool? vnavmeshReady;
    private bool lifestreamAvailable;
    private string status = "Ready to route to Mutamix.";

    public MutamixRouteController(
        IDataManager dataManager,
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;

        var territorySheet = dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        centralThanalanTerritoryId = territorySheet
            .FirstOrDefault(row => row.PlaceName.ValueNullable?.Name.ExtractText() == CentralThanalanName)
            .RowId;
        centralThanalanAetheryteId = dataManager.GetExcelSheet<Aetheryte>()
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
            if (!clientState.IsLoggedIn)
                return "Log in before starting a route.";
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
                BeginWalk(destination);
                break;

            case RouteState.Walking:
                UpdateWalking(now);
                break;
        }
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
            Stop("Route complete. Open Mutamix's Materia Transmutation window when you're ready.");
            return;
        }

        if (pathWasObserved)
            Stop("vnavmesh stopped before reaching Mutamix. Check the route and try again.");
        else if (now - stateStartedAt > TimeSpan.FromSeconds(8))
            Stop("vnavmesh accepted the route but did not start moving.", stopNavigation: true);
    }

    private bool BeginWalk(Vector3 target)
    {
        try
        {
            if (!pathfindAndMoveTo.InvokeFunc(target, false))
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

        SetState(RouteState.Walking, "vnavmesh is walking to Mutamix.");
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
                gameObject.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc &&
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
        Walking,
    }
}
