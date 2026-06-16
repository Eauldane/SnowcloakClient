using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop.Ipc;

namespace Snowcloak.UI.Components;

public sealed class PluginAvailabilityPanel : IDisposable
{
    private readonly UiFontService _fontService;
    private readonly IpcManager _ipcManager;
    private readonly IFrameTickHandle _tick;
    private bool _brioExists;
    private bool _customizePlusExists;
    private bool _glamourerExists;
    private bool _heelsExists;
    private bool _honorificExists;
    private bool _moodlesExists;
    private bool _penumbraExists;
    private bool _petNamesExists;

    public PluginAvailabilityPanel(IpcManager ipcManager, UiFontService fontService, IFrameScheduler frameScheduler)
    {
        ArgumentNullException.ThrowIfNull(ipcManager);
        ArgumentNullException.ThrowIfNull(fontService);
        ArgumentNullException.ThrowIfNull(frameScheduler);

        _ipcManager = ipcManager;
        _fontService = fontService;
        _tick = frameScheduler.Register("PluginAvailabilityPanel", TickInterval.EveryMilliseconds(200), TickPriority.Normal,
            RefreshIpcAvailability, FrameGates.Dead, FrameGates.Zoning, FrameGates.Cutscene);
    }

    public bool Draw(bool intro = false)
    {
        if (intro)
        {
            ImGui.SetWindowFontScale(0.8f);
            _fontService.BigText("Mandatory Plugins");
            ImGui.SetWindowFontScale(1.0f);
        }
        else
        {
            ImGui.TextUnformatted("Mandatory Plugins:");
            ImGui.SameLine();
        }

        ImGui.TextUnformatted("Penumbra");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_penumbraExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Penumbra is {(_penumbraExists ? "available and up to date." : "unavailable or not up to date.")}");

        ImGui.TextUnformatted("Glamourer");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_glamourerExists, inline: false);
        ElezenImgui.AttachTooltip($"Glamourer is {(_glamourerExists ? "available and up to date." : "unavailable or not up to date.")}");

        if (intro)
        {
            ImGui.SetWindowFontScale(0.8f);
            _fontService.BigText("Optional Addons");
            ImGui.SetWindowFontScale(1.0f);
            ElezenImgui.WrappedText("These addons are not required for basic operation, but without them you may not see others as intended.");
        }
        else
        {
            ImGui.TextUnformatted("Optional Addons:");
            ImGui.SameLine();
        }

        var alignPos = ImGui.GetCursorPosX();

        ImGui.TextUnformatted("SimpleHeels");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_heelsExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"SimpleHeels is {(_heelsExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        ImGui.SameLine();
        ImGui.TextUnformatted("Customize+");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_customizePlusExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Customize+ is {(_customizePlusExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        ImGui.SameLine();
        ImGui.TextUnformatted("Honorific");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_honorificExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Honorific is {(_honorificExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        ImGui.SameLine();
        ImGui.TextUnformatted("PetNicknames");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_petNamesExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"PetNicknames is {(_petNamesExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        ImGui.SetCursorPosX(alignPos);
        ImGui.TextUnformatted("Moodles");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_moodlesExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Moodles is {(_moodlesExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        ImGui.SameLine();
        ImGui.TextUnformatted("Brio");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_brioExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Brio is {(_brioExists ? "available and up to date." : "unavailable or not up to date.")}");
        ImGui.Spacing();

        if (!_penumbraExists || !_glamourerExists)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "You need Penumbra and Glamourer kept up to date to use Snowcloak.");
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _tick.Dispose();
    }

    private void RefreshIpcAvailability()
    {
        _penumbraExists = _ipcManager.Penumbra.APIAvailable;
        _glamourerExists = _ipcManager.Glamourer.APIAvailable;
        _customizePlusExists = _ipcManager.CustomizePlus.APIAvailable;
        _heelsExists = _ipcManager.Heels.APIAvailable;
        _honorificExists = _ipcManager.Honorific.APIAvailable;
        _petNamesExists = _ipcManager.PetNames.APIAvailable;
        _moodlesExists = _ipcManager.Moodles.APIAvailable;
        _brioExists = _ipcManager.Brio.APIAvailable;
    }
}
