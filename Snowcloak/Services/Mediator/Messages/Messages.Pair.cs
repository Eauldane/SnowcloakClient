using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Dto.User;
using Snowcloak.PlayerData.Pairs;

namespace Snowcloak.Services.Mediator;

#pragma warning disable MA0048 // File name must match type name
#pragma warning disable S2094
public record ClearProfileDataMessage(UserData? UserData = null, ProfileVisibility? Visibility = null) : MessageBase;
public record ClearCharacterProfileDataMessage(string? Ident = null, ProfileVisibility? Visibility = null, bool PreserveSummary = false) : MessageBase;
public record CyclePauseMessage(UserData UserData) : MessageBase;
public record PauseMessage(UserData UserData) : MessageBase;
public record TargetPairMessage(Pair Pair) : MessageBase;
public record HoldPairApplicationMessage(string UID, string Source) : KeyedMessage(UID);
public record UnholdPairApplicationMessage(string UID, string Source) : KeyedMessage(UID);
public record HoldPairDownloadsMessage(string UID, string Source) : KeyedMessage(UID);
public record UnholdPairDownloadsMessage(string UID, string Source) : KeyedMessage(UID);
public record PairDataAppliedMessage(string UID, CharacterData? CharacterData) : KeyedMessage(UID);
public record PairApplicationCompletedMessage(string UID, CharacterData CharacterData) : KeyedMessage(UID);
public record LocalCharacterDataPushedMessage(IReadOnlyList<UserData> Recipients, string DataHash) : MessageBase;
public record RequestPairDataMessage(UserData UserData) : MessageBase;
public record PairDataAnalyzedMessage(string UID) : KeyedMessage(UID);
public record PairingAvailabilityChangedMessage : MessageBase;
public record PairingRequestReceivedMessage(PairingRequestDto Request) : MessageBase;
public record PairingRequestListChangedMessage : MessageBase;
#pragma warning restore S2094
#pragma warning restore MA0048 // File name must match type name
