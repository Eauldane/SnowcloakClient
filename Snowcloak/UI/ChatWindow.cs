using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Snowcloak.UI;

public class ChatWindow : WindowMediatorSubscriberBase
{
    private enum ChannelKind
    {
        Syncshell,
        Standard,
        Direct
    }

    private readonly record struct ChatChannelKey(ChannelKind Kind, string Id);

    private sealed record ChatLine(DateTime Timestamp, string Sender, string Message);

    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<ChatChannelKey, List<ChatLine>> _channelLogs = [];
    private readonly List<ChatChannelData> _standardChannels = [];
    private readonly Dictionary<string, ChatChannelData> _standardChannelLookup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _joinedStandardChannels = new(StringComparer.Ordinal);
    private ChatChannelKey? _selectedChannel;
    private string _pendingMessage = string.Empty;
    private bool _autoScroll = true;
    private string _standardChannelNameInput = string.Empty;
    private string _standardChannelTopicInput = string.Empty;
    private bool _standardChannelPrivateInput;
    private string _standardChannelTopicDraft = string.Empty;
    private bool _standardChannelListLoading;

    public ChatWindow(ILogger<ChatWindow> logger, SnowMediator mediator, ApiController apiController,
        PairManager pairManager, ServerConfigurationManager serverManager, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Chat###SnowcloakChatWindow", performanceCollectorService)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _serverManager = serverManager;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 400),
            MaximumSize = new Vector2(1400, 1200)
        };

        Mediator.Subscribe<GroupChatMsgMessage>(this, message => AddGroupMessage(message));
        Mediator.Subscribe<UserChatMsgMessage>(this, message => AddDirectMessage(message.ChatMsg));
        Mediator.Subscribe<ConnectedMessage>(this, _message =>
        {
            _ = RefreshStandardChannels();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, message =>
        {
            ClearStandardChannels();
        });
    }

    protected override void DrawInternal()
    {
        DrawChatLayout();
    }

    private void DrawChatLayout()
    {
        var inputHeight = ImGui.GetFrameHeightWithSpacing() * 2.2f;
        using var _ = ImRaii.Child("ChatLayout", new Vector2(-1, -1), false);

        DrawChatColumns(inputHeight);
        DrawChatInput();
    }

    private void DrawChatColumns(float inputHeight)
    {
        using var _ = ImRaii.Child("ChatColumns", new Vector2(-1, -inputHeight), false);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var listWidth = 190f * ImGuiHelpers.GlobalScale;
        var memberWidth = 200f * ImGuiHelpers.GlobalScale;
        var centerWidth = ImGui.GetContentRegionAvail().X - listWidth - memberWidth - spacing * 2f;
        if (centerWidth < 220f * ImGuiHelpers.GlobalScale)
        {
            memberWidth = 160f * ImGuiHelpers.GlobalScale;
            centerWidth = ImGui.GetContentRegionAvail().X - listWidth - memberWidth - spacing * 2f;
        }

        DrawChannelList(listWidth);
        ImGui.SameLine();
        DrawChatLog(centerWidth);
        ImGui.SameLine();
        DrawMemberList(memberWidth);
    }

    private void DrawChannelList(float width)
    {
        using var _ = ImRaii.Child("ChannelList", new Vector2(width, -1), true);
        ImGui.TextUnformatted("Channels");
        ImGui.Separator();

        DrawStandardChannelControls();
        DrawChannelSection("Standard Channels", ChannelKind.Standard, GetStandardChannels());
        ImGui.Separator();
        DrawChannelSection("Syncshells", ChannelKind.Syncshell, GetSyncshellChannels());
        ImGui.Separator();
        DrawChannelSection("Direct Messages", ChannelKind.Direct, GetDirectChannels());
    }

    private void DrawChannelSection(string label, ChannelKind kind, List<(string Id, string Name)> channels)
    {
        using (var header = ImRaii.TreeNode(label))
        {
            if (!header.Success) return;
            foreach (var channel in channels)
            {
                var key = new ChatChannelKey(kind, channel.Id);
                var displayName = GetChannelDisplayName(kind, channel.Id, channel.Name);
                var isSelected = _selectedChannel.HasValue && _selectedChannel.Value.Equals(key);
                if (ImGui.Selectable(displayName, isSelected))
                {
                    SetSelectedChannel(key);
                }
            }
        }

        if (_selectedChannel == null && channels.Count > 0)
        {
            var first = channels[0];
            SetSelectedChannel(new ChatChannelKey(kind, first.Id));
        }
    }

    private void DrawChatLog(float width)
    {
        using var _ = ImRaii.Child("ChatLog", new Vector2(width, -1), true, ImGuiWindowFlags.HorizontalScrollbar);
        if (_selectedChannel == null)
        {
            UiSharedService.ColorTextWrapped("Select a channel to start chatting.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        var key = _selectedChannel.Value;
        if (!_channelLogs.TryGetValue(key, out var log))
        {
            log = [];
        }

        if (key.Kind == ChannelKind.Standard)
        {
            DrawStandardChannelLogHint(key);
        }

        var shouldScroll = _autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 10f;

        foreach (var entry in log)
        {
            var timestamp = entry.Timestamp.ToString("HH:mm", CultureInfo.InvariantCulture);
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "[{0}] {1}: {2}", timestamp, entry.Sender, entry.Message));
        }

        if (shouldScroll)
        {
            ImGui.SetScrollHereY(1.0f);
        }
    }

    private void DrawMemberList(float width)
    {
        using var _ = ImRaii.Child("MemberList", new Vector2(width, -1), true);
        ImGui.TextUnformatted("Members");
        ImGui.Separator();

        if (_selectedChannel == null)
        {
            UiSharedService.ColorTextWrapped("No channel selected.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        var key = _selectedChannel.Value;
        if (key.Kind == ChannelKind.Syncshell)
        {
            DrawSyncshellMembers(key);
        }
        else if (key.Kind == ChannelKind.Standard)
        {
            DrawStandardChannelDetails(key);
        }
        else
        {
            DrawDirectMembers(key);
        }
    }

    private void DrawSyncshellMembers(ChatChannelKey key)
    {
        var groupInfo = GetGroupInfo(key.Id);
        if (groupInfo == null)
        {
            UiSharedService.ColorTextWrapped("Unknown syncshell.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        var members = new List<(UserData User, int Rank)>();
        if (groupInfo.Owner != null)
        {
            members.Add((groupInfo.Owner, 3));
        }

        if (_pairManager.GroupPairs.TryGetValue(groupInfo, out var pairs))
        {
            foreach (var pair in pairs)
            {
                var rank = IsModerator(pair, groupInfo) ? 2 : 1;
                members.Add((pair.UserData, rank));
            }
        }

        foreach (var entry in members
                     .GroupBy(m => m.User.UID)
                     .Select(g => g.OrderByDescending(x => x.Rank).First())
                     .OrderByDescending(m => m.Rank)
                     .ThenBy(m => GetUserDisplayName(m.User), StringComparer.OrdinalIgnoreCase))
        {
            var prefix = GetRolePrefix(groupInfo, entry.User);
            ImGui.TextUnformatted(prefix + GetUserDisplayName(entry.User));
        }
    }

    private void DrawDirectMembers(ChatChannelKey key)
    {
        var userData = GetUserData(key.Id);
        if (userData != null)
        {
            ImGui.TextUnformatted(GetUserDisplayName(userData));
        }

        if (!string.IsNullOrWhiteSpace(_apiController.UID))
        {
            var selfName = GetUserDisplayName(new UserData(_apiController.UID, _apiController.VanityId));
            UiSharedService.ColorTextWrapped(string.Format(CultureInfo.InvariantCulture, "You ({0})", selfName), ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }
    }

    private void DrawChatInput()
    {
        var canSend = _selectedChannel != null && _apiController.IsConnected && _selectedChannel.Value.Kind != ChannelKind.Standard;
        using var disabled = ImRaii.Disabled(!canSend);

        var inputWidth = ImGui.GetContentRegionAvail().X - (70f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(inputWidth);
        var send = ImGui.InputTextWithHint("##ChatInput", "Type a message...", ref _pendingMessage, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("Send", new Vector2(60f * ImGuiHelpers.GlobalScale, 0)) || send)
        {
            if (_selectedChannel != null)
            {
                SendMessage(_selectedChannel.Value, _pendingMessage);
                _pendingMessage = string.Empty;
            }
        }

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
    }

    private void SendMessage(ChatChannelKey channel, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var trimmed = message.Trim();
        var chatMessage = new ChatMessage
        {
            PayloadContent = Encoding.UTF8.GetBytes(trimmed)
        };

        if (channel.Kind == ChannelKind.Syncshell)
        {
            var group = GetGroupData(channel.Id);
            if (group == null) return;
            _ = _apiController.GroupChatSendMsg(new GroupDto(group), chatMessage);
        }
        else if (channel.Kind == ChannelKind.Standard)
        {
            return;
        }
        else
        {
            var userData = GetUserData(channel.Id);
            if (userData == null) return;
            _ = _apiController.UserChatSendMsg(new UserDto(userData), chatMessage);
        }

        AddLocalMessage(channel, trimmed);
    }

    private void AddLocalMessage(ChatChannelKey channel, string message)
    {
        var selfUser = new UserData(_apiController.UID, _apiController.VanityId);
        var displayName = FormatSenderName(channel, selfUser);
        AppendMessage(channel, displayName, message, DateTime.UtcNow);
    }

    private void AddGroupMessage(GroupChatMsgMessage message)
    {
        var channel = new ChatChannelKey(ChannelKind.Syncshell, message.GroupInfo.GID);
        var text = DecodeMessage(message.ChatMsg.PayloadContent);
        var displayName = FormatSenderName(channel, message.ChatMsg.Sender);
        AppendMessage(channel, displayName, text, ResolveTimestamp(message.ChatMsg.Timestamp));
    }

    private void AddDirectMessage(SignedChatMessage message)
    {
        var channel = new ChatChannelKey(ChannelKind.Direct, message.Sender.UID);
        var text = DecodeMessage(message.PayloadContent);
        var displayName = FormatSenderName(channel, message.Sender);
        AppendMessage(channel, displayName, text, ResolveTimestamp(message.Timestamp));
    }

    private void AppendMessage(ChatChannelKey channel, string sender, string message, DateTime timestamp)
    {
        if (!_channelLogs.TryGetValue(channel, out var log))
        {
            log = [];
            _channelLogs[channel] = log;
        }

        log.Add(new ChatLine(timestamp, sender, message));
        if (_selectedChannel == null)
        {
            _selectedChannel = channel;
        }
    }

    private string DecodeMessage(byte[] payload)
    {
        if (payload.Length == 0) return string.Empty;
        return Encoding.UTF8.GetString(payload);
    }

    private DateTime ResolveTimestamp(long timestamp)
    {
        if (timestamp <= 0)
        {
            return DateTime.UtcNow;
        }

        return DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
    }

    private string GetUserDisplayName(UserData user)
    {
        var note = _serverManager.GetNoteForUid(user.UID);
        if (!string.IsNullOrWhiteSpace(note)) return note;
        if (!string.IsNullOrWhiteSpace(user.Alias)) return user.Alias!;
        return user.UID;
    }

    private string FormatSenderName(ChatChannelKey channel, UserData user)
    {
        var prefix = channel.Kind == ChannelKind.Syncshell
            ? GetRolePrefix(GetGroupInfo(channel.Id), user)
            : string.Empty;
        return prefix + GetUserDisplayName(user);
    }

    private string GetRolePrefix(GroupFullInfoDto? groupInfo, UserData user)
    {
        if (groupInfo == null) return string.Empty;
        if (string.Equals(groupInfo.Owner.UID, user.UID, StringComparison.Ordinal)) return "~";
        var pair = _pairManager.GetPairByUID(user.UID);
        if (pair != null && IsModerator(pair, groupInfo)) return "@";
        return string.Empty;
    }

    private bool IsModerator(Pair pair, GroupFullInfoDto groupInfo)
    {
        if (pair.GroupPair.TryGetValue(groupInfo, out var groupPairInfo))
        {
            return groupPairInfo.GroupPairStatusInfo.HasFlag(API.Data.Enum.GroupUserInfo.IsModerator);
        }

        return false;
    }

    private GroupFullInfoDto? GetGroupInfo(string gid)
    {
        return _pairManager.GroupPairs.Keys.FirstOrDefault(group => string.Equals(group.GID, gid, StringComparison.Ordinal));
    }

    private GroupData? GetGroupData(string gid)
    {
        var groupInfo = GetGroupInfo(gid);
        return groupInfo?.Group;
    }

    private UserData? GetUserData(string uid)
    {
        return _pairManager.GetPairByUID(uid)?.UserData ?? new UserData(uid);
    }

    private List<(string Id, string Name)> GetSyncshellChannels()
    {
        return _pairManager.GroupPairs.Keys
            .Select(group => (group.GID, GetChannelName(group.Group)))
            .OrderBy(channel => channel.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<(string Id, string Name)> GetStandardChannels()
    {
        return _standardChannels
            .Select(channel => (channel.ChannelId, channel.Name))
            .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<(string Id, string Name)> GetDirectChannels()
    {
        return _pairManager.DirectPairs
            .Select(pair => (pair.UserData.UID, GetUserDisplayName(pair.UserData)))
            .OrderBy(channel => channel.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetChannelName(GroupData group)
    {
        var note = _serverManager.GetNoteForGid(group.GID);
        if (!string.IsNullOrWhiteSpace(note)) return note;
        if (!string.IsNullOrWhiteSpace(group.Alias)) return group.Alias!;
        return group.GID;
    }

    private string GetChannelDisplayName(ChannelKind kind, string id, string name)
    {
        if (kind == ChannelKind.Syncshell)
        {
            return string.Format(CultureInfo.InvariantCulture, "# {0}", name);
        }

        if (kind == ChannelKind.Standard)
        {
            var channel = GetStandardChannel(id);
            var privateSuffix = channel?.IsPrivate == true ? " (private)" : string.Empty;
            var joinedSuffix = _joinedStandardChannels.Contains(id) ? " (joined)" : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "# {0}{1}{2}", name, privateSuffix, joinedSuffix);
        }

        return name;
    }

    private ChatChannelData? GetStandardChannel(string id)
    {
        return _standardChannelLookup.TryGetValue(id, out var channel) ? channel : null;
    }

    private void SetSelectedChannel(ChatChannelKey key)
    {
        _selectedChannel = key;
        if (key.Kind == ChannelKind.Standard)
        {
            var channel = GetStandardChannel(key.Id);
            _standardChannelTopicDraft = channel?.Topic ?? string.Empty;
        }
    }

    private void DrawStandardChannelControls()
    {
        ImGui.TextUnformatted("Standard Channels");
        using (ImRaii.Disabled(!_apiController.IsConnected))
        {
            if (ImGui.Button("Refresh List"))
            {
                _ = RefreshStandardChannels();
            }
        }

        if (_standardChannelListLoading)
        {
            ImGui.SameLine();
            UiSharedService.ColorTextWrapped("Loading...", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        using var header = ImRaii.TreeNode("Create Channel");
        if (!header.Success) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelName", "Channel name", ref _standardChannelNameInput, 80);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelTopic", "Topic (optional)", ref _standardChannelTopicInput, 200);
        ImGui.Checkbox("Private", ref _standardChannelPrivateInput);

        using (ImRaii.Disabled(!_apiController.IsConnected || string.IsNullOrWhiteSpace(_standardChannelNameInput)))
        {
            if (ImGui.Button("Create"))
            {
                _ = CreateStandardChannel();
            }
        }
    }

    private void DrawStandardChannelDetails(ChatChannelKey key)
    {
        var channel = GetStandardChannel(key.Id);
        if (channel == null)
        {
            UiSharedService.ColorTextWrapped("Unknown channel.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        ImGui.TextUnformatted(channel.Name);
        UiSharedService.ColorTextWrapped(channel.IsPrivate ? "Private channel" : "Public channel", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

        var joined = _joinedStandardChannels.Contains(channel.ChannelId);
        using (ImRaii.Disabled(!_apiController.IsConnected))
        {
            if (!joined)
            {
                if (ImGui.Button("Join"))
                {
                    _ = JoinStandardChannel(channel);
                }
            }
            else
            {
                if (ImGui.Button("Leave"))
                {
                    _ = LeaveStandardChannel(channel);
                }
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Topic");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##StandardChannelTopicEdit", "Set topic", ref _standardChannelTopicDraft, 200);
        using (ImRaii.Disabled(!_apiController.IsConnected))
        {
            if (ImGui.Button("Update Topic"))
            {
                _ = UpdateStandardChannelTopic(channel, _standardChannelTopicDraft);
            }
        }
    }

    private void DrawStandardChannelLogHint(ChatChannelKey key)
    {
        if (!_joinedStandardChannels.Contains(key.Id))
        {
            UiSharedService.ColorTextWrapped("Join this channel to receive messages.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        UiSharedService.ColorTextWrapped("Standard channel chat is not yet available in the client.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
    }

    private async Task RefreshStandardChannels()
    {
        if (!_apiController.IsConnected) return;

        _standardChannelListLoading = true;
        try
        {
            var channels = await _apiController.ChannelList().ConfigureAwait(false);
            _standardChannels.Clear();
            _standardChannelLookup.Clear();

            foreach (var channel in channels.Select(dto => dto.Channel).Where(channel => channel.Type == ChannelType.Standard))
            {
                _standardChannels.Add(channel);
                _standardChannelLookup[channel.ChannelId] = channel;
            }

            _joinedStandardChannels.RemoveWhere(id => !_standardChannelLookup.ContainsKey(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh standard channels.");
        }
        finally
        {
            _standardChannelListLoading = false;
        }
    }

    private void ClearStandardChannels()
    {
        _standardChannels.Clear();
        _standardChannelLookup.Clear();
        _joinedStandardChannels.Clear();
    }

    private async Task CreateStandardChannel()
    {
        if (!_apiController.IsConnected) return;

        var name = _standardChannelNameInput.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var createDto = new ChannelCreateDto(name, ChannelType.Standard, string.IsNullOrWhiteSpace(_standardChannelTopicInput) ? null : _standardChannelTopicInput.Trim(), _standardChannelPrivateInput);
        try
        {
            var created = await _apiController.ChannelCreate(createDto).ConfigureAwait(false);
            var channel = created.Channel;
            _standardChannels.RemoveAll(existing => string.Equals(existing.ChannelId, channel.ChannelId, StringComparison.Ordinal));
            _standardChannels.Add(channel);
            _standardChannelLookup[channel.ChannelId] = channel;
            _joinedStandardChannels.Add(channel.ChannelId);
            SetSelectedChannel(new ChatChannelKey(ChannelKind.Standard, channel.ChannelId));

            _standardChannelNameInput = string.Empty;
            _standardChannelTopicInput = string.Empty;
            _standardChannelPrivateInput = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create standard channel.");
        }
    }

    private async Task JoinStandardChannel(ChatChannelData channel)
    {
        if (!_apiController.IsConnected) return;

        try
        {
            var member = await _apiController.ChannelJoin(new ChannelDto(channel)).ConfigureAwait(false);
            if (member != null)
            {
                _joinedStandardChannels.Add(channel.ChannelId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join standard channel.");
        }
    }

    private async Task LeaveStandardChannel(ChatChannelData channel)
    {
        if (!_apiController.IsConnected) return;

        try
        {
            await _apiController.ChannelLeave(new ChannelDto(channel)).ConfigureAwait(false);
            _joinedStandardChannels.Remove(channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to leave standard channel.");
        }
    }

    private async Task UpdateStandardChannelTopic(ChatChannelData channel, string? topic)
    {
        if (!_apiController.IsConnected) return;

        try
        {
            await _apiController.ChannelSetTopic(new ChannelTopicUpdateDto(channel, string.IsNullOrWhiteSpace(topic) ? null : topic.Trim()))
                .ConfigureAwait(false);
            channel.Topic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update standard channel topic.");
        }
    }
}
