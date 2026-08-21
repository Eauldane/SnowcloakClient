using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.Core.Chat;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Snowcloak.UI.Components;

public sealed class ChatConversationView
{
    private static readonly RpChatMode[] SceneChatModes = [RpChatMode.InCharacter, RpChatMode.OutOfCharacter, RpChatMode.Action];
    private readonly BackgroundTaskTracker _backgroundTasks;
    private readonly ChatClientService _chatService;
    private readonly ImGuiChatRenderer _renderer;
    private readonly SnowMediator _mediator;
    private readonly FileDialogManager _fileDialogManager;
    private readonly Dictionary<string, RpChatMode> _roomModes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<RpChatMode>> _roomModeFilters = new(StringComparer.Ordinal);
    private Task<List<RoomDicePresetDto>>? _dicePresetLoad;
    private List<RoomDicePresetDto> _dicePresets = [];
    private string _dicePresetRoomId = string.Empty;
    private string _diceExpression = "1d20";
    private string _draft = string.Empty;
    private ConversationKey? _draftKey;
    private string _commandStatus = string.Empty;
    private Task<List<RoomSceneHistorySummaryDto>>? _sceneHistoryLoad;
    private Task<RoomSceneHistoryDto>? _sceneExportLoad;
    private string _sceneHistoryRoomId = string.Empty;
    private List<RoomSceneHistorySummaryDto> _sceneHistory = [];
    private string _sceneHistoryStatus = string.Empty;
    private SceneExportRequest? _sceneExportRequest;
    private bool _sceneHistoryShowAllModes;

    public ChatConversationView(ILogger logger, ChatClientService chatService, ImGuiChatRenderer renderer,
        SnowMediator mediator, FileDialogManager fileDialogManager)
    {
        _backgroundTasks = new BackgroundTaskTracker(logger);
        _chatService = chatService;
        _renderer = renderer;
        _mediator = mediator;
        _fileDialogManager = fileDialogManager;
    }

    public void Draw(ConversationKey key, bool showHeader = true)
    {
        var conversation = _chatService.Store.Snapshot.Conversations.FirstOrDefault(candidate => candidate.Key == key);
        if (conversation == null)
        {
            ImGui.TextDisabled("Conversation is no longer available.");
            return;
        }

        if (_draftKey != key)
        {
            _draftKey = key;
            _draft = conversation.Draft;
        }

        if (showHeader)
        {
            DrawHeader(conversation);
        }

        var room = key.Kind == ConversationKind.Room
            ? _chatService.ListRooms().FirstOrDefault(candidate => string.Equals(candidate.RoomId, key.Id, StringComparison.Ordinal))
            : null;
        var scene = room?.Scene?.IsScene == true;
        var inputHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y
            + (scene ? ImGui.GetFrameHeightWithSpacing() * 3f : 0f)
            + (string.IsNullOrWhiteSpace(_commandStatus) ? 0f : ImGui.GetTextLineHeightWithSpacing());
        using (var log = ImRaii.Child($"chat-log-{key}", new Vector2(-1, -inputHeight), false))
        {
            DateTime? currentDate = null;
            var visibleModes = room == null ? null : _roomModeFilters.GetValueOrDefault(room.RoomId);
            foreach (var entry in conversation.Entries)
            {
                if (visibleModes != null && !visibleModes.Contains(entry.RpMode))
                    continue;
                var localDate = entry.Timestamp.ToLocalTime().Date;
                if (currentDate != localDate)
                {
                    currentDate = localDate;
                    DrawDateSeparator(localDate);
                }

                using var id = ImRaii.PushId(entry.LocalId);
                var role = conversation.Members.GetValueOrDefault(entry.SenderUid);
                var labels = conversation.MemberLabels.GetValueOrDefault(entry.SenderUid);
                _renderer.Render(entry, ImGui.GetContentRegionAvail().X,
                    role is RoomRole.Owner or RoomRole.Moderator ? role : null, labels);
                if (entry.State == DeliveryState.Failed)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Retry"))
                    {
                        Queue(_chatService.Store.RetryAsync(key, entry.LocalId), nameof(ChatStore.RetryAsync));
                    }
                }
            }

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f * ImGuiHelpers.GlobalScale)
            {
                ImGui.SetScrollHereY(1f);
            }
        }

        using var inputDisabled = ImRaii.Disabled(!_chatService.CanSend);
        if (scene && room != null)
        {
            DrawSceneComposer(room);
        }
        ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X);
        var submit = ImGui.InputText("##chat-message", ref _draft, 2000, ImGuiInputTextFlags.EnterReturnsTrue);
        _chatService.Store.SetDraft(key, _draft);
        ImGui.SameLine();
        if (DrawSendButton())
        {
            submit = true;
        }

        if (_chatService.CanSend && submit && !string.IsNullOrWhiteSpace(_draft))
        {
            var text = _draft;
            _draft = string.Empty;
            _chatService.Store.SetDraft(key, string.Empty);
            Queue(SubmitWithFeedbackAsync(key, room, text), nameof(ChatStore.SendAsync));
            ImGui.SetKeyboardFocusHere(-1);
        }
        if (!string.IsNullOrWhiteSpace(_commandStatus))
            ImGui.TextColored(SnowcloakColours.CompactTextMuted, _commandStatus);
    }

    private void DrawSceneComposer(Snowcloak.API.Data.RoomData room)
    {
        var mode = _roomModes.GetValueOrDefault(room.RoomId, RpChatMode.InCharacter);
        var self = _chatService.GetRoomMembers(room.RoomId)
            .FirstOrDefault(member => string.Equals(member.User.UID, _chatService.SelfUid, StringComparison.Ordinal));
        var canNarrate = self?.IsNarrator == true;
        if (mode == RpChatMode.Standard || mode == RpChatMode.Narration && !canNarrate)
        {
            mode = RpChatMode.OutOfCharacter;
            _roomModes[room.RoomId] = mode;
        }
        ImGui.SetNextItemWidth(145f * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##scene-chat-mode", ModeLabel(mode)))
        {
            foreach (var option in SceneChatModes)
            {
                if (ImGui.Selectable(ModeLabel(option), option == mode))
                {
                    mode = option;
                    _roomModes[room.RoomId] = option;
                }
            }
            if (canNarrate && ImGui.Selectable(ModeLabel(RpChatMode.Narration), mode == RpChatMode.Narration))
            {
                mode = RpChatMode.Narration;
                _roomModes[room.RoomId] = mode;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextColored(SnowcloakColours.CompactTextMuted,
            room.Scene?.TurnState is { Enabled: true, UserUids.Count: > 0 } turn
                ? "Scene active  ·  " + CurrentTurnLabel(room.RoomId, turn) + "  ·  /snowturn will end the turn"
                : "Scene active");

        var filters = _roomModeFilters.GetValueOrDefault(room.RoomId);
        if (filters == null)
        {
            filters = AllRoomModes();
            _roomModeFilters[room.RoomId] = filters;
        }
        ImGui.TextColored(SnowcloakColours.CompactTextMuted, "Show:");
        foreach (var filterMode in new[] { RpChatMode.Standard, RpChatMode.InCharacter, RpChatMode.Action, RpChatMode.OutOfCharacter, RpChatMode.Narration })
        {
            ImGui.SameLine();
            var visible = filters.Contains(filterMode);
            if (ImGui.Checkbox(ModeShortLabel(filterMode) + "##filter-" + filterMode, ref visible))
            {
                if (visible) filters.Add(filterMode);
                else filters.Remove(filterMode);
            }
        }

        if (_chatService.SupportsRoleplaySceneToolkit)
        {
            CompleteDicePresetLoad(room);
            ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("##dice-expression", "4d6kh3+2", ref _diceExpression, 64);
            ImGui.SameLine();
            var validExpression = IsValidDiceExpression(_diceExpression);
            using (ImRaii.Disabled(!validExpression || !_chatService.CanSend))
            {
                if (ImGui.Button("Roll##dice-expression"))
                    Queue(_chatService.RollDiceExpressionAsync(room, _diceExpression, null, null), nameof(ChatClientService.RollDiceExpressionAsync));
            }
            ImGui.SameLine();
            ImGui.TextColored(validExpression ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                validExpression ? "valid" : "Use NdS, kh/kl, and an optional modifier");
            foreach (var preset in _dicePresets)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(preset.Name + "##preset-" + preset.Id))
                    Queue(_chatService.RollDiceExpressionAsync(room, null, preset.Id, preset.Name), nameof(ChatClientService.RollDiceExpressionAsync));
            }
        }
    }

    private async Task SubmitWithFeedbackAsync(ConversationKey key, Snowcloak.API.Data.RoomData? room, string input)
    {
        try
        {
            await SubmitAsync(key, room, input).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _commandStatus = ex.GetBaseException().Message;
        }
    }

    private async Task SubmitAsync(ConversationKey key, Snowcloak.API.Data.RoomData? room, string input)
    {
        _commandStatus = string.Empty;
        var trimmed = input.Trim();
        var separator = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var command = separator < 0 ? trimmed : trimmed[..separator];
        var arguments = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        if (command.StartsWith("/snow", StringComparison.OrdinalIgnoreCase))
        {
            if (room?.Scene?.IsScene != true)
            {
                _commandStatus = "RP chat commands require an active scene room.";
                return;
            }

            if (command.Equals("/snowroll", StringComparison.OrdinalIgnoreCase))
            {
                var expression = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (expression.Length == 0 || !IsValidDiceExpression(expression[0]))
                {
                    _commandStatus = "Use /snowroll NdS, optionally with khK/klK, a modifier, and a label.";
                    return;
                }
                if (_chatService.SupportsRoleplaySceneToolkit)
                {
                    await _chatService.RollDiceExpressionAsync(room, expression[0], null,
                        expression.Length > 1 ? expression[1] : null).ConfigureAwait(false);
                }
                else if (TryParseLegacyDiceExpression(expression[0], out var count, out var sides, out var modifier))
                {
                    await _chatService.RollDiceAsync(room, count, sides, modifier,
                        expression.Length > 1 ? expression[1] : null).ConfigureAwait(false);
                }
                else
                {
                    _commandStatus = "This server supports only NdS with an optional modifier; kh/kl requires the scene toolkit.";
                }
                return;
            }

            if (command.Equals("/snowturn", StringComparison.OrdinalIgnoreCase))
            {
                if (room.Scene?.TurnState is not { Enabled: true, UserUids.Count: > 0 })
                {
                    _commandStatus = "This scene has no active turn order.";
                    return;
                }
                await _chatService.AdvanceTurnAsync(room).ConfigureAwait(false);
                return;
            }

            var commandMode = command.ToUpperInvariant() switch
            {
                "/SNOWIC" => RpChatMode.InCharacter,
                "/SNOWOOC" => RpChatMode.OutOfCharacter,
                "/SNOWEMOTE" => RpChatMode.Action,
                "/SNOWNARRATE" => RpChatMode.Narration,
                _ => (RpChatMode?)null,
            };
            if (commandMode == null)
            {
                _commandStatus = "Unknown Snow chat command.";
                return;
            }
            _roomModes[room.RoomId] = commandMode.Value;
            if (string.IsNullOrWhiteSpace(arguments))
            {
                _commandStatus = ModeLabel(commandMode.Value) + " mode selected.";
                return;
            }
            await _chatService.Store.SendAsync(key, arguments, commandMode.Value).ConfigureAwait(false);
            return;
        }

        var mode = room?.Scene?.IsScene == true
            ? _roomModes.GetValueOrDefault(room.RoomId, RpChatMode.InCharacter)
            : RpChatMode.Standard;
        if (room?.Scene?.IsScene == true && mode == RpChatMode.Standard)
        {
            mode = RpChatMode.OutOfCharacter;
        }
        if (room?.Scene?.IsScene == true && IsWrappedOoc(trimmed, out var oocText))
        {
            mode = RpChatMode.OutOfCharacter;
            trimmed = oocText;
        }
        await _chatService.Store.SendAsync(key, trimmed, mode).ConfigureAwait(false);
    }

    private static bool IsWrappedOoc(string text, out string content)
    {
        if (text.StartsWith("((", StringComparison.Ordinal) && text.EndsWith("))", StringComparison.Ordinal) && text.Length > 4)
        {
            content = text[2..^2].Trim();
            return content.Length > 0;
        }
        content = text;
        return false;
    }

    private string CurrentTurnLabel(string roomId, Snowcloak.API.Dto.Roleplay.RoomTurnStateDto turn)
    {
        var uid = turn.UserUids[Math.Clamp(turn.CurrentIndex, 0, turn.UserUids.Count - 1)];
        var member = _chatService.GetRoomMembers(roomId).FirstOrDefault(candidate => string.Equals(candidate.User.UID, uid, StringComparison.Ordinal));
        return "Current turn: " + (member?.User.AliasOrUID ?? uid);
    }

    private static string ModeLabel(RpChatMode mode) => mode switch
    {
        RpChatMode.InCharacter => "In character",
        RpChatMode.OutOfCharacter => "Out of character",
        RpChatMode.Action => "Action",
        RpChatMode.Narration => "Narration",
        _ => "Standard chat",
    };

    private static string ModeShortLabel(RpChatMode mode) => mode switch
    {
        RpChatMode.InCharacter => "IC",
        RpChatMode.OutOfCharacter => "OOC",
        RpChatMode.Narration => "Narration",
        _ => mode.ToString(),
    };

    private static HashSet<RpChatMode> AllRoomModes() => Enum.GetValues<RpChatMode>().ToHashSet();

    private static bool IsValidDiceExpression(string value)
    {
        var match = Regex.Match(value, @"^(?<count>\d{1,2})[dD](?<sides>\d{1,4})(?:(?<keep>kh|kl)(?<keepCount>\d{1,2}))?(?<modifier>[+-]\d{1,5})?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success
            || !int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !int.TryParse(match.Groups["sides"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sides)
            || count is < 1 or > 20 || sides is < 2 or > 1000)
            return false;
        if (match.Groups["keep"].Success
            && (!int.TryParse(match.Groups["keepCount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var keep) || keep < 1 || keep > count))
            return false;
        return !match.Groups["modifier"].Success
            || int.TryParse(match.Groups["modifier"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var modifier)
            && modifier is >= -10000 and <= 10000;
    }

    private static bool TryParseLegacyDiceExpression(string value, out int count, out int sides, out int modifier)
    {
        count = 0;
        sides = 0;
        modifier = 0;
        var match = Regex.Match(value, @"^(?<count>\d{1,2})[dD](?<sides>\d{1,4})(?<modifier>[+-]\d{1,5})?$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success
            && int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out count)
            && int.TryParse(match.Groups["sides"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sides)
            && count is >= 1 and <= 20
            && sides is >= 2 and <= 1000
            && (!match.Groups["modifier"].Success
                || int.TryParse(match.Groups["modifier"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out modifier)
                && modifier is >= -10000 and <= 10000);
    }

    private void CompleteDicePresetLoad(Snowcloak.API.Data.RoomData room)
    {
        if (!string.Equals(_dicePresetRoomId, room.RoomId, StringComparison.Ordinal))
        {
            _dicePresetRoomId = room.RoomId;
            _dicePresets = [];
            _dicePresetLoad = _chatService.ListDicePresetsAsync(room);
        }
        if (_dicePresetLoad?.IsCompleted == true)
        {
            if (_dicePresetLoad.IsCompletedSuccessfully) _dicePresets = _dicePresetLoad.Result;
            _dicePresetLoad = null;
        }
    }

    private static void DrawDateSeparator(DateTime date)
    {
        var today = DateTime.Today;
        var label = date == today
            ? "Today"
            : date == today.AddDays(-1)
                ? "Yesterday"
                : date.ToString("D", CultureInfo.CurrentCulture);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(FontAwesomeIcon.ChevronDown.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    private static bool DrawSendButton()
    {
        var baseColour = new Vector4(0.145f, 0.290f, 0.470f, 1f);
        var hoverColour = new Vector4(0.190f, 0.350f, 0.540f, 1f);
        var activeColour = new Vector4(0.230f, 0.410f, 0.620f, 1f);
        using var buttonColour = ImRaii.PushColor(ImGuiCol.Button, baseColour);
        using var buttonHoverColour = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverColour);
        using var buttonActiveColour = ImRaii.PushColor(ImGuiCol.ButtonActive, activeColour);
        bool clicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            clicked = ImGui.Button(FontAwesomeIcon.PaperPlane.ToIconString() + "##send-message",
                new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
        }
        ElezenImgui.AttachTooltip("Send message");
        return clicked;
    }

    private void DrawHeader(ConversationSnapshot conversation)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(conversation.Title);
        ImGui.SameLine();
        var muteIcon = conversation.Muted ? FontAwesomeIcon.BellSlash : FontAwesomeIcon.Bell;
        if (ElezenImgui.ShowIconButton(muteIcon, conversation.Muted ? "Unmute conversation" : "Mute conversation"))
        {
            _chatService.SetMuted(conversation.Key, !conversation.Muted);
        }

        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.ExternalLinkAlt, "Open conversation in a pop-out window"))
        {
            _mediator.Publish(new OpenChatPopoutMessage(conversation.Key));
        }

        if (conversation.Key.Kind == ConversationKind.Room)
        {
            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileAlt, "Export room transcript"))
                ImGui.OpenPopup("scene-transcript-export");
            if (ImGui.BeginPopup("scene-transcript-export"))
            {
                if (ImGui.MenuItem("Text file")) BeginTranscriptExport(conversation, markdown: false);
                if (ImGui.MenuItem("Markdown file")) BeginTranscriptExport(conversation, markdown: true);
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Archive, "Download finished scene"))
            {
                EnsureSceneHistoryLoaded(conversation.Key.Id);
                ImGui.OpenPopup("scene-history-download");
            }
            DrawSceneHistoryDownload(conversation.Key.Id);
        }

        ImGui.Separator();
    }

    private void DrawSceneHistoryDownload(string roomId)
    {
        CompleteSceneHistoryOperations();
        if (!ImGui.BeginPopup("scene-history-download")) return;
        ImGui.Checkbox("Show OOC and standard chat", ref _sceneHistoryShowAllModes);
        if (_sceneHistoryLoad != null)
        {
            ImGui.TextDisabled("Loading scene history...");
        }
        else if (!string.IsNullOrEmpty(_sceneHistoryStatus))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, _sceneHistoryStatus);
        }
        else if (_sceneHistory.Count == 0)
        {
            ImGui.TextDisabled("No finished scenes are available to you in this room.");
        }
        else
        {
            foreach (var history in _sceneHistory)
            {
                using var id = ImRaii.PushId(history.HistoryId);
                ImGui.TextUnformatted(history.Title);
                ImGui.TextColored(SnowcloakColours.CompactTextMuted,
                    $"{DateTimeOffset.FromUnixTimeSeconds(history.FinishedAt).ToLocalTime():d} · {history.MessageCount} messages");
                if (ImGui.MenuItem("Download text"))
                    BeginSceneHistoryExport(roomId, history.HistoryId, markdown: false);
                ImGui.SameLine();
                if (ImGui.MenuItem("Download Markdown"))
                    BeginSceneHistoryExport(roomId, history.HistoryId, markdown: true);
                ModernSection.SoftSeparator();
            }
        }
        ImGui.EndPopup();
    }

    private void EnsureSceneHistoryLoaded(string roomId)
    {
        if (string.Equals(_sceneHistoryRoomId, roomId, StringComparison.Ordinal) && _sceneHistoryLoad != null) return;
        _sceneHistoryRoomId = roomId;
        _sceneHistory = [];
        _sceneHistoryStatus = string.Empty;
        var room = _chatService.ListRooms().FirstOrDefault(candidate => string.Equals(candidate.RoomId, roomId, StringComparison.Ordinal));
        if (room == null)
        {
            _sceneHistoryStatus = "This room is no longer available.";
            return;
        }
        _sceneHistoryLoad = _chatService.ListSceneHistoryAsync(room);
    }

    private void BeginSceneHistoryExport(string roomId, string historyId, bool markdown)
    {
        var room = _chatService.ListRooms().FirstOrDefault(candidate => string.Equals(candidate.RoomId, roomId, StringComparison.Ordinal));
        if (room == null) return;
        _sceneExportRequest = new SceneExportRequest(markdown);
        _sceneExportLoad = _chatService.GetSceneHistoryAsync(room, historyId, _sceneHistoryShowAllModes);
    }

    private void CompleteSceneHistoryOperations()
    {
        if (_sceneHistoryLoad?.IsCompleted == true)
        {
            if (_sceneHistoryLoad.IsCompletedSuccessfully) _sceneHistory = _sceneHistoryLoad.Result;
            else _sceneHistoryStatus = "Unable to load scene history.";
            _sceneHistoryLoad = null;
        }
        if (_sceneExportLoad?.IsCompleted == true)
        {
            if (_sceneExportLoad.IsCompletedSuccessfully && _sceneExportRequest != null)
                BeginSceneHistoryExport(_sceneExportLoad.Result, _sceneExportRequest.Markdown);
            _sceneExportLoad = null;
            _sceneExportRequest = null;
        }
    }

    private void BeginTranscriptExport(ConversationSnapshot conversation, bool markdown)
    {
        var extension = markdown ? ".md" : ".txt";
        var stem = string.Concat(conversation.Title.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var fileName = $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
        _fileDialogManager.SaveFileDialog("Export room transcript", extension, fileName, extension, (success, path) =>
        {
            if (!success) return;
            File.WriteAllText(path, BuildTranscript(conversation, markdown), Encoding.UTF8);
        });
    }

    private void BeginSceneHistoryExport(RoomSceneHistoryDto history, bool markdown)
    {
        var extension = markdown ? ".md" : ".txt";
        var title = string.IsNullOrWhiteSpace(history.Summary.Title) ? "scene" : history.Summary.Title;
        var stem = string.Concat(title.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var fileName = $"{stem}-{DateTimeOffset.FromUnixTimeSeconds(history.Summary.FinishedAt):yyyyMMdd-HHmmss}{extension}";
        _fileDialogManager.SaveFileDialog("Download finished scene", extension, fileName, extension, (success, path) =>
        {
            if (!success) return;
            File.WriteAllText(path, BuildSceneHistoryTranscript(history, markdown), Encoding.UTF8);
        });
    }

    private static string BuildSceneHistoryTranscript(RoomSceneHistoryDto history, bool markdown)
    {
        var builder = new StringBuilder();
        var scene = history.Scene;
        if (markdown)
        {
            builder.Append("# ").AppendLine(history.Summary.Title);
            if (scene.Cast.Count > 0) builder.Append("**Cast:** ").AppendLine(string.Join(", ", scene.Cast));
            if (!string.IsNullOrWhiteSpace(scene.Setting)) builder.Append("**Setting:** ").AppendLine(scene.Setting);
            if (!string.IsNullOrWhiteSpace(scene.ExpectedTone)) builder.Append("**Tone:** ").AppendLine(scene.ExpectedTone);
            if (scene.ContentWarnings.Count > 0) builder.Append("**Content warnings:** ").AppendLine(string.Join(", ", scene.ContentWarnings));
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine(history.Summary.Title);
            if (scene.Cast.Count > 0) builder.Append("Cast: ").AppendLine(string.Join(", ", scene.Cast));
            if (!string.IsNullOrWhiteSpace(scene.Setting)) builder.Append("Setting: ").AppendLine(scene.Setting);
            if (!string.IsNullOrWhiteSpace(scene.ExpectedTone)) builder.Append("Tone: ").AppendLine(scene.ExpectedTone);
            if (scene.ContentWarnings.Count > 0) builder.Append("Content warnings: ").AppendLine(string.Join(", ", scene.ContentWarnings));
            builder.AppendLine();
        }
        foreach (var entry in history.Entries)
        {
            var message = entry.Message;
            var time = DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            var text = ChatMessageCodec.DecodeText(message.Message.PayloadContent);
            var name = message.Message.SenderName;
            if (markdown)
            {
                builder.Append("- `").Append(time).Append("` ");
                if (message.Message.RpMode == RpChatMode.Narration) builder.Append("***Narration - ").Append(name).Append(":*** ").Append(text);
                else if (message.Message.RpMode == RpChatMode.Action) builder.Append('*').Append(name).Append(' ').Append(text).Append('*');
                else if (message.Message.RpMode == RpChatMode.OutOfCharacter) builder.Append("**").Append(name).Append(" (OOC):** ").Append(text);
                else builder.Append("**").Append(name).Append(message.Message.RpMode == RpChatMode.InCharacter ? " (IC):** " : ":** ").Append(text);
                builder.AppendLine();
            }
            else
            {
                builder.Append('[').Append(time).Append("] ");
                if (message.Message.RpMode == RpChatMode.Narration) builder.Append("[Narration - ").Append(name).Append("] ").Append(text);
                else if (message.Message.RpMode == RpChatMode.Action) builder.Append("* ").Append(name).Append(' ').Append(text);
                else if (message.Message.RpMode == RpChatMode.OutOfCharacter) builder.Append("((").Append(name).Append(": ").Append(text).Append("))");
                else builder.Append(name).Append(message.Message.RpMode == RpChatMode.InCharacter ? " (IC): " : ": ").Append(text);
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

    private string BuildTranscript(ConversationSnapshot conversation, bool markdown)
    {
        var room = _chatService.ListRooms().FirstOrDefault(candidate => string.Equals(candidate.RoomId, conversation.Key.Id, StringComparison.Ordinal));
        var builder = new StringBuilder();
        if (markdown)
        {
            builder.Append("# ").AppendLine(string.IsNullOrWhiteSpace(room?.Scene?.Title) ? conversation.Title : room.Scene.Title);
            if (room?.Scene?.Cast.Count > 0) builder.Append("**Cast:** ").AppendLine(string.Join(", ", room.Scene.Cast));
            if (!string.IsNullOrWhiteSpace(room?.Scene?.Setting)) builder.Append("**Setting:** ").AppendLine(room.Scene.Setting);
            if (!string.IsNullOrWhiteSpace(room?.Scene?.ExpectedTone)) builder.Append("**Tone:** ").AppendLine(room.Scene.ExpectedTone);
            if (room?.Scene?.ContentWarnings.Count > 0) builder.Append("**Content warnings:** ").AppendLine(string.Join(", ", room.Scene.ContentWarnings));
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine(string.IsNullOrWhiteSpace(room?.Scene?.Title) ? conversation.Title : room.Scene.Title);
            if (room?.Scene?.Cast.Count > 0) builder.Append("Cast: ").AppendLine(string.Join(", ", room.Scene.Cast));
            if (!string.IsNullOrWhiteSpace(room?.Scene?.Setting)) builder.Append("Setting: ").AppendLine(room.Scene.Setting);
            if (!string.IsNullOrWhiteSpace(room?.Scene?.ExpectedTone)) builder.Append("Tone: ").AppendLine(room.Scene.ExpectedTone);
            if (room?.Scene?.ContentWarnings.Count > 0) builder.Append("Content warnings: ").AppendLine(string.Join(", ", room.Scene.ContentWarnings));
            builder.AppendLine();
        }

        foreach (var entry in conversation.Entries)
        {
            var text = ChatMessageCodec.Flatten(entry.Segments);
            var time = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            if (entry.Kind != ChatEntryKind.Message)
            {
                builder.Append(markdown ? "- *[" : "[").Append(time).Append("] ")
                    .Append(entry.Kind == ChatEntryKind.TurnChanged ? entry.RawText : entry.Display.Name + " " + entry.RawText)
                    .AppendLine(markdown ? "*" : string.Empty);
                continue;
            }
            if (markdown)
            {
                builder.Append("- `").Append(time).Append("` ");
                if (entry.RpMode == RpChatMode.Narration) builder.Append("***Narration - ").Append(entry.Display.Name).Append(":*** ").Append(text);
                else if (entry.RpMode == RpChatMode.Action) builder.Append('*').Append(entry.Display.Name).Append(' ').Append(text).Append('*');
                else if (entry.RpMode == RpChatMode.OutOfCharacter) builder.Append("**").Append(entry.Display.Name).Append(" (OOC):** ").Append(text);
                else builder.Append("**").Append(entry.Display.Name).Append(entry.RpMode == RpChatMode.InCharacter ? " (IC):** " : ":** ").Append(text);
                builder.AppendLine();
            }
            else
            {
                builder.Append('[').Append(time).Append("] ");
                if (entry.RpMode == RpChatMode.Narration) builder.Append("[Narration - ").Append(entry.Display.Name).Append("] ").Append(text);
                else if (entry.RpMode == RpChatMode.Action) builder.Append("* ").Append(entry.Display.Name).Append(' ').Append(text);
                else if (entry.RpMode == RpChatMode.OutOfCharacter) builder.Append("((").Append(entry.Display.Name).Append(": ").Append(text).Append("))");
                else builder.Append(entry.Display.Name).Append(entry.RpMode == RpChatMode.InCharacter ? " (IC): " : ": ").Append(text);
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }


    private void Queue(Task task, string operation)
    {
        _ = _backgroundTasks.Run(() => task, operation);
    }

    private sealed record SceneExportRequest(bool Markdown);
}
