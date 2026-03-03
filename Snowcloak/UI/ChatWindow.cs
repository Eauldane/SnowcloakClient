using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Chat;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
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

    private sealed record ChatLine(DateTime Timestamp, string SenderUid, string Sender, string Message, Vector4? SenderColor, Vector4? SenderGlowColor);

    private readonly ApiController _apiController;
    private readonly ChatService _chatService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly SnowcloakConfigService _configService;
    private readonly PairManager _pairManager;
    private readonly ServerConfigurationManager _serverManager;
    private readonly Dictionary<ChatChannelKey, List<ChatLine>> _channelLogs = [];
    private readonly List<ChatChannelData> _standardChannels = [];
    private readonly Dictionary<string, ChatChannelData> _standardChannelLookup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _joinedStandardChannels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChannelMemberDto>> _standardChannelMembers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, UserData>> _syncshellChatMembers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _joinedSyncshellChats = new(StringComparer.Ordinal);
    private ChatChannelKey? _selectedChannel;
    private readonly HashSet<string> _pinnedDirectChannels = new(StringComparer.Ordinal);
    private string _pendingMessage = string.Empty;
    private bool _autoScroll = true;
    private readonly HashSet<ChatChannelKey> _unreadChannels = [];
    private readonly HashSet<string> _loadedDirectHistory = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadedSyncshellHistory = new(StringComparer.Ordinal);
    private string _standardChannelTopicDraft = string.Empty;
    private bool _isEditingStandardChannelTopic;

    public ChatWindow(ILogger<ChatWindow> logger, SnowMediator mediator, ApiController apiController,
        PairManager pairManager, ServerConfigurationManager serverManager, PerformanceCollectorService performanceCollectorService,
        ChatService chatService, DalamudUtilService dalamudUtil, SnowcloakConfigService configService)
        : base(logger, mediator, "Snowcloak Chat###SnowcloakChatWindow", performanceCollectorService)
    {
        _apiController = apiController;
        _chatService = chatService;
        _dalamudUtil = dalamudUtil;
        _configService = configService;
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
        Mediator.Subscribe<GroupChatMemberStateMessage>(this, message => HandleSyncshellChatMemberState(message.MemberState));
        Mediator.Subscribe<ClearProfileDataMessage>(this, message => HandleUserProfileUpdate(message.UserData));
        Mediator.Subscribe<ConnectedMessage>(this, _message =>
        {
            _ = RefreshStandardChannels();
            _ = AutoJoinStandardChannels();
            _ = AutoJoinSyncshellChats();
        });
        Mediator.Subscribe<DisconnectedMessage>(this, message =>
        {
            ResetChatState();
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

        var buttonAreaHeight = ImGui.GetFrameHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y;
        var listHeight = Math.Max(0f, ImGui.GetContentRegionAvail().Y - buttonAreaHeight - ImGui.GetStyle().ItemSpacing.Y);

        using (var list = ImRaii.Child("ChannelListContent", new Vector2(-1, listHeight), false))
        {
            if (list.Success)
            {
                DrawStandardChannelSection();
                ImGui.Separator();
                DrawChannelSection("Syncshells", ChannelKind.Syncshell, GetSyncshellChannels());
                ImGui.Separator();
                DrawChannelSection("Direct Messages", ChannelKind.Direct, GetDirectChannels());
            }
        }

        ImGui.Separator();
        DrawStandardChannelButtons();
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
                    ElezenImgui.ColouredWrappedText("No joined standard channels.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                }
                else
                {
                    foreach (var channel in channels)
                    {
                        var key = new ChatChannelKey(ChannelKind.Standard, channel.Id);
                        var displayName = GetChannelDisplayName(ChannelKind.Standard, channel.Id, channel.Name);
                        var isSelected = _selectedChannel.HasValue && _selectedChannel.Value.Equals(key);
                        var unread = _unreadChannels.Contains(key);
                        using var unreadStyle = ImRaii.PushColor(ImGuiCol.Text, GetUnreadChannelColor(), unread);

                        if (ImGui.Selectable(displayName, isSelected))
                        {
                            SetSelectedChannel(key);
                        }
                    }
                }
            }
        }

        var joinedChannels = GetJoinedStandardChannels();
        if (_selectedChannel == null && joinedChannels.Count > 0)
        {
            var first = joinedChannels[0];
            SetSelectedChannel(new ChatChannelKey(ChannelKind.Standard, first.Id));
        }
    }

    private void DrawStandardChannelButtons()
    {
        var buttonWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Browse Channels", new Vector2(buttonWidth, 0)))
        {
            Mediator.Publish(new UiToggleMessage(typeof(StandardChannelDirectoryWindow)));
        }

        if (ImGui.Button("Create Channel", new Vector2(buttonWidth, 0)))
        {
            Mediator.Publish(new UiToggleMessage(typeof(StandardChannelCreateWindow)));
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
                var unread = _unreadChannels.Contains(key);
                using var unreadStyle = ImRaii.PushColor(ImGuiCol.Text, GetUnreadChannelColor(), unread);

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
            ElezenImgui.ColouredWrappedText("Select a channel to start chatting.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
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

        var channelLabel = string.Empty;
        if (key.Kind == ChannelKind.Syncshell || key.Kind == ChannelKind.Standard)
        {
            channelLabel = GetChannelDisplayName(key.Kind, key.Id, GetChannelDisplayLabel(key));
        }

        ImGui.PushTextWrapPos(0f);
        foreach (var entry in log)
        {
            var timestamp = entry.Timestamp.ToString("HH:mm", CultureInfo.InvariantCulture);
            ImGui.TextUnformatted(string.Format(CultureInfo.InvariantCulture, "[{0}] ", timestamp));
            ImGui.SameLine(0f, 0f);
            DrawTextWithOptionalColor(entry.Sender, entry.SenderColor, entry.SenderGlowColor);

            ImGui.SameLine(0f, 0f);
            ImGui.TextUnformatted(": ");
            ImGui.SameLine(0f, 0f);
            ImGui.TextWrapped(entry.Message);
        }
        ImGui.PopTextWrapPos();

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

            ElezenImgui.ColouredWrappedText(topic, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

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
        else if (key.Kind == ChannelKind.Syncshell)
        {
            var shellConfig = _serverManager.GetShellConfigForGid(key.Id);
            using (ImRaii.Disabled(!_apiController.IsConnected))
            {
                if (shellConfig.Enabled)
                {
                    if (ImGui.Button("Leave Chat"))
                    {
                        shellConfig.Enabled = false;
                        _serverManager.SaveShellConfigForGid(key.Id, shellConfig);
                        _joinedSyncshellChats.Remove(key.Id);
                        _syncshellChatMembers.Remove(key.Id);
                        _loadedSyncshellHistory.Remove(key.Id);
                        var group = GetGroupData(key.Id);
                        if (group != null)
                        {
                            _ = _apiController.GroupChatLeave(new GroupDto(group));
                        }
                        _selectedChannel = null;
                    }
                }
                else
                {
                    if (ImGui.Button("Join Chat"))
                    {
                        shellConfig.Enabled = true;
                        _serverManager.SaveShellConfigForGid(key.Id, shellConfig);
                        _joinedSyncshellChats.Add(key.Id);
                        var group = GetGroupData(key.Id);
                        if (group != null)
                        {
                            _ = _apiController.GroupChatJoin(new GroupDto(group));
                            _ = RefreshSyncshellChatMembers(group.GID);
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
            ElezenImgui.ColouredWrappedText("No channel selected.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
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
            ElezenImgui.ColouredWrappedText("Unknown syncshell.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        if (!_syncshellChatMembers.TryGetValue(key.Id, out var members) || members.Count == 0)
        {
            var message = _joinedSyncshellChats.Contains(key.Id)
                ? "No chat members loaded."
                : "Join this chat to view members.";
            ElezenImgui.ColouredWrappedText(message, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        foreach (var entry in members.Values
                     .GroupBy(user => user.UID)
                     .Select(group => group.First())
                     .Select(user => (User: user, Rank: GetSyncshellChatMemberRank(groupInfo, user)))
                     .OrderByDescending(member => member.Rank)
                     .ThenBy(member => GetUserDisplayName(member.User), StringComparer.OrdinalIgnoreCase))
        {
            var prefix = GetRolePrefix(groupInfo, entry.User);
            var label = prefix + GetUserDisplayName(entry.User);
            DrawTextWithOptionalColor(label, TryGetVanityColor(entry.User.DisplayColour), TryGetVanityColor(entry.User.DisplayGlowColour));
        }
    }

    private void DrawDirectMembers(ChatChannelKey key)
    {
        var userData = GetUserData(key.Id);
        if (userData != null)
        {
            DrawTextWithOptionalColor(GetUserDisplayName(userData), TryGetVanityColor(userData.DisplayColour), TryGetVanityColor(userData.DisplayGlowColour));
        }

        if (!string.IsNullOrWhiteSpace(_apiController.UID))
        {
            var selfName = GetUserDisplayName(new UserData(_apiController.UID, _apiController.VanityId, _apiController.DisplayColour, _apiController.DisplayGlowColour));
            var selfColor = TryGetVanityColor(_apiController.DisplayColour);
            var selfGlowColor = TryGetVanityColor(_apiController.DisplayGlowColour);
            ImGui.TextDisabled("You (");
            ImGui.SameLine(0f, 0f);
            DrawTextWithOptionalColor(selfName, selfColor, selfGlowColor);

            ImGui.SameLine(0f, 0f);
            ImGui.TextDisabled(")");
        }
    }

    private void DrawChatInput()
    {
        var canSend = _selectedChannel != null
                      && !_configService.Current.DisableChat
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
                _ = SendMessageAsync(_selectedChannel.Value, _pendingMessage);
            }
        }

        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
    }

    private ChatMessage BuildOutgoingChatMessage(string message)
    {
        var senderName = _dalamudUtil.GetPlayerName();
        var senderHomeWorld = _dalamudUtil.GetHomeWorldId();
        return new ChatMessage
        {
            SenderName = senderName,
            SenderHomeWorldId = senderHomeWorld,
            PayloadContent = Encoding.UTF8.GetBytes(message)
        };
    }

    private async Task SendMessageAsync(ChatChannelKey channel, string message)
    {
        if (_configService.Current.DisableChat) return;
        if (string.IsNullOrWhiteSpace(message)) return;

        var originalMessage = message;
        var trimmed = message.Trim();
        var chatMessage = BuildOutgoingChatMessage(trimmed);

        try
        {
            if (channel.Kind == ChannelKind.Syncshell)
            {
                var group = GetGroupData(channel.Id);
                if (group == null) return;
                await _apiController.GroupChatSendMsg(new GroupDto(group), chatMessage).ConfigureAwait(false);
                _chatService.PrintLocalGroupChat(group, chatMessage.PayloadContent);
            }
            else if (channel.Kind == ChannelKind.Standard)
            {
                if (!_joinedStandardChannels.Contains(channel.Id)) return;
                var standardChannel = GetStandardChannel(channel.Id);
                if (standardChannel == null) return;
                await _apiController.ChannelChatSendMsg(new ChannelDto(standardChannel), chatMessage).ConfigureAwait(false);
            }
            else
            {
                var userData = GetUserData(channel.Id);
                if (userData == null) return;
                await _apiController.UserChatSendMsg(new UserDto(userData), chatMessage).ConfigureAwait(false);
                _chatService.PrintLocalUserChat(chatMessage.PayloadContent);
                _pinnedDirectChannels.Add(channel.Id);
            }

            AddLocalMessage(channel, trimmed);
            if (string.Equals(_pendingMessage, originalMessage, StringComparison.Ordinal))
            {
                _pendingMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send chat message for {ChannelKind}:{ChannelId}", channel.Kind, channel.Id);
            Mediator.Publish(new NotificationMessage(
                "Chat send failed",
                $"Could not send message: {ex.Message}",
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
        }
    }

    private void AddLocalMessage(ChatChannelKey channel, string message)
    {
        var selfUser = new UserData(_apiController.UID, _apiController.VanityId, _apiController.DisplayColour, _apiController.DisplayGlowColour);
        var displayName = FormatSenderName(channel, selfUser);
        AppendMessage(channel, selfUser.UID, displayName, message, DateTime.Now, markUnread: false,
            senderColor: TryGetVanityColor(_apiController.DisplayColour),
            senderGlowColor: TryGetVanityColor(_apiController.DisplayGlowColour));
    }

    private void AddGroupMessage(GroupChatMsgMessage message)
    {
        if (_configService.Current.DisableChat) return;

        var shellConfig = _serverManager.GetShellConfigForGid(message.GroupInfo.GID);
        if (!shellConfig.Enabled)
        {
            return;
        }

        var channel = new ChatChannelKey(ChannelKind.Syncshell, message.GroupInfo.GID);
        var text = DecodeMessage(message.ChatMsg.PayloadContent);
        var displayName = FormatSenderName(channel, message.ChatMsg.Sender);
        var senderColors = ResolveSenderColors(channel, message.ChatMsg.Sender);
        AppendMessage(channel, message.ChatMsg.Sender.UID, displayName, text, ResolveTimestamp(message.ChatMsg.Timestamp), markUnread: true,
            senderColor: senderColors.Foreground,
            senderGlowColor: senderColors.Glow);
    }

    private void AddDirectMessage(SignedChatMessage message)
    {
        if (_configService.Current.DisableChat) return;

        var channel = new ChatChannelKey(ChannelKind.Direct, message.Sender.UID);
        var text = DecodeMessage(message.PayloadContent);
        var displayName = FormatSenderName(channel, message.Sender);
        var senderColors = ResolveSenderColors(channel, message.Sender);
        AppendMessage(channel, message.Sender.UID, displayName, text, ResolveTimestamp(message.Timestamp), markUnread: true,
            senderColor: senderColors.Foreground,
            senderGlowColor: senderColors.Glow);
        _pinnedDirectChannels.Add(channel.Id);

    }

    private void AddStandardChannelMessage(ChannelChatMsgMessage message)
    {
        if (_configService.Current.DisableChat) return;

        var channelData = message.ChannelInfo.Channel;
        TrackStandardChannel(channelData);
        if (!_standardChannelMembers.ContainsKey(channelData.ChannelId))
        {
            _ = RefreshStandardChannelMembers(channelData.ChannelId);
        }
        var channel = new ChatChannelKey(ChannelKind.Standard, channelData.ChannelId);
        var text = DecodeMessage(message.ChatMsg.PayloadContent);
        var displayName = FormatSenderName(channel, message.ChatMsg.Sender);
        var senderColors = ResolveSenderColors(channel, message.ChatMsg.Sender);
        AppendMessage(channel, message.ChatMsg.Sender.UID, displayName, text, ResolveTimestamp(message.ChatMsg.Timestamp), markUnread: true,
            senderColor: senderColors.Foreground,
            senderGlowColor: senderColors.Glow);
    }

    private void HandleStandardChannelMemberJoined(ChannelMemberJoinedDto member)
    {
        TrackStandardChannel(member.Channel);
        var isSelf = string.Equals(member.User.UID, _apiController.UID, StringComparison.Ordinal);
        if (isSelf && !_joinedStandardChannels.Contains(member.Channel.ChannelId))
        {
            Mediator.Publish(new StandardChannelMembershipChangedMessage(member.Channel, true));
        }
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
        var isSelf = string.Equals(member.User.UID, _apiController.UID, StringComparison.Ordinal);
        if (isSelf && _joinedStandardChannels.Contains(member.Channel.ChannelId))
        {
            Mediator.Publish(new StandardChannelMembershipChangedMessage(member.Channel, false));
        }
        if (_standardChannelMembers.TryGetValue(member.Channel.ChannelId, out var members))
        {
            members.RemoveAll(entry => string.Equals(entry.User.UID, member.User.UID, StringComparison.Ordinal));
        }
        else if (_joinedStandardChannels.Contains(member.Channel.ChannelId))
        {
            _ = RefreshStandardChannelMembers(member.Channel.ChannelId);
        }
    }

    private void HandleSyncshellChatMemberState(GroupChatMemberStateDto memberState)
    {
        var gid = memberState.Group.GID;
        var isSelf = string.Equals(memberState.User.UID, _apiController.UID, StringComparison.Ordinal);
        if (isSelf)
        {
            if (memberState.IsJoined)
            {
                var groupInfo = GetGroupInfo(gid);
                if (!_serverManager.HasShellConfigForGid(gid)
                    && (groupInfo == null || !string.Equals(groupInfo.OwnerUID, _apiController.UID, StringComparison.Ordinal)))
                {
                    var shellConfig = _serverManager.GetShellConfigForGid(gid);
                    shellConfig.Enabled = false;
                    _serverManager.SaveShellConfigForGid(gid, shellConfig);
                }

                var config = _serverManager.GetShellConfigForGid(gid);
                if (!config.Enabled)
                {
                    _joinedSyncshellChats.Remove(gid);
                    _syncshellChatMembers.Remove(gid);
                    _loadedSyncshellHistory.Remove(gid);
                    _ = _apiController.GroupChatLeave(memberState.Group);
                    return;
                }

                _joinedSyncshellChats.Add(gid);
                _ = LoadSyncshellHistory(gid);
            }
            else
            {
                _joinedSyncshellChats.Remove(gid);
                _syncshellChatMembers.Remove(gid);
                _loadedSyncshellHistory.Remove(gid);
            }
        }

        if (!_joinedSyncshellChats.Contains(gid))
        {
            return;
        }

        if (!_syncshellChatMembers.TryGetValue(gid, out var members))
        {
            members = new Dictionary<string, UserData>(StringComparer.Ordinal);
            _syncshellChatMembers[gid] = members;
        }

        if (memberState.IsJoined)
        {
            members[memberState.User.UID] = memberState.User;
        }
        else
        {
            members.Remove(memberState.User.UID);
        }
    }

    private void AppendMessage(ChatChannelKey channel, string senderUid, string sender, string message, DateTime timestamp, bool markUnread,
        Vector4? senderColor = null, Vector4? senderGlowColor = null)
    {
        if (!_channelLogs.TryGetValue(channel, out var log))
        {
            log = [];
            _channelLogs[channel] = log;
        }

        log.Add(new ChatLine(timestamp, senderUid, sender, message, senderColor, senderGlowColor));
        if (markUnread && (!_selectedChannel.HasValue || !_selectedChannel.Value.Equals(channel)))
        {
            _unreadChannels.Add(channel);
        }

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
            return DateTime.Now;
        }

        return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
    }

    private string GetUserDisplayName(UserData user)
    {
        var note = _serverManager.GetNoteForUid(user.UID);
        if (!string.IsNullOrWhiteSpace(note)) return note;
        if (!string.IsNullOrWhiteSpace(user.Alias)) return user.Alias!;
        return user.UID;
    }

    private static Vector4? TryGetVanityColor(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return null;
        }

        try
        {
            return ElezenTools.UI.Colour.HexToVector4(hexColor);
        }
        catch
        {
            return null;
        }
    }

    private void HandleUserProfileUpdate(UserData? updatedUser)
    {
        if (updatedUser == null || string.IsNullOrWhiteSpace(updatedUser.UID))
        {
            return;
        }

        var uid = updatedUser.UID;
        foreach (var channelMembers in _standardChannelMembers.Values)
        {
            for (int i = 0; i < channelMembers.Count; i++)
            {
                if (!string.Equals(channelMembers[i].User.UID, uid, StringComparison.Ordinal))
                {
                    continue;
                }

                channelMembers[i] = new ChannelMemberDto(channelMembers[i].Channel, updatedUser, channelMembers[i].Roles);
            }
        }

        foreach (var syncshellMembers in _syncshellChatMembers.Values)
        {
            if (syncshellMembers.ContainsKey(uid))
            {
                syncshellMembers[uid] = updatedUser;
            }
        }

        RefreshCachedSenderColours(updatedUser);
    }

    private (Vector4? Foreground, Vector4? Glow) ResolveSenderColors(ChatChannelKey channel, UserData sender)
    {
        if (string.Equals(sender.UID, _apiController.UID, StringComparison.Ordinal))
        {
            return (TryGetVanityColor(_apiController.DisplayColour), TryGetVanityColor(_apiController.DisplayGlowColour));
        }

        var pairUserData = _pairManager.GetPairByUID(sender.UID)?.UserData;
        if (pairUserData != null)
        {
            var pairColor = TryGetVanityColor(pairUserData.DisplayColour);
            var pairGlowColor = TryGetVanityColor(pairUserData.DisplayGlowColour);
            if (pairColor.HasValue || pairGlowColor.HasValue)
            {
                return (pairColor, pairGlowColor);
            }
        }

        var senderColor = TryGetVanityColor(sender.DisplayColour);
        var senderGlowColor = TryGetVanityColor(sender.DisplayGlowColour);
        if (senderColor.HasValue || senderGlowColor.HasValue)
        {
            return (senderColor, senderGlowColor);
        }

        if (channel.Kind == ChannelKind.Standard
            && _standardChannelMembers.TryGetValue(channel.Id, out var standardMembers))
        {
            var standardMember = standardMembers.FirstOrDefault(entry => string.Equals(entry.User.UID, sender.UID, StringComparison.Ordinal));
            var standardColor = TryGetVanityColor(standardMember?.User.DisplayColour);
            var standardGlowColor = TryGetVanityColor(standardMember?.User.DisplayGlowColour);
            if (standardColor.HasValue || standardGlowColor.HasValue)
            {
                return (standardColor, standardGlowColor);
            }
        }

        if (channel.Kind == ChannelKind.Syncshell
            && _syncshellChatMembers.TryGetValue(channel.Id, out var syncshellMembers)
            && syncshellMembers.TryGetValue(sender.UID, out var syncshellMember))
        {
            var syncshellColor = TryGetVanityColor(syncshellMember.DisplayColour);
            var syncshellGlowColor = TryGetVanityColor(syncshellMember.DisplayGlowColour);
            if (syncshellColor.HasValue || syncshellGlowColor.HasValue)
            {
                return (syncshellColor, syncshellGlowColor);
            }
        }

        return (null, null);
    }

    private void RefreshCachedSenderColours(UserData updatedUser)
    {
        foreach (var (channel, log) in _channelLogs)
        {
            if (log.Count == 0)
            {
                continue;
            }

            var displayName = FormatSenderName(channel, updatedUser);
            var colors = ResolveSenderColors(channel, updatedUser);
            for (int i = 0; i < log.Count; i++)
            {
                var uidMatches = string.Equals(log[i].SenderUid, updatedUser.UID, StringComparison.Ordinal);
                var fallbackSenderMatches = string.IsNullOrWhiteSpace(log[i].SenderUid)
                    && string.Equals(log[i].Sender, displayName, StringComparison.Ordinal);
                if (!uidMatches && !fallbackSenderMatches)
                {
                    continue;
                }

                log[i] = log[i] with { Sender = displayName, SenderColor = colors.Foreground, SenderGlowColor = colors.Glow };
            }
        }
    }

    private static void DrawTextWithOptionalColor(string text, Vector4? color, Vector4? glowColor = null)
    {
        if (!color.HasValue && !glowColor.HasValue)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        var foreground = color ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (glowColor.HasValue)
        {
            var drawList = ImGui.GetWindowDrawList();
            var textPos = ImGui.GetCursorScreenPos();
            var glow = glowColor.Value;
            var glowAlpha = Math.Clamp(glow.W <= 0f ? 0.45f : glow.W, 0.05f, 1f);
            var glowU32 = ImGui.ColorConvertFloat4ToU32(new Vector4(glow.X, glow.Y, glow.Z, glowAlpha));
            var spread = 1.0f * ImGuiHelpers.GlobalScale;
            drawList.AddText(new Vector2(textPos.X - spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X + spread, textPos.Y), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y - spread), glowU32, text);
            drawList.AddText(new Vector2(textPos.X, textPos.Y + spread), glowU32, text);
        }

        ImGui.TextColored(foreground, text);
    }

    private static void DrawSelectableWithOptionalColor(string label, Vector4? color)
    {
        if (color.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
            ImGui.Selectable(label);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Selectable(label);
        }
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
        return GetChannelRoleRank(roles) >= GetChannelRoleRank(ChannelUserRole.Operator);
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
            .Where(group => _serverManager.GetShellConfigForGid(group.GID).Enabled)
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
            .Where(pair => pair.IsOnline || _pinnedDirectChannels.Contains(pair.UserData.UID))
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
        _unreadChannels.Remove(key);

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

        if (key.Kind == ChannelKind.Direct)
        {
            _ = LoadDirectHistory(key.Id);
        }
        else if (key.Kind == ChannelKind.Syncshell)
        {
            _ = LoadSyncshellHistory(key.Id);
        }
    }

    private void DrawStandardChannelDetails(ChatChannelKey key)
    {
        var channel = GetStandardChannel(key.Id);
        if (channel == null)
        {
            ElezenImgui.ColouredWrappedText("Unknown channel.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        ImGui.TextUnformatted(channel.Name);
        ElezenImgui.ColouredWrappedText(channel.IsPrivate ? "Private channel" : "Public channel", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

        if (!_joinedStandardChannels.Contains(channel.ChannelId))
        {
            ImGui.Separator();
            ElezenImgui.ColouredWrappedText("Join this channel to view members.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            return;
        }

        ImGui.Separator();
        DrawStandardChannelMembers(channel.ChannelId);
    }

    private void DrawStandardChannelMembers(string channelId)
    {
        if (!_standardChannelMembers.TryGetValue(channelId, out var members) || members.Count == 0)
        {
            ElezenImgui.ColouredWrappedText("No members loaded.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
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
            DrawSelectableWithOptionalColor(label, TryGetVanityColor(member.User.DisplayColour));
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
        var canKick = selfRank >= GetChannelRoleRank(ChannelUserRole.HalfOperator);
        var canBan = selfRank >= GetChannelRoleRank(ChannelUserRole.Operator);
        var canAssignRoles = selfRank >= GetChannelRoleRank(ChannelUserRole.Admin);
        var canModerateTarget = !isSelf && selfRank > memberRank;
        var hasActions = false;

        if (canModerateTarget && (canKick || canBan))
        {
            if (canKick && ImGui.MenuItem("Kick"))
            {
                _ = KickStandardChannelMember(channelId, member.User);
            }

            if (canBan && ImGui.MenuItem("Ban"))
            {
                _ = BanStandardChannelMember(channelId, member.User);
            }

            hasActions = true;
        }

        if (canAssignRoles && canModerateTarget)
        {
            if (ImGui.BeginMenu("Roles"))
            {
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.None, selfRoles);
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.Voice, selfRoles);
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.HalfOperator, selfRoles);
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.Operator, selfRoles);
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.Admin, selfRoles);
                DrawStandardChannelRoleOption(channelId, member, ChannelUserRole.Owner, selfRoles);
                ImGui.EndMenu();
            }

            hasActions = true;
        }

        if (!hasActions)
        {
            ElezenImgui.ColouredWrappedText("No actions available.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }

        ImGui.EndPopup();
    }

    private static ChannelUserRole NormalizeChannelRole(ChannelUserRole roles)
    {
        if (roles.HasFlag(ChannelUserRole.Owner)) return ChannelUserRole.Owner;
        if (roles.HasFlag(ChannelUserRole.Admin)) return ChannelUserRole.Admin;
        if (roles.HasFlag(ChannelUserRole.Operator)) return ChannelUserRole.Operator;
        if (roles.HasFlag(ChannelUserRole.HalfOperator)) return ChannelUserRole.HalfOperator;
        if (roles.HasFlag(ChannelUserRole.Voice)) return ChannelUserRole.Voice;
        return ChannelUserRole.None;
    }

    private static bool CanAssignRole(ChannelUserRole actorRoles, ChannelUserRole desiredRole)
    {
        var actor = NormalizeChannelRole(actorRoles);
        if (desiredRole == ChannelUserRole.Owner || desiredRole == ChannelUserRole.Admin)
        {
            return actor == ChannelUserRole.Owner;
        }

        if (desiredRole == ChannelUserRole.Operator || desiredRole == ChannelUserRole.HalfOperator || desiredRole == ChannelUserRole.Voice || desiredRole == ChannelUserRole.None)
        {
            return actor == ChannelUserRole.Owner || actor == ChannelUserRole.Admin;
        }

        return false;
    }

    private static string GetRoleLabel(ChannelUserRole role)
    {
        return role == ChannelUserRole.None ? "None" : role.ToString();
    }

    private void DrawStandardChannelRoleOption(string channelId, ChannelMemberDto member, ChannelUserRole role, ChannelUserRole selfRoles)
    {
        if (!CanAssignRole(selfRoles, role))
        {
            return;
        }

        var currentRole = NormalizeChannelRole(member.Roles);
        var isSelected = currentRole == role;
        if (ImGui.MenuItem(GetRoleLabel(role), string.Empty, isSelected))
        {
            if (!isSelected)
            {
                _ = UpdateStandardChannelRole(channelId, member.User, role);
            }
        }

        if (role == ChannelUserRole.Owner && !isSelected && ImGui.IsItemHovered())
        {
            UiSharedService.AttachToolTip("Warning: assigning Owner will transfer channel ownership.");
        }
    }

    private void DrawStandardChannelLogHint(ChatChannelKey key)
    {
        if (!_joinedStandardChannels.Contains(key.Id))
        {
            ElezenImgui.ColouredWrappedText("Join this channel to receive messages.", ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        }
    }

    private void AppendHistoryMessage(ChatChannelKey channel, SignedChatMessage message)
    {
        var displayName = FormatSenderName(channel, message.Sender);
        var text = DecodeMessage(message.PayloadContent);
        var timestamp = ResolveTimestamp(message.Timestamp);
        var senderColor = ResolveSenderColors(channel, message.Sender);

        if (_channelLogs.TryGetValue(channel, out var existing)
            && existing.Any(entry =>
                entry.Timestamp == timestamp
                && string.Equals(entry.Sender, displayName, StringComparison.Ordinal)
                && string.Equals(entry.Message, text, StringComparison.Ordinal)))
        {
            return;
        }

        AppendMessage(channel, message.Sender.UID, displayName, text, timestamp, markUnread: false, senderColor: senderColor.Foreground, senderGlowColor: senderColor.Glow);
    }

    private async Task LoadDirectHistory(string uid)
    {
        if (_configService.Current.DisableChat) return;
        if (!_apiController.IsConnected) return;
        if (_apiController.ServerInfo.ChatHistoryReplayDays <= 0) return;
        if (!_loadedDirectHistory.Add(uid)) return;

        try
        {
            var user = GetUserData(uid);
            if (user == null) return;

            var history = await _apiController.UserChatGetHistory(new UserDto(user)).ConfigureAwait(false);
            var channel = new ChatChannelKey(ChannelKind.Direct, uid);
            foreach (var message in history)
            {
                AppendHistoryMessage(channel, message);
            }

            if (history.Count > 0)
            {
                _pinnedDirectChannels.Add(uid);
            }
        }
        catch (Exception ex)
        {
            _loadedDirectHistory.Remove(uid);
            _logger.LogWarning(ex, "Failed to load direct chat history for {Uid}", uid);
        }
    }

    private async Task LoadSyncshellHistory(string gid)
    {
        if (_configService.Current.DisableChat) return;
        if (!_apiController.IsConnected) return;
        if (_apiController.ServerInfo.ChatHistoryReplayDays <= 0) return;
        if (!_loadedSyncshellHistory.Add(gid)) return;

        var group = GetGroupData(gid);
        if (group == null)
        {
            _loadedSyncshellHistory.Remove(gid);
            return;
        }

        try
        {
            var history = await _apiController.GroupChatGetHistory(new GroupDto(group)).ConfigureAwait(false);
            var channel = new ChatChannelKey(ChannelKind.Syncshell, gid);
            foreach (var message in history)
            {
                AppendHistoryMessage(channel, message);
            }
        }
        catch (Exception ex)
        {
            _loadedSyncshellHistory.Remove(gid);
            _logger.LogWarning(ex, "Failed to load syncshell chat history for {Gid}", gid);
        }
    }

    private async Task AutoJoinSyncshellChats()
    {
        if (!_apiController.IsConnected) return;

        foreach (var group in _pairManager.GroupPairs.Keys)
        {
          var shellConfig = _serverManager.GetShellConfigForGid(group.GID);
            if (!shellConfig.Enabled)
            {
                continue;
            }

            try
            {
                await _apiController.GroupChatJoin(new GroupDto(group.Group)).ConfigureAwait(false);
                await RefreshSyncshellChatMembers(group.GID).ConfigureAwait(false);
                await LoadSyncshellHistory(group.GID).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-join syncshell chat {Gid}", group.GID);
            }
        }
    }

    private async Task RefreshSyncshellChatMembers(string gid)
    {
        if (!_apiController.IsConnected) return;

        var group = GetGroupData(gid);
        if (group == null)
        {
            return;
        }

        try
        {
            var members = await _apiController.GroupChatGetMembers(new GroupDto(group)).ConfigureAwait(false);
            var memberMap = new Dictionary<string, UserData>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!member.IsJoined)
                {
                    continue;
                }

                memberMap[member.User.UID] = member.User;
            }

            if (memberMap.Count == 0)
            {
                _syncshellChatMembers.Remove(gid);
            }
            else
            {
                _syncshellChatMembers[gid] = memberMap;
            }

            if (memberMap.ContainsKey(_apiController.UID))
            {
                _joinedSyncshellChats.Add(gid);
            }
            else
            {
                _joinedSyncshellChats.Remove(gid);
                _loadedSyncshellHistory.Remove(gid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh syncshell chat members for {Gid}", gid);
        }
    }

    private void ClearSyncshellChatMembers()
    {
        _syncshellChatMembers.Clear();
        _joinedSyncshellChats.Clear();
        _loadedSyncshellHistory.Clear();
    }

    private int GetSyncshellChatMemberRank(GroupFullInfoDto? groupInfo, UserData user)
    {
        if (groupInfo == null)
        {
            return 1;
        }

        if (groupInfo.Owner != null && string.Equals(groupInfo.Owner.UID, user.UID, StringComparison.Ordinal))
        {
            return 3;
        }

        var pair = _pairManager.GetPairByUID(user.UID);
        if (pair != null && IsModerator(pair, groupInfo))
        {
            return 2;
        }

        return 1;
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

        // Iterate a snapshot so membership updates can safely mutate persisted channel state.
        var channelsToJoin = _serverManager.GetJoinedStandardChannels().ToList();
        foreach (var channel in channelsToJoin)
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
            Mediator.Publish(new NotificationMessage(
                "Kick failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
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
            Mediator.Publish(new NotificationMessage(
                "Ban failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
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
            await RefreshStandardChannelMembers(channelId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update channel member role.");
            Mediator.Publish(new NotificationMessage(
                "Role update failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
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
                return;
            }

            if (MaybeRemoveJoinedStandardChannel(channel))
            {
                NotifyStandardChannelJoinFailure(channel, "You may no longer have access to this channel.");
            }
        }
        catch (Exception ex)
        {
            if (IsPermanentChannelJoinFailure(ex))
            {
                _logger.LogWarning(ex, "Standard channel join failed permanently.");
                if (MaybeRemoveJoinedStandardChannel(channel))
                {
                    NotifyStandardChannelJoinFailure(channel, "This channel may no longer exist.");
                }
                return;
            }
            _logger.LogWarning(ex, "Failed to join standard channel.");
            Mediator.Publish(new NotificationMessage(
                "Channel join failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
        }
    }

    private bool MaybeRemoveJoinedStandardChannel(ChatChannelData channel)
    {
        if (!_joinedStandardChannels.Contains(channel.ChannelId)) return false;
        Mediator.Publish(new StandardChannelMembershipChangedMessage(channel, false));
        return true;
    }

    private void NotifyStandardChannelJoinFailure(ChatChannelData channel, string reason)
    {
        var channelName = string.IsNullOrWhiteSpace(channel.Name) ? channel.ChannelId : channel.Name;
        Mediator.Publish(new NotificationMessage(
            "Channel join failed",
            $"Could not rejoin '{channelName}'. {reason} It was removed from your joined channels list.",
            NotificationType.Warning,
            TimeSpan.FromSeconds(7.5)));
    }

    private static bool IsPermanentChannelJoinFailure(Exception ex)
    {
        foreach (var message in EnumerateExceptionMessages(ex))
        {
            if (message.Contains("Invalid channel payload", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Handle known and variant server texts for deleted/missing channels.
            if (message.Contains("Channel not found", StringComparison.OrdinalIgnoreCase)
                || (message.Contains("channel", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateExceptionMessages(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                yield return current.Message;
            }
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
            Mediator.Publish(new NotificationMessage(
                "Leave failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
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
            Mediator.Publish(new NotificationMessage(
                "Topic update failed",
                ex.Message,
                NotificationType.Warning,
                TimeSpan.FromSeconds(6)));
        }
    }
    
    private void ResetChatState()
    {
        _channelLogs.Clear();
        _selectedChannel = null;
        _pendingMessage = string.Empty;
        _standardChannelTopicDraft = string.Empty;
        _isEditingStandardChannelTopic = false;
        _pinnedDirectChannels.Clear();
        _loadedDirectHistory.Clear();
        _loadedSyncshellHistory.Clear();
        ClearStandardChannels();
        ClearSyncshellChatMembers();
    }

    private static Vector4 GetUnreadChannelColor()
    {
        return ImGui.GetStyle().Colors[(int)ImGuiCol.PlotHistogram];
    }

}
