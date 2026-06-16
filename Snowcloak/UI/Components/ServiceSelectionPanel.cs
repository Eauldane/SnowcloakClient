using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;

namespace Snowcloak.UI.Components;

public sealed class ServiceSelectionPanel
{
    private readonly ApiController _apiController;
    private readonly SnowcloakConfigService _configService;
    private readonly ServerRegistry _serverRegistry;
    private string _customServerName = string.Empty;
    private string _customServerUri = string.Empty;
    private int _serverSelectionIndex = -1;

    public ServiceSelectionPanel(ServerRegistry serverRegistry, ApiController apiController, SnowcloakConfigService configService)
    {
        _serverRegistry = serverRegistry;
        _apiController = apiController;
        _configService = configService;
    }

    public int Draw(bool selectOnChange = false, bool intro = false)
    {
        string[] comboEntries = _serverRegistry.GetServerNames();

        if (selectOnChange)
        {
            _serverSelectionIndex = _serverRegistry.CurrentServerIndex;
        }
        else if (_serverSelectionIndex == -1)
        {
            _serverSelectionIndex = Array.IndexOf(_serverRegistry.GetServerApiUrls(), _serverRegistry.CurrentApiUrl);
        }

        if (_serverSelectionIndex == -1 || _serverSelectionIndex >= comboEntries.Length)
        {
            _serverSelectionIndex = 0;
        }

        for (int i = 0; i < comboEntries.Length; i++)
        {
            if (string.Equals(_serverRegistry.CurrentServer?.ServerName, comboEntries[i], StringComparison.OrdinalIgnoreCase))
            {
                comboEntries[i] += " [Current]";
            }
        }

        ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Select Service", comboEntries[_serverSelectionIndex]))
        {
            for (int i = 0; i < comboEntries.Length; i++)
            {
                bool isSelected = _serverSelectionIndex == i;
                if (ImGui.Selectable(comboEntries[i], isSelected))
                {
                    _serverSelectionIndex = i;
                    if (selectOnChange)
                    {
                        _serverRegistry.SelectServer(i);
                    }
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        if (intro)
        {
            return _serverSelectionIndex;
        }

        ImGui.SameLine();
        var text = _serverSelectionIndex == _serverRegistry.CurrentServerIndex ? "Reconnect" : "Connect";
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Link, text))
        {
            _serverRegistry.SelectServer(_serverSelectionIndex);
            _ = _apiController.CreateConnections();
        }

        if (ImGui.TreeNode("Add Custom Service"))
        {
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Custom Service URI", ref _customServerUri, 255);
            ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
            ImGui.InputText("Custom Service Name", ref _customServerName, 255);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Add Custom Service")
                && !string.IsNullOrEmpty(_customServerUri)
                && !string.IsNullOrEmpty(_customServerName))
            {
                _serverRegistry.AddServer(new ServerStorage
                {
                    ServerName = _customServerName,
                    ServerUri = _customServerUri,
                });
                _customServerName = string.Empty;
                _customServerUri = string.Empty;
                _configService.Update(_ => { });
            }
            ImGui.TreePop();
        }

        return _serverSelectionIndex;
    }
}
