using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MutaMaxxing.Services;

// Structural copy of TextAdvance's ExternalTerritoryConfig IPC contract.
// Null fields preserve the user's setting; true fields request the skip behavior
// only while MutaMaxxing owns external control.
public sealed class TextAdvanceExternalConfig
{
    public bool? EnableCutsceneEsc = true;
    public bool? EnableCutsceneSkipConfirm = true;
    public bool? EnableTalkSkip = true;
}

public sealed class TextAdvanceIpc
{
    private const string RequesterName = "MutaMaxxing";

    private readonly ICallGateSubscriber<bool> isEnabled;
    private readonly ICallGateSubscriber<string, TextAdvanceExternalConfig, bool> enableExternalControl;
    private readonly ICallGateSubscriber<string, bool> disableExternalControl;
    private bool ownsControl;

    public TextAdvanceIpc(IDalamudPluginInterface pluginInterface)
    {
        isEnabled = pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsEnabled");
        enableExternalControl = pluginInterface.GetIpcSubscriber<string, TextAdvanceExternalConfig, bool>("TextAdvance.EnableExternalControl");
        disableExternalControl = pluginInterface.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");
    }

    public bool IsAvailableAndEnabled()
    {
        try
        {
            return isEnabled.HasFunction && isEnabled.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public bool TryAcquire()
    {
        if (!IsAvailableAndEnabled())
            return false;

        try
        {
            ownsControl = enableExternalControl.HasFunction && enableExternalControl.InvokeFunc(RequesterName, new TextAdvanceExternalConfig());
            return ownsControl;
        }
        catch
        {
            ownsControl = false;
            return false;
        }
    }

    public void Release()
    {
        if (!ownsControl)
            return;

        try
        {
            if (disableExternalControl.HasFunction)
                disableExternalControl.InvokeFunc(RequesterName);
        }
        catch
        {
            // TextAdvance may have been unloaded during a run; the IPC owner is gone.
        }
        finally
        {
            ownsControl = false;
        }
    }
}
