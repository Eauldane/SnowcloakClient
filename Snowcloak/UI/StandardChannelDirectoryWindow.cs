using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System;
using System.Linq;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class StandardChannelDirectoryWindow : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly List<ChatChannelData> _channels = [];
    private readonly HashSet<string> _joinedChannelIds = new(StringComparer.Ordinal);
    private bool _isLoading;
    private string _filter = string.Empty;

    public StandardChannelDirectoryWindow(ILogger<StandardChannelDirectoryWindow> logger, SnowMediator mediator,
        ApiController apiController, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Standard Channels###SnowcloakStandardChannels", performanceCollectorService)
    {
        _apiController = apiController;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(900, 1200)
        };

        Mediator.Subscribe<ConnectedMessage>(this, message =>
        {
            _ = RefreshChannels();
        });

        Mediator.Subscribe<DisconnectedMessage>(this, _ =>
        {
            ClearChannels();
        });

        Mediator.Subscribe<StandardChannelMembershipChangedMessage>(this, message => OnMembershipChanged(message));
    }

    public override void OnOpen()
    {
        _ = RefreshChannels();
    }

    protected override void DrawInternal()
    {
        ImGui.TextUnformatted("Browse standard channels");
        ImGui.Separator();

        using (ImRaii.Disabled(!_apiController.IsConnected))
        {
            if (ImGui.Button("Refresh"))
            {
                _ = RefreshChannels();
            }
        }

        if (_isLoading)
        {
            ImGui.SameLine();
            ElezenImgui.ColouredWrappedText("Loading...", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelFilter", "Filter channels...", ref _filter, 80);

        if (!_apiController.IsConnected)
        {
            ElezenImgui.ColouredWrappedText("Connect to a server to load standard channels.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        using var table = ImRaii.Table("standard-channel-table", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableSetupColumn("Topic", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Privacy", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var channel in GetFilteredChannels())
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.Topic ?? string.Empty);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.IsPrivate ? "Private" : "Public");

            ImGui.TableNextColumn();
            var isJoined = _joinedChannelIds.Contains(channel.ChannelId);
            using (ImRaii.Disabled(isJoined))
            {
                if (ImGui.Button(isJoined ? "Joined" : "Join"))
                {
                    _ = JoinChannel(channel);
                }
            }
        }
    }

    private IEnumerable<ChatChannelData> GetFilteredChannels()
    {
        IEnumerable<ChatChannelData> channels = _channels;
        if (!string.IsNullOrWhiteSpace(_filter))
        {
            channels = channels.Where(channel => channel.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                                 || (channel.Topic?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return channels.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshChannels()
    {
        if (!_apiController.IsConnected) return;

        _isLoading = true;
        try
        {
            var channels = await _apiController.ChannelList().ConfigureAwait(false);
            _channels.Clear();
            foreach (var channel in channels.Select(dto => dto.Channel).Where(channel => channel.Type == ChannelType.Standard))
            {
                _channels.Add(channel);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh standard channel list.");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ClearChannels()
    {
        _channels.Clear();
        _joinedChannelIds.Clear();
        _filter = string.Empty;
    }

    private async Task JoinChannel(ChatChannelData channel)
    {
        if (!_apiController.IsConnected) return;

        try
        {
            var member = await _apiController.ChannelJoin(new ChannelDto(channel)).ConfigureAwait(false);
            if (member != null)
            {
                _joinedChannelIds.Add(channel.ChannelId);
                Mediator.Publish(new StandardChannelMembershipChangedMessage(channel, true));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join standard channel.");
        }
    }

    private void OnMembershipChanged(StandardChannelMembershipChangedMessage message)
    {
        if (message.IsJoined)
        {
            _joinedChannelIds.Add(message.Channel.ChannelId);
            if (_channels.All(channel => !string.Equals(channel.ChannelId, message.Channel.ChannelId, StringComparison.Ordinal)))
            {
                _channels.Add(message.Channel);
            }
        }
        else
        {
            _joinedChannelIds.Remove(message.Channel.ChannelId);
        }
    }
}
