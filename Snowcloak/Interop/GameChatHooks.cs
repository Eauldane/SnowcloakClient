using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.Shell;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;

namespace Snowcloak.Interop;

public record ChatChannelOverride
{
    public string ChannelName = string.Empty;
    public Action<byte[]>? ChatMessageHandler;
}

public unsafe sealed class GameChatHooks : IDisposable
{
    // Based on https://git.anna.lgbt/anna/ExtraChat/src/branch/main/client/ExtraChat/GameFunctions.cs

    private readonly ILogger<GameChatHooks> _logger;
    private readonly Action<int, byte[]> _ssCommandHandler;

    #region signatures
    #pragma warning disable CS0649
    // I do not know what kind of black magic this function performs
    // Client::UI::Misc::PronounModule::???
    
    [Signature("E8 ?? ?? ?? ?? 44 88 74 24 ?? 4C 8D 45")]
    private readonly delegate* unmanaged<PronounModule*, Utf8String*, byte, Utf8String*> _processStringStep2;
    
    // Component::Shell::ShellCommandModule::ExecuteCommandInner
    private delegate void SendMessageDelegate(ShellCommandModule* module, Utf8String* message, UIModule* uiModule);
    [Signature(
        "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87",
        DetourName = nameof(SendMessageDetour)
    )]
    private Hook<SendMessageDelegate>? SendMessageHook { get; init; }

    // Client::UI::Shell::RaptureShellModule::SetChatChannel
    private delegate void SetChatChannelDelegate(RaptureShellModule* module, uint channel);
    [Signature(
        "E8 ?? ?? ?? ?? 33 C0 EB ?? 85 D2",
        DetourName = nameof(SetChatChannelDetour)
    )]
    private Hook<SetChatChannelDelegate>? SetChatChannelHook { get; init; }

    // Component::Shell::ShellCommandModule::ChangeChannelName
    private delegate byte* ChangeChannelNameDelegate(AgentChatLog* agent);
    [Signature(
        "E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8D 4D B0 48 8B F8 E8 ?? ?? ?? ?? 41 8B D6",
        DetourName = nameof(ChangeChannelNameDetour)
    )]
    private Hook<ChangeChannelNameDelegate>? ChangeChannelNameHook { get; init; }

    // Client::UI::Agent::AgentChatLog::???
    private delegate byte ShouldDoNameLookupDelegate(AgentChatLog* agent);
    [Signature(
        "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B D9 40 32 FF 48 8B 49 ?? ?? ?? ?? FF 50",
        DetourName = nameof(ShouldDoNameLookupDetour)
    )]
    private Hook<ShouldDoNameLookupDelegate>? ShouldDoNameLookupHook { get; init; }

    // Temporary chat channel change (via hotkey)
    // Client::UI::Shell::RaptureShellModule::???
    private delegate ulong TempChatChannelDelegate(RaptureShellModule* module, uint x, uint y, ulong z);
    [Signature(
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 83 B9 ?? ?? ?? ?? ?? 49 8B F9 41 8B F0",
        DetourName = nameof(TempChatChannelDetour)
    )]
    private Hook<TempChatChannelDelegate>? TempChatChannelHook { get; init; }

    // Temporary tell target change (via hotkey)
    // Client::UI::Shell::RaptureShellModule::SetContextTellTargetInForay
    private delegate ulong TempTellTargetDelegate(RaptureShellModule* module, ulong a, ulong b, ulong c, ushort d, ulong e, ulong f, ushort g);
    [Signature(
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 83 B9 ?? ?? ?? ?? ?? 41 0F B7 F9",
        DetourName = nameof(TempTellTargetDetour)
    )]
    private Hook<TempTellTargetDelegate>? TempTellTargetHook { get; init; }

    // Called every frame while the chat bar is not focused
    private delegate void UnfocusTickDelegate(RaptureShellModule* module);
    [Signature(
        "40 53 48 83 EC ?? 83 B9 ?? ?? ?? ?? ?? 48 8B D9 0F 84 ?? ?? ?? ?? 48 8D 91",
        DetourName = nameof(UnfocusTickDetour)
    )]
    private Hook<UnfocusTickDelegate>? UnfocusTickHook { get; init; }
    #pragma warning restore CS0649
    #endregion

    private ChatChannelOverride? _chatChannelOverride;
    private ChatChannelOverride? _chatChannelOverrideTempBuffer;
    private bool _shouldForceNameLookup = false;

    private DateTime _nextMessageIsReply = DateTime.UnixEpoch;

    public ChatChannelOverride? ChatChannelOverride
    {
        get => _chatChannelOverride;
        set {
            _chatChannelOverride = value;
            _shouldForceNameLookup = true;
        }
    }

    private void StashChatChannel()
    {
        if (_chatChannelOverride != null)
        {
            _logger.LogTrace("Stashing chat channel");
            _chatChannelOverrideTempBuffer = _chatChannelOverride;
            ChatChannelOverride = null;
        }
    }

    private void UnstashChatChannel()
    {
        if (_chatChannelOverrideTempBuffer != null)
        {
            _logger.LogTrace("Unstashing chat channel");
            ChatChannelOverride = _chatChannelOverrideTempBuffer;
            _chatChannelOverrideTempBuffer = null;
        }
    }

    public GameChatHooks(ILogger<GameChatHooks> logger, IGameInteropProvider gameInteropProvider, Action<int, byte[]> ssCommandHandler)
    {
        _logger = logger;
        _ssCommandHandler = ssCommandHandler;

        logger.LogInformation("Initializing GameChatHooks");
        gameInteropProvider.InitializeFromAttributes(this);

        SendMessageHook?.Enable();
        SetChatChannelHook?.Enable();
        ChangeChannelNameHook?.Enable();
        ShouldDoNameLookupHook?.Enable();
        TempChatChannelHook?.Enable();
        TempTellTargetHook?.Enable();
        UnfocusTickHook?.Enable();
    }

    public void Dispose()
    {
        SendMessageHook?.Dispose();
        SetChatChannelHook?.Dispose();
        ChangeChannelNameHook?.Dispose();
        ShouldDoNameLookupHook?.Dispose();
        TempChatChannelHook?.Dispose();
        TempTellTargetHook?.Dispose();
        UnfocusTickHook?.Dispose();
    }

    private byte[] ProcessChatMessage(Utf8String* message)
    {
        var pronounModule = UIModule.Instance()->GetPronounModule();
        var chatString1 = pronounModule->ProcessString(message, true);
        var chatString2 = _processStringStep2(pronounModule, chatString1, 1);
        return MemoryHelper.ReadRaw((nint)chatString2->StringPtr.Value, chatString2->Length);
    }

    private void SendMessageDetour(ShellCommandModule* thisPtr, Utf8String* message, UIModule* uiModule)
    {
        try
        {
            var messageLength = message->Length;
            var messageSpan = message->AsSpan();

            bool isCommand = false;
            bool isReply = false;

            var utcNow = DateTime.UtcNow;

            // Check if chat input begins with a command (or auto-translated command)
            // Or if we think we're being called to send text via the /r command
            if (_nextMessageIsReply >= utcNow)
            {
                isCommand = true;
            }
            else if (messageLength == 0 || messageSpan[0] == (byte)'/' || !messageSpan.ContainsAnyExcept((byte)' '))
            {
                isCommand = true;
                if (messageSpan.StartsWith(System.Text.Encoding.ASCII.GetBytes("/r ")) || messageSpan.StartsWith(System.Text.Encoding.ASCII.GetBytes("/reply ")))
                    isReply = true;
            }
            else if (messageSpan[0] == (byte)0x02) /* Payload.START_BYTE */
            {
                var payload = Payload.Decode(new BinaryReader(new UnmanagedMemoryStream(message->StringPtr, message->BufSize))) as AutoTranslatePayload;

                // Auto-translate text begins with /
                if (payload != null && payload.Text.Length > 2 && payload.Text[2] == '/')
                {
                    isCommand = true;
                    if (payload.Text[2..].StartsWith("/r ", StringComparison.Ordinal) || payload.Text[2..].StartsWith("/reply ", StringComparison.Ordinal))
                        isReply = true;
                }
            }

            // When using /r the game will set a flag and then call this function a second time
            // The next call to this function will be raw text intended for the IM recipient
            // This flag's validity is time-limited as a fail-safe
            if (isReply)
                _nextMessageIsReply = utcNow + TimeSpan.FromMilliseconds(100);

            // If it is a command, check if it begins with /ss first so we can handle the message directly
            // Letting Dalamud handle the commands causes all of the special payloads to be dropped
            if (isCommand && messageSpan.StartsWith(System.Text.Encoding.ASCII.GetBytes("/ss")))
            {
                for (int i = 1; i <= ChatService.CommandMaxNumber; ++i)
                {
                    var cmdString = $"/ss{i} ";
                    if (messageSpan.StartsWith(System.Text.Encoding.ASCII.GetBytes(cmdString)))
                    {
                        var ssChatBytes = ProcessChatMessage(message);
                        ssChatBytes = ssChatBytes.Skip(cmdString.Length).ToArray();
                        _ssCommandHandler?.Invoke(i, ssChatBytes);
                        return;
                    }
                }
            }

            // If not a command, or no override is set, then call the original chat handler
            if (isCommand || _chatChannelOverride == null)
            {
                SendMessageHook!.OriginalDisposeSafe(thisPtr, message, uiModule);
                return;
            }

            // Otherwise, the text is to be sent to the emulated chat channel handler
            // The chat input string is rendered in to a payload for display first
            var chatBytes = ProcessChatMessage(message);

            if (chatBytes.Length > 0)
                _chatChannelOverride.ChatMessageHandler?.Invoke(chatBytes);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception thrown during SendMessageDetour");
        }
    }

    private void SetChatChannelDetour(RaptureShellModule* module, uint channel)
    {
        try
        {
            if (_chatChannelOverride != null)
            {
                _chatChannelOverride = null;
                _shouldForceNameLookup = true;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception thrown during SetChatChannelDetour");
        }

        SetChatChannelHook!.OriginalDisposeSafe(module, channel);
    }

    private ulong TempChatChannelDetour(RaptureShellModule* module, uint x, uint y, ulong z)
    {
        var result = TempChatChannelHook!.OriginalDisposeSafe(module, x, y, z);

        if (result != 0)
            StashChatChannel();

        return result;
    }

    private ulong TempTellTargetDetour(RaptureShellModule* module, ulong a, ulong b, ulong c, ushort d, ulong e, ulong f, ushort g)
    {
        var result = TempTellTargetHook!.OriginalDisposeSafe(module, a, b, c, d, e, f, g);

        if (result != 0)
            StashChatChannel();

        return result;
    }

    private void UnfocusTickDetour(RaptureShellModule* module)
    {
        UnfocusTickHook!.OriginalDisposeSafe(module);
        UnstashChatChannel();
    }

    private byte* ChangeChannelNameDetour(AgentChatLog* agent)
    {
        var originalResult = ChangeChannelNameHook!.OriginalDisposeSafe(agent);

        try
        {
            // Replace the chat channel name on the UI if active
            if (_chatChannelOverride != null)
            {
                agent->ChannelLabel.SetString(_chatChannelOverride.ChannelName);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception thrown during ChangeChannelNameDetour");
        }

        return originalResult;
    }

    private byte ShouldDoNameLookupDetour(AgentChatLog* agent)
    {
        var originalResult = ShouldDoNameLookupHook!.OriginalDisposeSafe(agent);

        try
        {
            // Force the chat channel name to update when required
            if (_shouldForceNameLookup)
            {
                _shouldForceNameLookup = false;
                return 1;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exception thrown during ShouldDoNameLookupDetour");
        }

        return originalResult;
    }
}
