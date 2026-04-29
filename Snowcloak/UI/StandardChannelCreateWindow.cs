using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Snowcloak.UI;

public sealed class StandardChannelCreateWindow : WindowMediatorSubscriberBase
{
    private static readonly Regex ChannelNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly ApiController _apiController;
    private string _channelName = string.Empty;
    private string _channelTopic = string.Empty;
    private bool _isPrivate;

    public StandardChannelCreateWindow(ILogger<StandardChannelCreateWindow> logger, SnowMediator mediator,
        ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Create Standard Channel###SnowcloakStandardChannelCreate", performanceCollectorService)
    {
        _apiController = apiController;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 240),
            MaximumSize = new Vector2(700, 600)
        };
    }

    protected override void DrawInternal()
    {
        ImGui.TextUnformatted("Create a standard channel");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelName", "Channel name", ref _channelName, 80);
        var trimmedName = _channelName.Trim();
        var validChannelName = !string.IsNullOrWhiteSpace(trimmedName) && ChannelNameRegex.IsMatch(trimmedName);
        if (!string.IsNullOrWhiteSpace(trimmedName) && !validChannelName)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.45f, 1f), "Use IRC-style names: letters/numbers with optional '-' or '_' (no spaces).");
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelTopic", "Topic (optional)", ref _channelTopic, 200);

        ImGui.Checkbox("Private", ref _isPrivate);

        using (ImRaii.Disabled(!_apiController.IsConnected || !validChannelName))
        {
            if (ImGui.Button("Create"))
            {
                _ = CreateChannel();
            }
        }
    }

    private async Task CreateChannel()
    {
        if (!_apiController.IsConnected) return;

        var name = _channelName.Trim();
        if (string.IsNullOrWhiteSpace(name) || !ChannelNameRegex.IsMatch(name)) return;

        var topic = string.IsNullOrWhiteSpace(_channelTopic) ? null : _channelTopic.Trim();
        var createDto = new ChannelCreateDto(name, topic, _isPrivate, ChannelType.Standard);
        try
        {
            var created = await _apiController.ChannelCreate(createDto).ConfigureAwait(false);
            var channel = created.Channel;

            var member = await _apiController.ChannelJoin(new ChannelDto(channel)).ConfigureAwait(false);
            if (member != null)
            {
                Mediator.Publish(new StandardChannelMembershipChangedMessage(member.Channel, true));
            }
            else
            {
                Mediator.Publish(new StandardChannelMembershipChangedMessage(channel, true));
            }

            _channelName = string.Empty;
            _channelTopic = string.Empty;
            _isPrivate = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create standard channel.");
        }
    }
}
