using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ElezenTools.UI;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.Configuration.Models;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using System;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class StandardChannelDirectoryWindow : WindowMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly ServerConfigurationManager _serverManager;
    private readonly List<ChatChannelData> _channels = [];
    private readonly HashSet<string> _joinedChannelIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _channelUserCounts = new(StringComparer.Ordinal);
    private bool _isLoading;
    private string _filter = string.Empty;
    private bool _hideEmptyChannels = true;
    private bool _hasUserCounts;

    public StandardChannelDirectoryWindow(ILogger<StandardChannelDirectoryWindow> logger, SnowMediator mediator,
        ApiController apiController, ServerConfigurationManager serverManager,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Standard Channels###SnowcloakStandardChannels", performanceCollectorService)
    {
        _apiController = apiController;
        _serverManager = serverManager;

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
        ImGui.Checkbox("Hide empty channels", ref _hideEmptyChannels);

        if (!_hasUserCounts)
        {
            ElezenImgui.ColouredWrappedText("User counts are temporarily unavailable; showing all channels.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        if (!_apiController.IsConnected)
        {
            ElezenImgui.ColouredWrappedText("Connect to a server to load standard channels.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        using var table = ImRaii.Table("standard-channel-table", 5, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Topic", ImGuiTableColumnFlags.WidthStretch, 0.37f);
        ImGui.TableSetupColumn("Users", ImGuiTableColumnFlags.WidthFixed, 60f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Privacy", ImGuiTableColumnFlags.WidthFixed, 80f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        foreach (var channel in GetFilteredChannels())
        {
            ImGui.TableNextRow();
            ImGui.PushID(channel.ChannelId);
            var userCount = GetChannelUserCount(channel.ChannelId);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.Topic ?? string.Empty);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(_hasUserCounts ? userCount.ToString(CultureInfo.InvariantCulture) : "?");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(channel.IsPrivate ? "Private" : "Public");

            ImGui.TableNextColumn();
            var isJoined = _joinedChannelIds.Contains(channel.ChannelId);
            using (ImRaii.Disabled(isJoined))
            {
                if (ImGui.Button(isJoined ? "Joined##join" : "Join##join"))
                {
                    _ = JoinChannel(channel);
                }
            }

            ImGui.PopID();
        }
    }

    private IEnumerable<ChatChannelData> GetFilteredChannels()
    {
        IEnumerable<ChatChannelData> channels = _channels;
        if (_hideEmptyChannels && _hasUserCounts)
        {
            channels = channels.Where(channel => GetChannelUserCount(channel.ChannelId) > 0);
        }

        if (!string.IsNullOrWhiteSpace(_filter))
        {
            channels = channels.Where(channel => channel.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                                 || (channel.Topic?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return channels.OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task RefreshChannels()
    {
        LoadJoinedChannels();
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

            _channelUserCounts.Clear();
            var counts = await _apiController.ChannelListUserCounts().ConfigureAwait(false);
            foreach (var channel in _channels)
            {
                _channelUserCounts[channel.ChannelId] = counts.TryGetValue(channel.ChannelId, out var count) ? Math.Max(count, 0) : 0;
            }
            _hasUserCounts = true;
        }
        catch (HubException ex)
        {
            _hasUserCounts = false;
            _channelUserCounts.Clear();
            _logger.LogWarning(ex, "Failed to refresh standard channel list counts.");
        }
        catch (Exception ex)
        {
            _hasUserCounts = false;
            _channelUserCounts.Clear();
            _logger.LogWarning(ex, "Failed to refresh standard channel list.");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadJoinedChannels()
    {
        _joinedChannelIds.Clear();
        foreach (var channel in _serverManager.GetJoinedStandardChannels())
        {
            _joinedChannelIds.Add(channel.ChannelId);
        }
    }

    private void ClearChannels()
    {
        _channels.Clear();
        _joinedChannelIds.Clear();
        _channelUserCounts.Clear();
        _hasUserCounts = false;
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
            else
            {
                Mediator.Publish(new NotificationMessage(
                    "Join failed",
                    "Could not join this channel. You may be banned or no longer have access.",
                    NotificationType.Warning,
                    TimeSpan.FromSeconds(6)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join standard channel.");
            Mediator.Publish(new NotificationMessage(
                "Join failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
        }
    }

    private void OnMembershipChanged(StandardChannelMembershipChangedMessage message)
    {
        var channelId = message.Channel.ChannelId;
        if (message.IsJoined)
        {
            _joinedChannelIds.Add(channelId);
            if (_channels.All(channel => !string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal)))
            {
                _channels.Add(message.Channel);
            }

            if (_hasUserCounts)
            {
                _channelUserCounts[channelId] = Math.Max(GetChannelUserCount(channelId) + 1, 1);
            }
        }
        else
        {
            _joinedChannelIds.Remove(channelId);
            if (_hasUserCounts)
            {
                _channelUserCounts[channelId] = Math.Max(GetChannelUserCount(channelId) - 1, 0);
            }
        }
    }

    private int GetChannelUserCount(string channelId)
    {
        return _channelUserCounts.TryGetValue(channelId, out var count) ? count : 0;
    }
}
