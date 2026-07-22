using ElezenTools.UI.Mvu;
using Snowcloak.API.Data;

namespace Snowcloak.UI.PairingAvailability;

public sealed record SendPairRequestIntent(string Ident) : IIntent;

public sealed record ViewProfileIntent(string Ident) : IIntent;

public sealed record ReportProfileIntent(UserData User, string Ident, long Revision) : IIntent;

public sealed record ExaminePlayerIntent(string Ident, string DisplayName) : IIntent;

public sealed record OpenAdventurerPlateIntent(string Ident, string DisplayName) : IIntent;

public sealed record SetSearchQueryIntent(string Query) : IIntent;

public sealed record SetTagQueryIntent(string Query) : IIntent;

public sealed record SetOnlyWithProfilesIntent(bool Value) : IIntent;

public sealed record SetUseProfileCardsIntent(bool Value) : IIntent;

public sealed record SetLockedIntent(bool Locked) : IIntent;

public sealed record RefreshAvailabilityIntent : IIntent;

public sealed record RespondPairRequestIntent(Guid RequestId, bool Accepted) : IIntent;

public sealed record BlockPairRequesterIntent(Guid RequestId, string Uid) : IIntent;

public sealed record SetPairingEnabledIntent(bool Enabled) : IIntent;

public sealed record OpenFrostbrandPanelIntent : IIntent;

public sealed record ToggleAvailabilityWindowIntent : IIntent;
