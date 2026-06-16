using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.Configuration;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Text.Json;

namespace Snowcloak.UI.Components;

public sealed class AdvancedSettingsPanel
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    private readonly SnowcloakConfigService _configService;
#if DEBUG
    private readonly IpcTraceRecorder _ipcTraceRecorder;
#endif
    private readonly PairManager _pairManager;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly SnowMediator _mediator;
    private readonly UiFontService _fontService;
    private readonly Dictionary<string, object> _selectedComboItems = new(StringComparer.Ordinal);

    public AdvancedSettingsPanel(
        SnowcloakConfigService configService,
#if DEBUG
        IpcTraceRecorder ipcTraceRecorder,
#endif
        PairManager pairManager,
        PerformanceCollectorService performanceCollector,
        SnowMediator mediator,
        UiFontService fontService)
    {
        _configService = configService;
#if DEBUG
        _ipcTraceRecorder = ipcTraceRecorder;
#endif
        _pairManager = pairManager;
        _performanceCollector = performanceCollector;
        _mediator = mediator;
        _fontService = fontService;
    }

    public void Draw(CharacterData? lastCreatedCharacterData)
    {
        _fontService.BigText("Advanced");

        var logEvents = _configService.Current.LogEvents;
        if (ImGui.Checkbox("Log Event Viewer data to disk", ref logEvents))
        {
            _configService.Update(c => c.LogEvents = logEvents);
        }

        ImGui.SameLine(300.0f * ImGuiHelpers.GlobalScale);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.NotesMedical, "Open Event Viewer"))
        {
            _mediator.Publish(new UiToggleMessage(typeof(EventViewerUI)));
        }

        var holdCombatApplication = _configService.Current.HoldCombatApplication;
        if (ImGui.Checkbox("Hold application during combat", ref holdCombatApplication))
        {
            if (!holdCombatApplication)
            {
                _mediator.Publish(new CombatOrPerformanceEndMessage());
            }

            _configService.Update(c => c.HoldCombatApplication = holdCombatApplication);
        }

        ImGui.Separator();
        _fontService.BigText("Debug");
        var enableDebugFeatures = _configService.Current.EnableDebugFeatures;
        if (ImGui.Checkbox("Enable debug features", ref enableDebugFeatures))
        {
            _configService.Update(c => c.EnableDebugFeatures = enableDebugFeatures);
        }
        ElezenImgui.DrawHelpText("Enables debug-oriented UI actions such as local troubleshooting tools.");

#if DEBUG
        var recordingIpcTrace = _ipcTraceRecorder.IsCapturing;
        if (ImGui.Checkbox("Record IPC trace", ref recordingIpcTrace))
        {
            if (recordingIpcTrace)
            {
                _ipcTraceRecorder.Start();
            }
            else
            {
                _ipcTraceRecorder.Stop();
            }
        }
        ElezenImgui.DrawHelpText("Captures the outbound IPC call sequence to a trace file under the config 'traces' folder. Toggle off to flush the trace to disk.");

        if (lastCreatedCharacterData != null && ImGui.TreeNode("Last created character data"))
        {
            foreach (var line in JsonSerializer.Serialize(lastCreatedCharacterData, PrettyJsonOptions).Split('\n'))
            {
                ImGui.TextUnformatted(line);
            }

            ImGui.TreePop();
        }
#endif
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "[DEBUG] Copy Last created Character Data to clipboard"))
        {
            ImGui.SetClipboardText(lastCreatedCharacterData != null
                ? JsonSerializer.Serialize(lastCreatedCharacterData, PrettyJsonOptions)
                : "ERROR: No created character data, cannot copy.");
        }
        ElezenImgui.AttachTooltip("Use this when reporting mods being rejected from the server.");

        SettingsUiControls.DrawCombo("Log Level", Enum.GetValues<LogLevel>(), l => l.ToString(),
            _selectedComboItems,
            l => _configService.Update(c => c.LogLevel = l),
            _configService.Current.LogLevel);

        var logPerformance = _configService.Current.LogPerformance;
        if (ImGui.Checkbox("Log Performance Counters", ref logPerformance))
        {
            _configService.Update(c => c.LogPerformance = logPerformance);
        }
        ElezenImgui.DrawHelpText("Enabling this can incur a (slight) performance impact. Enabling this for extended periods of time is not recommended.");

        using (ImRaii.Disabled(!logPerformance))
        {
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Print Performance Stats to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats();
            }

            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.StickyNote, "Print Performance Stats (last 60s) to /xllog"))
            {
                _performanceCollector.PrintPerformanceStats(60);
            }
        }

        if (ImGui.TreeNode("Active Character Blocks"))
        {
            foreach (var pair in _pairManager.GetOnlineUserPairs().Where(pair => pair.IsApplicationBlocked))
            {
                ImGui.TextUnformatted(pair.PlayerName);
                ImGui.SameLine();
                ImGui.TextUnformatted(string.Join(", ", pair.HoldApplicationReasons));
            }
        }
    }
}
