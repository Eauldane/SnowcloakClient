using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.Group;
using Snowcloak.API.Dto.Roleplay;
using Snowcloak.API.Dto.User;
using Snowcloak.API.Dto.Venue;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Chat;
using Snowcloak.Services.Mediator;
using Snowcloak.WebAPI;

namespace Snowcloak.Services;

public sealed record RoleplayVenueEvent(VenueRegistryEntryDto Venue, VenueAdvertisementDto Advertisement);

public sealed class RoleplayClientService : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly PairManager _pairManager;
    private readonly ChatRoomRegistry _rooms;
    private readonly Lock _sync = new();
    private int _generation;
    private RpAvailabilityCardDto? _localAvailability;
    private RpAvailabilityCardDto? _ownAvailability;
    private readonly List<RoomInviteReceivedDto> _pendingInvites = [];
    private RpProfileDirectoryQueryDto _lastPeopleQuery = new();

    public RoleplayClientService(ILogger<RoleplayClientService> logger, SnowMediator mediator,
        ApiController apiController, PairManager pairManager, ChatRoomRegistry rooms) : base(logger, mediator)
    {
        _apiController = apiController;
        _pairManager = pairManager;
        _rooms = rooms;
        Mediator.Subscribe<ConnectedMessage>(this, message => _ = RefreshAsync());
        Mediator.Subscribe<DisconnectedMessage>(this, _ => Clear());
        Mediator.Subscribe<RpAvailabilityChangedMessage>(this, message => ApplyAvailability(message.Dto));
        Mediator.Subscribe<OpenRpSafetyChangedMessage>(this, message => ApplySafetyChange(message.State));
        Mediator.Subscribe<RpRoomUpdatedMessage>(this, message =>
        {
            _rooms.Upsert(message.Dto.Room);
            ApplyRoomUpdate(message.Dto.Room);
            Changed?.Invoke();
        });
        Mediator.Subscribe<RpRoomInviteReceivedMessage>(this, message =>
        {
            lock (_sync)
            {
                _pendingInvites.RemoveAll(invite => string.Equals(invite.Room.RoomId, message.Dto.Room.RoomId, StringComparison.Ordinal)
                    && string.Equals(invite.Inviter.UID, message.Dto.Inviter.UID, StringComparison.Ordinal));
                _pendingInvites.Add(message.Dto);
            }
            Changed?.Invoke();
        });
    }

    public event Action? Changed;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public RpProfileDirectoryConsentDto Consent { get; private set; } = new();
    public RpAvailabilityCardDto? OwnAvailability => _ownAvailability?.ExpiresAtUtc > DateTimeOffset.UtcNow ? _ownAvailability : null;
    public RpCurrentHooksDto CurrentHooks { get; private set; } = new();
    public RpProfileDirectoryListResponseDto People { get; private set; } = new();
    public RoomDirectoryListResponseDto Rooms { get; private set; } = new();
    public RpEventDirectoryListResponseDto PublicEvents { get; private set; } = new();
    public IReadOnlyList<RpEventDirectoryEntryDto> JoinedEvents { get; private set; } = [];
    public IReadOnlyList<RoleplayVenueEvent> VisibleVenueEvents { get; private set; } = [];
    public IReadOnlyDictionary<string, RpAvailabilityCardDto> VisibleCards { get; private set; } = new Dictionary<string, RpAvailabilityCardDto>(StringComparer.Ordinal);
    public IReadOnlyList<RoomInviteReceivedDto> PendingInvites
    {
        get
        {
            lock (_sync)
                return [.. _pendingInvites];
        }
    }

    public async Task RefreshAsync()
    {
        if (!_apiController.SupportsRpFeatures)
        {
            Clear();
            Status = "The connected server does not support roleplay features.";
            Changed?.Invoke();
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        IsBusy = true;
        Status = string.Empty;
        Changed?.Invoke();
        try
        {
            var consentTask = _apiController.RpProfileDirectoryGetConsent();
            var availabilityTask = _apiController.RpAvailabilityGetOwn();
            var hooksTask = _apiController.RpCurrentHooksGetOwn();
            var peopleTask = _apiController.RpProfileDirectoryList(_lastPeopleQuery);
            var roomsTask = _apiController.RpRoomDirectoryList(new RoomDirectoryQueryDto());
            var publicEventsTask = _apiController.RpEventDirectoryList(new RpEventDirectoryQueryDto
            {
                StartsAfterUtc = DateTime.UtcNow,
            });
            var joinedEventsTask = LoadJoinedEventsAsync();
            var venuesTask = _apiController.VenueRegistryList(new VenueRegistryListRequestDto(0, 50)
            {
                IncludeAds = true,
                IncludeUnlisted = false,
            });
            await Task.WhenAll(consentTask, availabilityTask, hooksTask, peopleTask, roomsTask, publicEventsTask, joinedEventsTask, venuesTask).ConfigureAwait(false);
            var consent = await consentTask.ConfigureAwait(false);
            var availability = await availabilityTask.ConfigureAwait(false);
            var hooks = await hooksTask.ConfigureAwait(false);
            var people = await peopleTask.ConfigureAwait(false);
            var rooms = await roomsTask.ConfigureAwait(false);
            var publicEvents = await publicEventsTask.ConfigureAwait(false);
            var joinedEvents = await joinedEventsTask.ConfigureAwait(false);
            var venues = await venuesTask.ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
                return;

            lock (_sync)
            {
                Consent = consent;
                if (_localAvailability?.ExpiresAtUtc > DateTimeOffset.UtcNow)
                    _ownAvailability = _localAvailability;
                else
                {
                    _localAvailability = null;
                    _ownAvailability = availability;
                }
                CurrentHooks = hooks;
                People = people;
                VisibleCards = People.Entries
                    .Where(entry => entry.Availability != null && !string.IsNullOrWhiteSpace(entry.Profile.Ident))
                    .ToDictionary(entry => entry.Profile.Ident, entry => entry.Availability!, StringComparer.Ordinal);
                Rooms = rooms;
                PublicEvents = publicEvents;
                JoinedEvents = joinedEvents;
                VisibleVenueEvents = venues.Registries
                    .SelectMany(venue => venue.Advertisements
                        .Where(ad => ad.IsActive && ad.StartsAt.HasValue && ad.EndsAt.GetValueOrDefault(ad.StartsAt.Value.AddHours(3)) >= DateTime.UtcNow)
                        .Select(ad => new RoleplayVenueEvent(venue, ad)))
                    .OrderBy(item => item.Advertisement.StartsAt)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                IsBusy = false;
                Changed?.Invoke();
                Mediator.Publish(new NameplateRedrawMessage());
            }
        }
    }

    public async Task SearchPeopleAsync(RpProfileDirectoryQueryDto query)
    {
        _lastPeopleQuery = query;
        People = await _apiController.RpProfileDirectoryList(query).ConfigureAwait(false);
        lock (_sync)
        {
            var cards = new Dictionary<string, RpAvailabilityCardDto>(VisibleCards, StringComparer.Ordinal);
            foreach (var entry in People.Entries.Where(entry => entry.Availability != null && !string.IsNullOrWhiteSpace(entry.Profile.Ident)))
                cards[entry.Profile.Ident] = entry.Availability!;
            VisibleCards = cards;
        }
        Changed?.Invoke();
        Mediator.Publish(new NameplateRedrawMessage());
    }

    public async Task SearchRoomsAsync(RoomDirectoryQueryDto query)
    {
        Rooms = await _apiController.RpRoomDirectoryList(query).ConfigureAwait(false);
        Changed?.Invoke();
        Mediator.Publish(new NameplateRedrawMessage());
    }

    public async Task SearchEventsAsync(RpEventDirectoryQueryDto query)
    {
        PublicEvents = await _apiController.RpEventDirectoryList(query).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task SetConsentAsync(bool listed)
    {
        Consent = await _apiController.RpProfileDirectorySetConsent(new RpProfileDirectoryConsentDto { Listed = listed }).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task SetAvailabilityAsync(RpAvailabilityCardUpdateDto update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Audience == Snowcloak.API.Data.Enum.RpAvailabilityAudience.LocalOnly)
        {
            if (OwnAvailability is { Audience: not RpAvailabilityAudience.LocalOnly })
                await _apiController.RpAvailabilityClear().ConfigureAwait(false);
            _localAvailability = new RpAvailabilityCardDto
            {
                State = update.State,
                Themes = [.. update.Themes],
                Audience = update.Audience,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(update.TtlMinutes),
                Paused = update.Paused,
                CurrentHook = CurrentHooks.Hooks.FirstOrDefault(hook => string.Equals(hook.HookId, update.CurrentHookId, StringComparison.Ordinal)),
            };
            _ownAvailability = _localAvailability;
            Changed?.Invoke();
            return;
        }
        _localAvailability = null;
        _ownAvailability = await _apiController.RpAvailabilitySet(update).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task ClearAvailabilityAsync()
    {
        if (OwnAvailability?.Audience != RpAvailabilityAudience.LocalOnly)
            await _apiController.RpAvailabilityClear().ConfigureAwait(false);
        _localAvailability = null;
        _ownAvailability = null;
        Changed?.Invoke();
    }

    public async Task SetCurrentHooksAsync(RpCurrentHooksUpdateDto update)
    {
        CurrentHooks = await _apiController.RpCurrentHooksSet(update).ConfigureAwait(false);
        Changed?.Invoke();
    }

    public void DismissInvite(RoomInviteReceivedDto invite)
    {
        lock (_sync)
            _pendingInvites.Remove(invite);
        Changed?.Invoke();
    }

    private async Task<List<RpEventDirectoryEntryDto>> LoadJoinedEventsAsync()
    {
        var fromUtc = DateTime.UtcNow;
        var toUtc = fromUtc.AddDays(90);
        var tasks = _pairManager.Groups.Values.Select(async group =>
        {
            var community = await _apiController.GroupGetCommunity(new GroupDto(group.Group)).ConfigureAwait(false);
            return community.Events.SelectMany(shellEvent => ExpandEvent(shellEvent, fromUtc, toUtc).Take(1)
                .Select(occurrence => new RpEventDirectoryEntryDto
                {
                    Group = group.Group,
                    Event = occurrence,
                }));
        }).ToArray();
        var events = await Task.WhenAll(tasks).ConfigureAwait(false);
        return events.SelectMany(entries => entries)
            .Where(entry => entry.Event.EndsAtUtc.GetValueOrDefault(entry.Event.StartsAtUtc.AddHours(1)) >= fromUtc)
            .OrderBy(entry => entry.Event.StartsAtUtc)
            .ToList();
    }

    private void ApplyAvailability(RpAvailabilityChangedDto dto)
    {
        lock (_sync)
        {
            var cards = new Dictionary<string, RpAvailabilityCardDto>(VisibleCards, StringComparer.Ordinal);
            if (dto.Card == null)
                cards.Remove(dto.Ident);
            else
                cards[dto.Ident] = dto.Card;
            VisibleCards = cards;
        }
        Changed?.Invoke();
    }

    private void ApplySafetyChange(UserSafetyStateDto state)
    {
        var blockedUids = state.BlockedUsers.Select(block => block.User.UID).ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            var removedIdents = People.Entries
                .Where(entry => entry.Profile.User != null && blockedUids.Contains(entry.Profile.User.UID)
                                || !state.AdultContentEnabled && entry.Profile.ContentRating == ProfileContentRating.Adult)
                .Select(entry => entry.Profile.Ident)
                .ToHashSet(StringComparer.Ordinal);
            People = People with
            {
                Entries = People.Entries.Where(entry => !removedIdents.Contains(entry.Profile.Ident)).ToList(),
                TotalCount = Math.Max(0, People.TotalCount - removedIdents.Count),
            };
            VisibleCards = VisibleCards
                .Where(entry => !removedIdents.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }
        Changed?.Invoke();
        Mediator.Publish(new NameplateRedrawMessage());
        _ = RefreshAsync();
    }

    private void Clear()
    {
        Interlocked.Increment(ref _generation);
        lock (_sync)
        {
            Consent = new();
            _ownAvailability = null;
            CurrentHooks = new();
            People = new();
            Rooms = new();
            PublicEvents = new();
            JoinedEvents = [];
            VisibleVenueEvents = [];
            VisibleCards = new Dictionary<string, RpAvailabilityCardDto>(StringComparer.Ordinal);
        }
        IsBusy = false;
        Status = string.Empty;
        Changed?.Invoke();
    }

    private void ApplyRoomUpdate(Snowcloak.API.Data.RoomData room)
    {
        lock (_sync)
        {
            var entries = Rooms.Entries.ToList();
            var index = entries.FindIndex(entry => string.Equals(entry.Room.RoomId, room.RoomId, StringComparison.Ordinal));
            if (room.Discovery?.IsListed == true)
            {
                if (index >= 0)
                    entries[index] = entries[index] with { Room = room };
                else
                    entries.Add(new RoomDirectoryEntryDto { Room = room });
            }
            else if (index >= 0)
            {
                entries.RemoveAt(index);
            }
            Rooms = Rooms with { Entries = entries, TotalCount = Math.Max(entries.Count, Rooms.TotalCount + (index < 0 && room.Discovery?.IsListed == true ? 1 : index >= 0 && room.Discovery?.IsListed != true ? -1 : 0)) };
        }
    }

    private static IEnumerable<GroupEventDto> ExpandEvent(GroupEventDto source, DateTime fromUtc, DateTime toUtc, int limit = 512)
    {
        var start = DateTime.SpecifyKind(source.StartsAtUtc, DateTimeKind.Utc);
        var duration = source.EndsAtUtc.HasValue
            ? DateTime.SpecifyKind(source.EndsAtUtc.Value, DateTimeKind.Utc) - start
            : (TimeSpan?)null;
        var recurrence = source.Recurrence;
        if (recurrence == null)
        {
            if (start <= toUtc && (duration.HasValue ? start + duration.Value >= fromUtc : start >= fromUtc))
                yield return source with { StartsAtUtc = start, EndsAtUtc = duration.HasValue ? start + duration.Value : null };
            yield break;
        }

        var generated = 0;
        var emitted = 0;
        var maximum = Math.Clamp(recurrence.OccurrenceCount ?? limit, 1, limit);
        var until = recurrence.UntilUtc.HasValue
            ? DateTime.SpecifyKind(recurrence.UntilUtc.Value, DateTimeKind.Utc)
            : DateTime.MaxValue;
        foreach (var occurrence in EnumerateEventStarts(start, recurrence, until, maximum))
        {
            generated++;
            if (occurrence > toUtc || generated > maximum)
                yield break;
            if (duration.HasValue ? occurrence + duration.Value >= fromUtc : occurrence >= fromUtc)
            {
                yield return source with
                {
                    StartsAtUtc = occurrence,
                    EndsAtUtc = duration.HasValue ? occurrence + duration.Value : null,
                };
                emitted++;
                if (emitted >= limit)
                    yield break;
            }
        }
    }

    private static IEnumerable<DateTime> EnumerateEventStarts(DateTime start, GroupEventRecurrenceDto recurrence, DateTime until, int limit)
    {
        var interval = Math.Max(1, recurrence.Interval);
        if (recurrence.Frequency == RpEventRecurrenceFrequency.Weekly && recurrence.DaysOfWeekMask != 0)
        {
            var day = start.Date;
            var startWeek = StartOfWeek(start.Date);
            var yielded = 0;
            while (day <= until && yielded < limit)
            {
                var week = (int)((StartOfWeek(day) - startWeek).TotalDays / 7);
                var bit = 1 << (int)day.DayOfWeek;
                if (week >= 0 && week % interval == 0 && (recurrence.DaysOfWeekMask & bit) != 0)
                {
                    yielded++;
                    yield return DateTime.SpecifyKind(day + start.TimeOfDay, DateTimeKind.Utc);
                }
                day = day.AddDays(1);
            }
            yield break;
        }

        var current = start;
        for (var index = 0; index < limit && current <= until; index++)
        {
            yield return current;
            current = recurrence.Frequency switch
            {
                RpEventRecurrenceFrequency.Daily => current.AddDays(interval),
                RpEventRecurrenceFrequency.Weekly => current.AddDays(7 * interval),
                RpEventRecurrenceFrequency.Monthly => current.AddMonths(interval),
                _ => DateTime.MaxValue,
            };
        }
    }

    private static DateTime StartOfWeek(DateTime value) => value.AddDays(-(int)value.DayOfWeek);
}
