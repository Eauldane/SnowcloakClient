using ElezenTools.UI.Mvu;

namespace Snowcloak.UI.PairingAvailability;

/// <summary>Send a Snowcloak pair request to the player.</summary>
public sealed record SendPairRequestIntent(string Ident) : IIntent;

/// <summary>Open the player's Snowcloak profile.</summary>
public sealed record ViewProfileIntent(string Ident) : IIntent;

/// <summary>Open the in-game examine window for the player.</summary>
public sealed record ExaminePlayerIntent(string Ident, string DisplayName) : IIntent;

/// <summary>Open the in-game adventurer plate for the player.</summary>
public sealed record OpenAdventurerPlateIntent(string Ident, string DisplayName) : IIntent;

/// <summary>Set the free-text profile search filter.</summary>
public sealed record SetSearchQueryIntent(string Query) : IIntent;

/// <summary>Set the required-tag filter.</summary>
public sealed record SetTagQueryIntent(string Query) : IIntent;

/// <summary>Toggle "only show players with a meaningful profile".</summary>
public sealed record SetOnlyWithProfilesIntent(bool Value) : IIntent;

/// <summary>Switch between the card and table render modes.</summary>
public sealed record SetUseProfileCardsIntent(bool Value) : IIntent;

/// <summary>Lock (pause live updates) or unlock the list.</summary>
public sealed record SetLockedIntent(bool Locked) : IIntent;

/// <summary>Force a server-side refresh of nearby availability.</summary>
public sealed record RefreshAvailabilityIntent : IIntent;

public sealed record RespondPairRequestIntent(Guid RequestId, bool Accepted) : IIntent;

public sealed record SetPairingEnabledIntent(bool Enabled) : IIntent;

public sealed record OpenFrostbrandPanelIntent : IIntent;

public sealed record ToggleAvailabilityWindowIntent : IIntent;
