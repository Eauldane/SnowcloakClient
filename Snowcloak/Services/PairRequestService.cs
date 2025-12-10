using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Microsoft.Extensions.DependencyInjection;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Dto.User;
using Snowcloak.Configuration;
using Snowcloak.Configuration.Models;
using Snowcloak.Interop.Ipc;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;
using System.Text.Json;
using Snowcloak.Services.ServerConfiguration;
using System.Threading;

namespace Snowcloak.Services;

public class PairRequestService : DisposableMediatorSubscriberBase
{
    private readonly ILogger<PairRequestService> _logger;
    private readonly SnowcloakConfigService _configService;
    private readonly Lazy<ApiController> _apiController;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly IpcManager _ipcManager;
    private readonly IToastGui _toastGui;
    private readonly ConcurrentDictionary<Guid, PairingRequestDto> _pendingRequests = new();
    private readonly HashSet<string> _availableIdents = new(StringComparer.Ordinal);
    private readonly object _availabilityLock = new();
    private HashSet<string> _stagedAvailability = new(StringComparer.Ordinal);
    private CancellationTokenSource? _availabilityDebounceCts;
    private readonly IContextMenu _contextMenu;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly HashSet<string> _lastNearbyIdentSnapshot = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _nearbyAvailabilityCts = new();
    private readonly Task _nearbyAvailabilityLoop;
    private static readonly TimeSpan AvailabilityApplyDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan NearbyAvailabilityPollInterval = TimeSpan.FromSeconds(5);
    private const int MaxNearbySnapshot = 1024;
    private bool _advertisingPairing;

    public PairRequestService(ILogger<PairRequestService> logger, SnowcloakConfigService configService,
        SnowMediator mediator, DalamudUtilService dalamudUtilService,
        IpcManager ipcManager, IToastGui toastGui, IContextMenu contextMenu, IServiceProvider serviceProvider,
        ServerConfigurationManager serverConfigurationManager)
        : base(logger, mediator)
    {
        _logger = logger;
        _configService = configService;
        _apiController = new Lazy<ApiController>(() => serviceProvider.GetRequiredService<ApiController>());
        _dalamudUtilService = dalamudUtilService;
        _ipcManager = ipcManager;
        _toastGui = toastGui;
        _contextMenu = contextMenu;
        _serverConfigurationManager = serverConfigurationManager;
        _contextMenu.OnMenuOpened += ContextMenuOnMenuOpened;
        Mediator.Subscribe<TargetPlayerChangedMessage>(this, OnTargetPlayerChanged);

        Mediator.Subscribe<ConnectedMessage>(this, OnConnected);
        Mediator.Subscribe<HubReconnectedMessage>(this, OnHubReconnected);
        _nearbyAvailabilityLoop = Task.Run(() => PollNearbyAvailabilityAsync(_nearbyAvailabilityCts.Token));
    }

    private void OnConnected(ConnectedMessage message) => _ = OnConnectedAsync();

    private void OnHubReconnected(HubReconnectedMessage message) => _ = OnConnectedAsync();

    private async Task OnConnectedAsync()
    {
        await SyncAdvertisingAsync(force: true).ConfigureAwait(false);
        await RefreshNearbyAvailabilityAsync(force: true).ConfigureAwait(false);
    }
    
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _contextMenu.OnMenuOpened -= ContextMenuOnMenuOpened;
        
        _nearbyAvailabilityCts.Cancel();
        try
        {
            _availabilityDebounceCts?.Cancel();
            _nearbyAvailabilityLoop.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // ignored
        }
    }

    public IReadOnlyCollection<string> AvailableIdents
        => _availableIdents.ToArray();

    public IReadOnlyCollection<PairingRequestDto> PendingRequests
        => _pendingRequests.Values.ToList();

    private void ContextMenuOnMenuOpened(IMenuOpenedArgs args)
    {
        if (!_configService.Current.EnableRightClickMenus) return;
        if (!_configService.Current.PairingSystemEnabled) return;
        if (args.MenuType == ContextMenuType.Inventory) return;
        if (!_dalamudUtilService.TryGetIdentFromMenuTarget(args, out var ident)) return;
        if (!_availableIdents.Contains(ident)) return;

        void Add(string name, Action<IMenuItemClickedArgs>? action)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = name,
                PrefixChar = 'S',
                PrefixColor = 526,
                OnClicked = action
            });
        }

        Add("Send Snowcloak Pair Request", async _ => await SendPairRequestAsync(ident).ConfigureAwait(false));
        Add("View Snowcloak Profile", _ => Mediator.Publish(new NotificationMessage("Profile request", "Requesting profile from nearby player", NotificationType.Info, TimeSpan.FromSeconds(4))));
    }

    public async Task SyncAdvertisingAsync(bool force = false)
    {
        var advertise = _configService.Current.PairingSystemEnabled;

        if (!force && _advertisingPairing == advertise) return;
        
        _advertisingPairing = advertise;

        try
        {
            await _apiController.Value.UserSetPairingOptIn(new PairingOptInDto(advertise)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send pairing availability update");
        }
    }

    public void UpdateAvailability(IEnumerable<PairingAvailabilityDto> available)
    {
        var incoming = available?.Select(dto => dto.Ident)
            .Where(ident => !string.IsNullOrWhiteSpace(ident))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        // Keep availability limited to players we still see locally so nameplate highlights
        // only persist while the player remains in range (or until the server tells us they
        // opted out).
        if (_lastNearbyIdentSnapshot.Count > 0)
            incoming.IntersectWith(_lastNearbyIdentSnapshot);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Pairing availability from server: {count} -> [{idents}]", incoming.Count,
                string.Join(", ", incoming));
        
        StageAvailability(incoming);
    }

    private void StageAvailability(HashSet<string> incoming)
    {
        lock (_availabilityLock)
        {
            if (_stagedAvailability.Count == 0)
                _stagedAvailability = new HashSet<string>(incoming, StringComparer.Ordinal);
            else
                _stagedAvailability.UnionWith(incoming);

            _availabilityDebounceCts?.Cancel();
            _availabilityDebounceCts = new CancellationTokenSource();
            var token = _availabilityDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(AvailabilityApplyDebounce, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                HashSet<string> staged;
                lock (_availabilityLock)
                {
                    staged = new HashSet<string>(_stagedAvailability, StringComparer.Ordinal);
                    _stagedAvailability.Clear();
                }

                ApplyAvailability(staged);
            });
        }
    }

    private void ApplyAvailability(HashSet<string> incoming)
    {

        if (!_configService.Current.PairingSystemEnabled)
        {
            _availableIdents.Clear();
            Mediator.Publish(new PairingAvailabilityChangedMessage());
            return;
        }
        
        if (_availableIdents.SetEquals(incoming))
            return;

        _availableIdents.Clear();

        foreach (var ident in incoming)
        {
            _availableIdents.Add(ident);
        }

        Mediator.Publish(new PairingAvailabilityChangedMessage());
    }

       private async Task PollNearbyAvailabilityAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshNearbyAvailabilityAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to refresh nearby pairing availability");
            }

            try
            {
                await Task.Delay(NearbyAvailabilityPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        }
    }

    private async Task RefreshNearbyAvailabilityAsync(bool force = false)
    {
        if (!_configService.Current.PairingSystemEnabled)
        {
            if (_availableIdents.Count > 0)
            {
                _availableIdents.Clear();
                Mediator.Publish(new PairingAvailabilityChangedMessage());
            }

            return;
        }

        if (!_apiController.Value.IsConnected)
            return;

        var nearby = await _dalamudUtilService.GetNearbyPlayerNameHashesAsync(MaxNearbySnapshot).ConfigureAwait(false);
        var nearbySet = new HashSet<string>(nearby, StringComparer.Ordinal);

        _lastNearbyIdentSnapshot.Clear();
        foreach (var ident in nearbySet)
        {
            _lastNearbyIdentSnapshot.Add(ident);
        }

        try
        {
            var availability = await _apiController.Value
                .UserQueryPairingAvailability(new PairingAvailabilityQueryDto([.. nearbySet]))
                .ConfigureAwait(false);

            UpdateAvailability(availability);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to query nearby pairing availability");
        }
        
        await EvaluatePendingRequestsAsync(nearbySet).ConfigureAwait(false);
    }

    
    public async Task SendPairRequestAsync(string ident)
    {
        if (!_configService.Current.PairingSystemEnabled)
        {
            _logger.LogDebug("Pair request send ignored: pairing system disabled");
            return;
        }

        try
        {
            await _apiController.Value.UserSendPairRequest(new PairingRequestTargetDto(ident)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send pair request to {ident}", ident);
        }
    }

    public async Task RespondAsync(PairingRequestDto request, bool accepted, string? reason = null)
    {
        var note = GetRequesterDisplayName(request);
        await RespondWithDecisionAsync(request.RequestId, accepted, reason).ConfigureAwait(false);

        if (accepted)
        {
            ApplyAutoNote(request, note);
        }
        
        _pendingRequests.TryRemove(request.RequestId, out _);
        Mediator.Publish(new PairingRequestListChangedMessage());
    }
    
    public Task RespondAsync(Guid requestId, bool accepted, string? reason = null)
    {
        if (_pendingRequests.TryGetValue(requestId, out var request))
            return RespondAsync(request, accepted, reason);

        return RespondWithDecisionAsync(requestId, accepted, reason);
    }

    private async Task RespondWithDecisionAsync(Guid requestId, bool accepted, string? reason)
    {
        try
        {
            await _apiController.Value
                .UserRespondToPairRequest(new PairingRequestDecisionDto(requestId, accepted, reason))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to respond to request {requestId}", requestId);
        }
    }

    public void ReceiveRequest(PairingRequestDto dto)
    {
        _ = HandleRequestAsync(dto);
    }

    private async Task HandleRequestAsync(PairingRequestDto dto)
    {
        var autoRejectResult = await ShouldAutoRejectAsync(dto.RequesterIdent).ConfigureAwait(false);
        if (autoRejectResult.ShouldReject)
        {
            await RespondAsync(dto.RequestId, false, autoRejectResult.Reason).ConfigureAwait(false);
            return;
        }

        _pendingRequests[dto.RequestId] = dto;
        Mediator.Publish(new PairingRequestReceivedMessage(dto));
        Mediator.Publish(new PairingRequestListChangedMessage());
        var requesterName = GetRequesterDisplayName(dto, setNoteFromNearby: true);
        _toastGui.ShowQuest(requesterName + " sent a pairing request.");
    }

    public RequesterDisplay GetRequesterDisplay(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        var resolved = TryResolveRequester(dto, setNoteFromNearby);
        return new RequesterDisplay(resolved.Name ?? dto.Requester.UID, resolved.WorldId);
    }
    
    public string GetRequesterDisplayName(PairingRequestDto dto, bool setNoteFromNearby = false)
    {
        return GetRequesterDisplay(dto, setNoteFromNearby).NameOrUid;
    }

    private RequesterDisplay TryResolveRequester(PairingRequestDto dto, bool setNoteFromNearby)
    {
        var pc = _dalamudUtilService.FindPlayerByNameHash(dto.RequesterIdent);
        if (pc.ObjectId != 0 && pc.Address != IntPtr.Zero && !string.IsNullOrWhiteSpace(pc.Name))
        {
            var name = pc.Name;
            var world = (ushort?)pc.HomeWorldId;
            _serverConfigurationManager.SetNameForUid(dto.Requester.UID, name);
            if (setNoteFromNearby && string.IsNullOrWhiteSpace(_serverConfigurationManager.GetNoteForUid(dto.Requester.UID)))
            {
                _serverConfigurationManager.SetNoteForUid(dto.Requester.UID, name);
            }

            return new RequesterDisplay(name, world);
        }

        return new RequesterDisplay(null, null);
        
    }

    private void ApplyAutoNote(PairingRequestDto request, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        if (_serverConfigurationManager.GetNoteForUid(request.Requester.UID) != null)
            return;

        _serverConfigurationManager.SetNoteForUid(request.Requester.UID, note);
    }

    private async Task EvaluatePendingRequestsAsync(HashSet<string> nearbySet)
    {
        foreach (var request in _pendingRequests.Values)
        {
            if (!nearbySet.Contains(request.RequesterIdent))
                continue;

            var autoRejectResult = await ShouldAutoRejectAsync(request.RequesterIdent, deferIfUnavailable: false)
                .ConfigureAwait(false);

            if (!autoRejectResult.ShouldReject)
                continue;

            await RespondAsync(request, false, autoRejectResult.Reason).ConfigureAwait(false);

            var requesterName = GetRequesterDisplayName(request);
            var message = $"{requesterName}'s pending pairing request was auto-rejected after they came into range and were found to match your filters.";

            _toastGui.ShowNormal(message);
        }
    }

    private async Task<(bool ShouldReject, string Reason)> ShouldAutoRejectAsync(string ident, bool deferIfUnavailable = true)
    {
        if (!_configService.Current.PairingSystemEnabled)
            return (false, string.Empty);
        
        var hasAppearanceFilters = _configService.Current.AutoRejectCombos.Count > 0;
        var minimumLevel = Math.Max(0, _configService.Current.PairRequestMinimumLevel);
        if (!hasAppearanceFilters && minimumLevel == 0)
            return (false, string.Empty);

        var pc = _dalamudUtilService.FindPlayerByNameHash(ident);
        if (pc.ObjectId == 0 || pc.Address == IntPtr.Zero)
            return deferIfUnavailable
                ? (false, string.Empty)
                : (true, "Auto rejected: requester unavailable for filtering");
        
        if (minimumLevel > 0)
        {
            if (pc.Level <= 0)
                return deferIfUnavailable
                    ? (false, string.Empty)
                    : (true, "Auto rejected: requester level unavailable");
            
            if (pc.Level < minimumLevel)
                return (true, $"Auto rejected: This user isn't interested in pairing with users below level {minimumLevel}.");
        }

        var appearance = await ExtractAppearanceAsync(pc.Address).ConfigureAwait(false);
        if (appearance == null)
            return hasAppearanceFilters
                ? deferIfUnavailable
                    ? (false, string.Empty)
                    : (true, "Auto rejected: appearance unavailable")
                : (false, string.Empty);        
        if (appearance.Gender.HasValue && appearance.Race.HasValue && appearance.Clan.HasValue)
        {
            var key = new AutoRejectCombo(appearance.Race.Value, appearance.Clan.Value, appearance.Gender.Value);
            if (_configService.Current.AutoRejectCombos.Contains(key))
                return (true, "Auto rejected: This user isn't interested in your apparent gender/clan combination.");
        }
        

        return hasAppearanceFilters
            ? (true, "Auto rejected: appearance unavailable")
            : (false, string.Empty);

        
    }

    private record DecodedAppearance(byte? Gender, byte? Race, byte? Clan, string RawBase64, string? DecodedJson, string? DecodeNotes);
    
    private async Task<DecodedAppearance?> ExtractAppearanceAsync(IntPtr characterAddress)
    {
        try
        {
            var glamourerState = await _ipcManager.Glamourer.GetCharacterCustomizationAsync(characterAddress).ConfigureAwait(false);
            if (string.IsNullOrEmpty(glamourerState))
                return null;

            var decoded = TryDecodeGlamourerState(glamourerState, out var gender, out var race, out var clan, out var decodeNotes);
            
            return new DecodedAppearance(gender, race, clan, glamourerState, decoded, decodeNotes);
            
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to extract appearance data");
            return null;
        }
    }

    private string? TryDecodeGlamourerState(string glamourerStateBase64, out byte? gender, out byte? race, out byte? clan, out string? decodeNotes)
    {
        gender = null;
        race = null;
        clan = null;
        decodeNotes = null;

        try
        {
            var rawBytes = Convert.FromBase64String(glamourerStateBase64);

            // Glamourer encodes its state as a GZip stream prefixed with a single version byte.
            // Strip the prefix if present so the gzip header (0x1F 0x8B) is the first byte before decoding to JSON.
            var gzipStart = Array.IndexOf(rawBytes, (byte)0x1F);
            if (gzipStart < 0 || gzipStart + 1 >= rawBytes.Length || rawBytes[gzipStart + 1] != 0x8B)
            {
                decodeNotes = "No gzip header found";
                return null;
            }

            using var memory = new MemoryStream(rawBytes, gzipStart, rawBytes.Length - gzipStart);
            using var gzip = new System.IO.Compression.GZipStream(memory, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var decoded = reader.ReadToEnd();

            decodeNotes = "decoded";
            TryParseAppearanceFromJson(decoded, out gender, out race, out clan, ref decodeNotes);

            return decoded;
        }
        catch (Exception ex)
        {
            decodeNotes = $"decode-failed: {ex.GetType().Name}";
            _logger.LogTrace(ex, "Failed to parse Glamourer state for appearance");
            return null;
        }
    }
    
        private void TryParseAppearanceFromJson(string decoded, out byte? gender, out byte? race, out byte? clan, ref string? decodeNotes)
    {
        gender = null;
        race = null;
        clan = null;

        try
        {
            using var document = JsonDocument.Parse(decoded);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                decodeNotes = "decode-ok:no-root-object";
                return;
            }

            if (!document.RootElement.TryGetProperty("Customize", out var customize) || customize.ValueKind != JsonValueKind.Object)
            {
                decodeNotes = "decode-ok:no-customize";
                return;
            }

            gender = ExtractByteFrom(customize, "Gender");
            race = ExtractByteFrom(customize, "Race");
            clan = ExtractByteFrom(customize, "Clan");

            decodeNotes = (gender, race, clan) switch
            {
                (null, null, null) => "decode-ok:customize-empty",
                _ => "decode-ok:customize-parsed"
            };
        }
        catch (Exception ex)
        {
            decodeNotes = $"decode-json-failed:{ex.GetType().Name}";
            _logger.LogTrace(ex, "Failed to parse Glamourer JSON");
        }
    }

    private static byte? ExtractByteFrom(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)) return null;
        return ExtractByteValue(element);
    }

    private static byte? ExtractByteValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Value", out var valueElement))
            return ExtractByteValue(valueElement);

        if (element.ValueKind == JsonValueKind.Number && element.TryGetByte(out var numberValue))
            return numberValue;

        if (element.ValueKind == JsonValueKind.String && byte.TryParse(element.GetString(), out var parsedValue))
            return parsedValue;

        return null;
    }

    
    
    private void OnTargetPlayerChanged(TargetPlayerChangedMessage message)
    {
        if (message.Character is not IPlayerCharacter playerCharacter)
            return;

        _ = LogTargetAppearanceAsync(playerCharacter);
    }

    private async Task LogTargetAppearanceAsync(IPlayerCharacter playerCharacter)
    {
        var appearance = await ExtractAppearanceAsync(playerCharacter.Address).ConfigureAwait(false);

        var name = playerCharacter.Name.ToString();
        var worldId = playerCharacter.HomeWorld.RowId;

        if (appearance == null)
        {
            _logger.LogDebug($"Targeted {name}@{worldId}: appearance could not be read");
            return;
        }

        var genderDisplay = appearance.Gender?.ToString() ?? "unknown";
        var raceDisplay = appearance.Race?.ToString() ?? "unknown";
        var clanDisplay = appearance.Clan?.ToString() ?? "unknown";
        var decodeNote = appearance.DecodeNotes ?? "decoded";

        _logger.LogDebug(
            $"Targeted {name}@{worldId}: detected gender={genderDisplay}, race={raceDisplay}, clan={clanDisplay}, rawBase64={appearance.RawBase64}, decodedJson={appearance.DecodedJson ?? "(decode failed)"}, notes={decodeNote}");
    }
}

public readonly record struct RequesterDisplay(string? Name, ushort? WorldId)
{
    public string NameOrUid => Name ?? string.Empty;
}
