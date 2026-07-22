using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.Core.Scheduling;
using Snowcloak.Game.Scheduling;
using Snowcloak.Interop.Ipc;
using System.Numerics;

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
            return DrawIntro();

        ImGui.TextUnformatted("Mandatory Plugins:");
        ImGui.SameLine();

        ImGui.TextUnformatted("Penumbra");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_penumbraExists, inline: false);
        ImGui.SameLine();
        ElezenImgui.AttachTooltip($"Penumbra is {(_penumbraExists ? "available and up to date." : "unavailable or not up to date.")}");

        ImGui.TextUnformatted("Glamourer");
        ImGui.SameLine();
        ElezenImgui.GetBooleanIcon(_glamourerExists, inline: false);
        ElezenImgui.AttachTooltip($"Glamourer is {(_glamourerExists ? "available and up to date." : "unavailable or not up to date.")}");

        ImGui.TextUnformatted("Optional Addons:");
        ImGui.SameLine();

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

    private bool DrawIntro()
    {
        using (_fontService.UidFont.Push())
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "REQUIRED PLUGINS");

        if (ImGui.BeginTable("##requiredPlugins", 2,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableNextColumn();
            DrawRequiredPluginCard("Penumbra", _penumbraExists, _ipcManager.Penumbra.Status.Version);
            ImGui.TableNextColumn();
            DrawRequiredPluginCard("Glamourer", _glamourerExists, _ipcManager.Glamourer.Status.Version);
            ImGui.EndTable();
        }

        ImGuiHelpers.ScaledDummy(7);
        using (_fontService.UidFont.Push())
            ImGui.TextColored(ImGuiColors.DalamudGrey, "OPTIONAL COMPATIBILITY PLUGINS");
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            "These plugins are commonly used and provide extra options to allow characters to be seen as intended. Installing them is highly recommended!");

        if (ImGui.BeginTable("##optionalPlugins", 3,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX))
        {
            DrawOptionalPluginCell("SimpleHeels", _heelsExists);
            DrawOptionalPluginCell("Customize+", _customizePlusExists);
            DrawOptionalPluginCell("Honorific", _honorificExists);
            DrawOptionalPluginCell("PetNicknames", _petNamesExists);
            DrawOptionalPluginCell("Moodles", _moodlesExists);
            DrawOptionalPluginCell("Brio", _brioExists);
            ImGui.EndTable();
        }

        var ready = _penumbraExists && _glamourerExists;
        if (!ready)
        {
            ImGuiHelpers.ScaledDummy(4);
            CharaDataHubCard.Error("Penumbra and Glamourer must both be available and up to date before setup can continue.");
        }

        return ready;
    }

    private static void DrawRequiredPluginCard(string name, bool available, string? version)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 58f * scale);
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var accent = available ? SnowcloakColours.OnlineBlue : ImGuiColors.DalamudRed;

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(SnowcloakColours.CompactPanelAlt), 4f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(SnowcloakColours.CompactBorderSubtle),
            4f * scale, ImDrawFlags.None, scale);
        drawList.AddLine(min, min with { Y = max.Y }, Colour.Vector4ToColour(accent), 2f * scale);

        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = (available ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle).ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(new Vector2(min.X + 12f * scale, min.Y + (size.Y - iconSize.Y) * 0.5f),
            Colour.Vector4ToColour(accent), iconText);
        ImGui.PopFont();

        var textX = min.X + 12f * scale + iconSize.X + 10f * scale;
        drawList.AddText(new Vector2(textX, min.Y + 10f * scale), Colour.Vector4ToColour(Vector4.One), name);
        drawList.AddText(new Vector2(textX, min.Y + 31f * scale),
            Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted),
            available
                ? $"Detected {version ?? "version unknown"}"
                : "Missing or outdated");
        ImGui.Dummy(size);
    }

    private static void DrawOptionalPluginCell(string name, bool available)
    {
        ImGui.TableNextColumn();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(available ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted,
            (available ? FontAwesomeIcon.Check : FontAwesomeIcon.Minus).ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(available ? Vector4.One : SnowcloakColours.CompactTextMuted, name);
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
