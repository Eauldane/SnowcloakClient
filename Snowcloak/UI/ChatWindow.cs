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
    private readonly Dictionary<string, List<ChannelMemberDto>> _standardChannelMembers = new(StringComparer.Ordinal);
    private ChatChannelKey? _selectedChannel;
    private string _pendingMessage = string.Empty;
    private bool _autoScroll = true;
    private string _standardChannelTopicDraft = string.Empty;
    private bool _isEditingStandardChannelTopic;

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
        Mediator.Subscribe<ChannelChatMsgMessage>(this, message => AddStandardChannelMessage(message));
        Mediator.Subscribe<ChannelMemberJoinedMessage>(this, message => HandleStandardChannelMemberJoined(message.Member));
        Mediator.Subscribe<ChannelMemberLeftMessage>(this, message => HandleStandardChannelMemberLeft(message.Member));
        Mediator.Subscribe<ConnectedMessage>(this, _message =>
        {
            _ = RefreshStandardChannels();
            _ = AutoJoinStandardChannels();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, message =>
        {
            ClearStandardChannels();
        });
        Mediator.Subscribe<StandardChannelMembershipChangedMessage>(this, message => OnStandardChannelMembershipChanged(message));

        LoadSavedStandardChannels();
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
        DrawChatCenter(centerWidth);
        ImGui.SameLine();
        DrawMemberList(memberWidth);
    }

    private void DrawChatCenter(float width)
    {
        using var _ = ImRaii.Child("ChatCenter", new Vector2(width, -1), false);
        DrawChannelHeader();
        DrawChatLog();
    }

    private void DrawChannelList(float width)
    {
        using var _ = ImRaii.Child("ChannelList", new Vector2(width, -1), true);
        ImGui.TextUnformatted("Channels");
        ImGui.Separator();

        DrawStandardChannelSection();
        ImGui.Separator();
        DrawChannelSection("Syncshells", ChannelKind.Syncshell, GetSyncshellChannels());
        ImGui.Separator();
        DrawChannelSection("Direct Messages", ChannelKind.Direct, GetDirectChannels());
    }

    private void DrawStandardChannelSection()
    {
        using (var header = ImRaii.TreeNode("Standard Channels"))
        {
            if (header.Success)
            {
                var channels = GetJoinedStandardChannels();
                if (channels.Count == 0)
                {
                    UiSharedService.ColorTextWrapped("No joined standard channels.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                }
                else
                {
                    foreach (var channel in channels)
                    {
                        var key = new ChatChannelKey(ChannelKind.Standard, channel.Id);
                        var displayName = GetChannelDisplayName(ChannelKind.Standard, channel.Id, channel.Name);
                        var isSelected = _selectedChannel.HasValue && _selectedChannel.Value.Equals(key);
                        if (ImGui.Selectable(displayName, isSelected))
                        {
                            SetSelectedChannel(key);
                        }
                    }
                }
            }
        }

        var buttonWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Browse Channels", new Vector2(buttonWidth, 0)))
        {
            Mediator.Publish(new UiToggleMessage(typeof(StandardChannelDirectoryWindow)));
        }

        if (ImGui.Button("Create Channel", new Vector2(buttonWidth, 0)))
        {
            Mediator.Publish(new UiToggleMessage(typeof(StandardChannelCreateWindow)));
        }

        var joinedChannels = GetJoinedStandardChannels();
        if (_selectedChannel == null && joinedChannels.Count > 0)
        {
            var first = joinedChannels[0];
            SetSelectedChannel(new ChatChannelKey(ChannelKind.Standard, first.Id));
        }
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

    private void DrawChatLog()
    {
        using var _ = ImRaii.Child("ChatLog", new Vector2(-1, -1), true, ImGuiWindowFlags.HorizontalScrollbar);
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

    private void DrawChannelHeader()
    {
        if (_selectedChannel == null)
        {
            ImGui.TextUnformatted("No channel selected");
            ImGui.Separator();
            return;
        }

        var key = _selectedChannel.Value;
        var displayName = GetChannelDisplayName(key.Kind, key.Id, GetChannelDisplayLabel(key));
        ImGui.TextUnformatted(displayName);

        if (key.Kind == ChannelKind.Standard)
        {
            var channel = GetStandardChannel(key.Id);
            var topic = channel?.Topic ?? "No topic set.";
            ImGui.TextUnformatted("Topic");
            if (CanEditStandardChannelTopic(key.Id))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(_isEditingStandardChannelTopic ? "Cancel" : "Edit Topic"))
                {
                    _isEditingStandardChannelTopic = !_isEditingStandardChannelTopic;
                }
            }

            UiSharedService.ColorTextWrapped(topic, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

            if (_isEditingStandardChannelTopic)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##StandardChannelTopicEdit", "Update topic", ref _standardChannelTopicDraft, 200);
                using (ImRaii.Disabled(!_apiController.IsConnected))
                {
                    if (ImGui.Button("Update Topic"))
                    {
                        if (channel != null)
                        {
                            _ = UpdateStandardChannelTopic(channel, _standardChannelTopicDraft);
                        }
                    }
                }
            }

            if (channel != null)
            {
                var joined = _joinedStandardChannels.Contains(channel.ChannelId);
                using (ImRaii.Disabled(!_apiController.IsConnected))
                {
                    if (!joined)
                    {
                        if (ImGui.Button("Join Channel"))
                        {
                            _ = JoinStandardChannel(channel);
                        }
                    }
                    else
                    {
                        if (ImGui.Button("Leave Channel"))
                        {
                            _ = LeaveStandardChannel(channel);
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Refresh Members"))
                        {
                            _ = RefreshStandardChannelMembers(channel.ChannelId);
                        }
                    }
                }
            }
        }

        ImGui.Separator();
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
        var canSend = _selectedChannel != null
                      && _apiController.IsConnected
                      && (_selectedChannel.Value.Kind != ChannelKind.Standard || _joinedStandardChannels.Contains(_selectedChannel.Value.Id));
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
            if (!_joinedStandardChannels.Contains(channel.Id)) return;
            var standardChannel = GetStandardChannel(channel.Id);
            if (standardChannel == null) return;
            _ = _apiController.ChannelChatSendMsg(new ChannelDto(standardChannel), chatMessage);
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

    private void AddStandardChannelMessage(ChannelChatMsgMessage message)
    {
        var channelData = message.ChannelInfo.Channel;
        TrackStandardChannel(channelData);
        if (!_standardChannelMembers.ContainsKey(channelData.ChannelId))
        {
            _ = RefreshStandardChannelMembers(channelData.ChannelId);
        }
        var channel = new ChatChannelKey(ChannelKind.Standard, channelData.ChannelId);
        var text = DecodeMessage(message.ChatMsg.PayloadContent);
        var displayName = FormatSenderName(channel, message.ChatMsg.Sender);
        AppendMessage(channel, displayName, text, ResolveTimestamp(message.ChatMsg.Timestamp));
    }

    private void HandleStandardChannelMemberJoined(ChannelMemberJoinedDto member)
    {
        TrackStandardChannel(member.Channel);
        if (_standardChannelMembers.TryGetValue(member.Channel.ChannelId, out var members))
        {
            var existing = members.FindIndex(entry => string.Equals(entry.User.UID, member.User.UID, StringComparison.Ordinal));
            if (existing >= 0)
            {
                members[existing] = new ChannelMemberDto(member.Channel, member.User, member.Roles);
            }
            else
            {
                members.Add(new ChannelMemberDto(member.Channel, member.User, member.Roles));
            }
        }
        else if (_joinedStandardChannels.Contains(member.Channel.ChannelId))
        {
            _ = RefreshStandardChannelMembers(member.Channel.ChannelId);
        }
    }

    private void HandleStandardChannelMemberLeft(ChannelMemberLeftDto member)
    {
        TrackStandardChannel(member.Channel);
        if (_standardChannelMembers.TryGetValue(member.Channel.ChannelId, out var members))
        {
            members.RemoveAll(entry => string.Equals(entry.User.UID, member.User.UID, StringComparison.Ordinal));
        }
        else if (_joinedStandardChannels.Contains(member.Channel.ChannelId))
        {
            _ = RefreshStandardChannelMembers(member.Channel.ChannelId);
        }
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
        var prefix = channel.Kind switch
        {
            ChannelKind.Syncshell => GetRolePrefix(GetGroupInfo(channel.Id), user),
            ChannelKind.Standard => GetStandardChannelRolePrefix(channel.Id, user.UID),
            _ => string.Empty
        };
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

    private string GetStandardChannelRolePrefix(string channelId, string uid)
    {
        var roles = GetStandardChannelMemberRoles(channelId, uid);
        return GetChannelRolePrefix(roles);
    }

    private string GetChannelRolePrefix(ChannelUserRole roles)
    {
        if (roles.HasFlag(ChannelUserRole.Owner)) return "~";
        if (roles.HasFlag(ChannelUserRole.Admin)) return "&";
        if (roles.HasFlag(ChannelUserRole.Operator)) return "@";
        if (roles.HasFlag(ChannelUserRole.HalfOperator)) return "%";
        if (roles.HasFlag(ChannelUserRole.Voice)) return "+";
        return string.Empty;
    }

    private int GetChannelRoleRank(ChannelUserRole roles)
    {
        if (roles.HasFlag(ChannelUserRole.Owner)) return 5;
        if (roles.HasFlag(ChannelUserRole.Admin)) return 4;
        if (roles.HasFlag(ChannelUserRole.Operator)) return 3;
        if (roles.HasFlag(ChannelUserRole.HalfOperator)) return 2;
        if (roles.HasFlag(ChannelUserRole.Voice)) return 1;
        return 0;
    }

    private ChannelUserRole GetStandardChannelMemberRoles(string channelId, string uid)
    {
        if (!_standardChannelMembers.TryGetValue(channelId, out var members))
        {
            return ChannelUserRole.None;
        }

        var member = members.FirstOrDefault(entry => string.Equals(entry.User.UID, uid, StringComparison.Ordinal));
        return member?.Roles ?? ChannelUserRole.None;
    }

    private ChannelUserRole GetStandardChannelSelfRoles(string channelId)
    {
        if (string.IsNullOrWhiteSpace(_apiController.UID))
        {
            return ChannelUserRole.None;
        }

        return GetStandardChannelMemberRoles(channelId, _apiController.UID);
    }

    private bool CanEditStandardChannelTopic(string channelId)
    {
        var roles = GetStandardChannelSelfRoles(channelId);
        return GetChannelRoleRank(roles) >= GetChannelRoleRank(ChannelUserRole.HalfOperator);
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

    private List<(string Id, string Name)> GetJoinedStandardChannels()
    {
        return _standardChannels
            .Where(channel => _joinedStandardChannels.Contains(channel.ChannelId))
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

    private string GetChannelDisplayLabel(ChatChannelKey key)
    {
        if (key.Kind == ChannelKind.Syncshell)
        {
            var group = GetGroupData(key.Id);
            return group != null ? GetChannelName(group) : key.Id;
        }

        if (key.Kind == ChannelKind.Standard)
        {
            return GetStandardChannel(key.Id)?.Name ?? key.Id;
        }

        return GetUserDisplayName(new UserData(key.Id));
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
            return string.Format(CultureInfo.InvariantCulture, "# {0}{1}", name, privateSuffix);
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
            _isEditingStandardChannelTopic = false;
            if (_joinedStandardChannels.Contains(key.Id))
            {
                _ = RefreshStandardChannelMembers(key.Id);
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

        if (!_joinedStandardChannels.Contains(channel.ChannelId))
        {
            ImGui.Separator();
            UiSharedService.ColorTextWrapped("Join this channel to view members.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        ImGui.Separator();
        DrawStandardChannelMembers(channel.ChannelId);
    }

    private void DrawStandardChannelMembers(string channelId)
    {
        if (!_standardChannelMembers.TryGetValue(channelId, out var members) || members.Count == 0)
        {
            UiSharedService.ColorTextWrapped("No members loaded.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        var selfRoles = GetStandardChannelSelfRoles(channelId);
        var selfRank = GetChannelRoleRank(selfRoles);

        foreach (var member in members
                     .GroupBy(member => member.User.UID)
                     .Select(group => group.OrderByDescending(entry => GetChannelRoleRank(entry.Roles)).First())
                     .OrderByDescending(member => GetChannelRoleRank(member.Roles))
                     .ThenBy(member => GetUserDisplayName(member.User), StringComparer.OrdinalIgnoreCase))
        {
            var label = GetChannelRolePrefix(member.Roles) + GetUserDisplayName(member.User);
            ImGui.PushID(member.User.UID);
            ImGui.Selectable(label);
            DrawStandardChannelMemberContextMenu(channelId, member, selfRoles, selfRank);
            ImGui.PopID();
        }
    }

    private void DrawStandardChannelMemberContextMenu(string channelId, ChannelMemberDto member, ChannelUserRole selfRoles, int selfRank)
    {
        if (!ImGui.BeginPopupContextItem("##StandardChannelMemberMenu"))
        {
            return;
        }

        var isSelf = string.Equals(member.User.UID, _apiController.UID, StringComparison.Ordinal);
        var memberRank = GetChannelRoleRank(member.Roles);
        var canModerate = selfRank >= GetChannelRoleRank(ChannelUserRole.HalfOperator);
        var canAssignRoles = selfRank >= GetChannelRoleRank(ChannelUserRole.Operator);
        var canModerateTarget = !isSelf && selfRank > memberRank;
        var hasActions = false;

        if (canModerate && canModerateTarget)
        {
            if (ImGui.MenuItem("Kick"))
            {
                _ = KickStandardChannelMember(channelId, member.User);
            }

            if (ImGui.MenuItem("Ban"))
            {
                _ = BanStandardChannelMember(channelId, member.User);
            }

            hasActions = true;
        }

        if (canAssignRoles && canModerateTarget)
        {
            if (ImGui.BeginMenu("Roles"))
            {
                DrawStandardChannelRoleToggle(channelId, member, ChannelUserRole.Voice, selfRank);
                DrawStandardChannelRoleToggle(channelId, member, ChannelUserRole.HalfOperator, selfRank);
                DrawStandardChannelRoleToggle(channelId, member, ChannelUserRole.Operator, selfRank);
                DrawStandardChannelRoleToggle(channelId, member, ChannelUserRole.Admin, selfRank);
                DrawStandardChannelRoleToggle(channelId, member, ChannelUserRole.Owner, selfRank);
                ImGui.EndMenu();
            }

            hasActions = true;
        }

        if (!hasActions)
        {
            UiSharedService.ColorTextWrapped("No actions available.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        ImGui.EndPopup();
    }

    private void DrawStandardChannelRoleToggle(string channelId, ChannelMemberDto member, ChannelUserRole role, int selfRank)
    {
        var roleRank = GetChannelRoleRank(role);
        if (roleRank <= 0 || selfRank < roleRank)
        {
            return;
        }

        var hasRole = member.Roles.HasFlag(role);
        if (ImGui.MenuItem(role.ToString(), string.Empty, hasRole))
        {
            var newRoles = hasRole ? member.Roles & ~role : member.Roles | role;
            _ = UpdateStandardChannelRole(channelId, member.User, newRoles);
        }

        if (role == ChannelUserRole.Owner && !hasRole && ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip("Warning: assigning Owner will transfer channel ownership.");
        }
    }

    private void DrawStandardChannelLogHint(ChatChannelKey key)
    {
        if (!_joinedStandardChannels.Contains(key.Id))
        {
            UiSharedService.ColorTextWrapped("Join this channel to receive messages.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }
    }

    private async Task RefreshStandardChannels()
    {
        if (!_apiController.IsConnected) return;

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
            MergeSavedStandardChannels();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh standard channels.");
        }
    }

    private void ClearStandardChannels()
    {
        _standardChannels.Clear();
        _standardChannelLookup.Clear();
        _standardChannelMembers.Clear();
        LoadSavedStandardChannels();
    }

    private void LoadSavedStandardChannels()
    {
        _joinedStandardChannels.Clear();
        foreach (var channel in _serverManager.GetJoinedStandardChannels())
        {
            _joinedStandardChannels.Add(channel.ChannelId);
            if (!_standardChannelLookup.ContainsKey(channel.ChannelId))
            {
                _standardChannels.Add(channel);
                _standardChannelLookup[channel.ChannelId] = channel;
            }
        }
    }

    private void MergeSavedStandardChannels()
    {
        foreach (var channel in _serverManager.GetJoinedStandardChannels())
        {
            if (!_standardChannelLookup.ContainsKey(channel.ChannelId))
            {
                _standardChannels.Add(channel);
                _standardChannelLookup[channel.ChannelId] = channel;
            }
        }

        _joinedStandardChannels.Clear();
        foreach (var channel in _serverManager.GetJoinedStandardChannels())
        {
            _joinedStandardChannels.Add(channel.ChannelId);
        }
    }

    private void TrackStandardChannel(ChatChannelData channel)
    {
        _standardChannels.RemoveAll(existing => string.Equals(existing.ChannelId, channel.ChannelId, StringComparison.Ordinal));
        _standardChannels.Add(channel);
        _standardChannelLookup[channel.ChannelId] = channel;
    }

    private void UpdateJoinedStandardChannel(ChatChannelData channel, bool isJoined)
    {
        if (isJoined)
        {
            _joinedStandardChannels.Add(channel.ChannelId);
            _serverManager.UpsertJoinedStandardChannel(channel);
        }
        else
        {
            _joinedStandardChannels.Remove(channel.ChannelId);
            _serverManager.RemoveJoinedStandardChannel(channel.ChannelId);
        }
    }

    private void OnStandardChannelMembershipChanged(StandardChannelMembershipChangedMessage message)
    {
        if (message.IsJoined)
        {
            TrackStandardChannel(message.Channel);
            UpdateJoinedStandardChannel(message.Channel, true);
        }
        else
        {
            UpdateJoinedStandardChannel(message.Channel, false);
            _standardChannelMembers.Remove(message.Channel.ChannelId);
        }
    }

    private async Task AutoJoinStandardChannels()
    {
        if (!_apiController.IsConnected) return;

        foreach (var channel in _serverManager.GetJoinedStandardChannels())
        {
            await JoinStandardChannel(channel).ConfigureAwait(false);
        }
    }

    private async Task RefreshStandardChannelMembers(string channelId)
    {
        if (!_apiController.IsConnected) return;

        var channel = GetStandardChannel(channelId);
        if (channel == null) return;

        try
        {
            var members = await _apiController.ChannelGetMembers(new ChannelDto(channel)).ConfigureAwait(false);
            _standardChannelMembers[channelId] = members;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh standard channel members.");
        }
    }

    private async Task KickStandardChannelMember(string channelId, UserData user)
    {
        if (!_apiController.IsConnected) return;

        var channel = GetStandardChannel(channelId);
        if (channel == null) return;

        try
        {
            await _apiController.ChannelKick(new ChannelKickDto(channel, user)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kick channel member.");
        }
    }

    private async Task BanStandardChannelMember(string channelId, UserData user)
    {
        if (!_apiController.IsConnected) return;

        var channel = GetStandardChannel(channelId);
        if (channel == null) return;

        try
        {
            await _apiController.ChannelBan(new ChannelBanDto(channel, user, 0)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ban channel member.");
        }
    }

    private async Task UpdateStandardChannelRole(string channelId, UserData user, ChannelUserRole roles)
    {
        if (!_apiController.IsConnected) return;

        var channel = GetStandardChannel(channelId);
        if (channel == null) return;

        try
        {
            await _apiController.ChannelSetRole(new ChannelRoleUpdateDto(channel, user, roles)).ConfigureAwait(false);
            ApplyStandardChannelRoleUpdate(channelId, user.UID, roles);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update channel member role.");
        }
    }

    private void ApplyStandardChannelRoleUpdate(string channelId, string uid, ChannelUserRole roles)
    {
        if (!_standardChannelMembers.TryGetValue(channelId, out var members)) return;

        for (var i = 0; i < members.Count; i++)
        {
            if (string.Equals(members[i].User.UID, uid, StringComparison.Ordinal))
            {
                members[i] = members[i] with { Roles = roles };
                break;
            }
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
                TrackStandardChannel(member.Channel);
                _ = RefreshStandardChannelMembers(member.Channel.ChannelId);
                Mediator.Publish(new StandardChannelMembershipChangedMessage(member.Channel, true));
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
            _standardChannelMembers.Remove(channel.ChannelId);
            Mediator.Publish(new StandardChannelMembershipChangedMessage(channel, false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to leave standard channel.");
        }
    }

    private async Task UpdateStandardChannelTopic(ChatChannelData channel, string? topic)
    {
        if (!_apiController.IsConnected) return;
        if (!CanEditStandardChannelTopic(channel.ChannelId)) return;

        try
        {
            var trimmedTopic = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
            await _apiController.ChannelSetTopic(new ChannelTopicUpdateDto(channel, trimmedTopic))
                .ConfigureAwait(false);
            channel.Topic = trimmedTopic;
            TrackStandardChannel(channel);
            if (_joinedStandardChannels.Contains(channel.ChannelId))
            {
                _serverManager.UpsertJoinedStandardChannel(channel);
            }
            _standardChannelTopicDraft = trimmedTopic ?? string.Empty;
            _isEditingStandardChannelTopic = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update standard channel topic.");
        }
    }
}
