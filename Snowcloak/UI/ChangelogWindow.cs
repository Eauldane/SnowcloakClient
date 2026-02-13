using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.Configuration;
using Snowcloak.Services.Mediator;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using ElezenTools.UI;
using Snowcloak.Services;


namespace Snowcloak.UI;

public class ChangelogWindow : WindowMediatorSubscriberBase
{
    private readonly HashSet<string> _autoExpandVersions;
    private readonly SnowcloakConfigService _configService;
    private readonly string? _lastSeenVersionLabel;
    private readonly Version? _lastSeenVersion;
    private bool _shouldMarkVersionSeen;
    private readonly List<ChangelogEntry> _visibleEntries;
    private readonly string _currentVersionLabel;
    private readonly bool _isFreshInstall;
    private readonly UiSharedService _uiSharedService;
    private IDalamudTextureWrap? _logoTexture;
    private readonly IDalamudPluginInterface _pluginInterface;
    private bool _resetScrollNextDraw = true;

    public ChangelogWindow(ILogger<ChangelogWindow> logger, SnowMediator mediator, SnowcloakConfigService configService, UiSharedService uiSharedService, IDalamudPluginInterface pluginInterface, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Snowcloak Patch Notes", performanceCollectorService)
    {
        _configService = configService;
        _uiSharedService = uiSharedService;
        _pluginInterface = pluginInterface;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520f, 620f),
            MaximumSize = new Vector2(700f, 900f),
        };
        RespectCloseHotkey = false;

        var currentVersion = NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version);
        _currentVersionLabel = FormatVersion(currentVersion);
        _lastSeenVersion = ParseVersion(configService.Current.LastSeenPatchNotesVersion);
        _lastSeenVersionLabel = _lastSeenVersion == null ? null : FormatVersion(_lastSeenVersion);
        _isFreshInstall = !_configService.Current.InitialScanComplete && _configService.Current.LastSeenPatchNotesVersion.IsNullOrEmpty();

        var changelogEntries = CreateChangelogEntries(currentVersion);
        _visibleEntries = (_isFreshInstall ? changelogEntries.Take(1) : changelogEntries).ToList();

        _autoExpandVersions = DetermineAutoExpandEntries(_visibleEntries, _lastSeenVersion);
        _shouldMarkVersionSeen = CompareVersions(currentVersion, _lastSeenVersion) > 0;
        
        LoadHeaderLogo();

        if (_shouldMarkVersionSeen && configService.Current.ShowChangelog)
        {
            IsOpen = true;
        }
    }

    public override void OnOpen()
    {
        base.OnOpen();

        if (_shouldMarkVersionSeen)
        {
            _configService.Current.LastSeenPatchNotesVersion = _currentVersionLabel;
            _configService.Save();
            _shouldMarkVersionSeen = false;
        }
        _resetScrollNextDraw = true;
    }

    protected override void DrawInternal()
    {
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2 + ImGui.GetStyle().WindowPadding.Y;
        var contentSize = new Vector2(-1, -footerHeight);
        
        if (ImGui.BeginChild("ChangelogContent", contentSize, false, ImGuiWindowFlags.NoSavedSettings))
        {
            if (_resetScrollNextDraw)
            {
                ImGui.SetScrollY(0);
                _resetScrollNextDraw = false;
            }

            DrawHeader();
            ImGui.TextUnformatted($"Snowcloak updated to version {_currentVersionLabel}.");
            if (_lastSeenVersionLabel != null)
            {
                ElezenImgui.WrappedText($"Last patch notes viewed: {_lastSeenVersionLabel}");
            }
            if (_isFreshInstall)
            {
                ElezenImgui.ColouredWrappedText("Welcome! Showing the latest notes for your first install.", ImGuiColors.DalamudGrey);
            }
            else if (_autoExpandVersions.Count > 0)
            {
                ElezenImgui.ColouredWrappedText("Newer versions since your last visit are expanded below.", ImGuiColors.DalamudGrey);
            }

            ImGui.Separator();

            foreach (var entry in _visibleEntries)
            {
                var flags = ImGuiTreeNodeFlags.FramePadding;
                if (_autoExpandVersions.Contains(entry.VersionLabel))
                {
                    flags |= ImGuiTreeNodeFlags.DefaultOpen;
                }

                if (!ImGui.CollapsingHeader(entry.HeaderLabel, flags))
                {
                    continue;
                }

                ImGui.PushID(entry.VersionLabel);
                DrawEntry(entry);
                ImGui.PopID();
            }
            ImGui.EndChild();
            var buttonWidth = 120f * ImGuiHelpers.GlobalScale;
            var cursorX = (ImGui.GetContentRegionAvail().X - buttonWidth) / 2f;
            if (cursorX > 0)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cursorX);
            }

            if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
            {
                IsOpen = false;
            }

        }
    }

    private static HashSet<string> DetermineAutoExpandEntries(IEnumerable<ChangelogEntry> entries, Version? lastSeenVersion)
    {
        return entries
            .Where(entry => CompareVersions(entry.Version, lastSeenVersion) > 0)
            .Select(entry => entry.VersionLabel)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static int CompareVersions(Version candidate, Version? baseline)
    {
        if (baseline == null)
        {
            return 1;
        }

        var normalizedCandidate = NormalizeForComparison(candidate);
        var normalizedBaseline = NormalizeForComparison(baseline);

        return normalizedCandidate.CompareTo(normalizedBaseline);
    }

    private static Version NormalizeForComparison(Version version)
        {
            static int NormalizePart(int part) => Math.Max(0, part);

            return new Version(
                NormalizePart(version.Major),
                NormalizePart(version.Minor),
                NormalizePart(version.Build),
                NormalizePart(version.Revision));
    }
    
    private void DrawHeader()
    {
        if (_logoTexture == null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var paddingY = ImGui.GetStyle().ItemSpacing.Y;

        var scale = Math.Min(1f, contentWidth / _logoTexture.Width);
        var size = new Vector2(_logoTexture.Width, _logoTexture.Height) * scale;

        var headerWidth = Math.Max(contentWidth, size.X);
        var headerHeight = size.Y + paddingY * 2;
        var headerStart = ImGui.GetCursorScreenPos();
        var headerEnd = headerStart + new Vector2(headerWidth, headerHeight);

        var headerColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.027f, 0.071f, 0.149f, 1f)); // #071226
        drawList.AddRectFilled(headerStart, headerEnd, headerColor);

        var centeredX = headerStart.X + (headerWidth - size.X) / 2f;
        var imagePos = new Vector2(centeredX, headerStart.Y + paddingY);
        drawList.AddImage(_logoTexture.Handle, imagePos, imagePos + size);

        ImGui.Dummy(new Vector2(headerWidth, headerHeight));
    }

    private void LoadHeaderLogo()
    {
        try
        {
            var pluginDir = _pluginInterface.AssemblyLocation.DirectoryName;
            if (pluginDir.IsNullOrEmpty())
            {
                return;
            }

            var logoPath = Path.Combine(pluginDir, "Assets", "changelogheader.png");
            if (!File.Exists(logoPath))
            {
                return;
            }

            _logoTexture = _uiSharedService.LoadImage(File.ReadAllBytes(logoPath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load changelog header logo...");
        }
    }

    private static void DrawEntry(ChangelogEntry entry)
    {
        var wrap = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.PushTextWrapPos(wrap);
        foreach (var section in entry.Sections)
        {
            ImGui.TextUnformatted(section.Title);
            foreach (var note in section.Notes)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                var noteWrap = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
                ImGui.PushTextWrapPos(noteWrap);
                ImGui.TextUnformatted(note);
                ImGui.PopTextWrapPos();            }

            ImGui.Spacing();
        }
        ImGui.PopTextWrapPos();
    }

    private static List<ChangelogEntry> CreateChangelogEntries(Version currentVersion)
    {
        var entries = new List<ChangelogEntry>
        {
            new(VersionFromString("2.2.3"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Added a way for the server to broadcast news so that planned maintenance, upcoming patches, known issues etc. can be communicated easier to those not in the Discord.",
                    "Adjusted texture compression preferences to allow selecting \"whatever's equipped\" with no server-side adjustment. This should help with items turning black.",
                    "When \"prefer compressed\" is enabled, the server can now selectively refuse to send known bad files once they've been reported to us and verified."
                ])
            ]),
            new(VersionFromString("2.2.2"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Patreon subscribers can now bypass the member cap on syncshells.",
                    "Character Analysis is now available for paired users to help you identify who's heavy in crowded areas.",
                    "Class abbreviations are now coloured in Frostbrand's nearby pairs window.",
                    "Profile windows now show a pair's active moodles."
                ])
            ]),
            new(VersionFromString("2.2.1"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Fixed an issue where uploads could cause a (non-fatal) UI crash if too many were being queued simultaneously."
                ])
            ]),
            new(VersionFromString("2.2.0"),
            [
                new ChangelogSection("New Features",
                [
                    "Snowcloak can now request the original versions of compressed textures, and vice versa.",
                    "A toggle has been added to the Performance tab of the settings window.",
                    "If the server only has one version of a texture, that version will be sent.",
                    "If the server has BOTH versions of a texture, it'll send the version appropriate for your choice, regardless of which version a synced user is wearing on their end.",
                    "Changes take effect on characters already drawn when you next receive an outfit update from them."
                ]),
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "More chat fixes than I can remember. The chat system is still considered unstable, and under heavy development.",
                    "Updated SCF format to version 3 for future extensibility.",
                    "Internal reworks and overhauls.",
                    "Linux users can now use hidden paths (courtesy of @claria-tan on GitHub!)"
                ])
            ]),
            new(VersionFromString("2.1.3"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Added settings option to disable changelog popups in the general tab.",
                    "Cleaned up some unused settings options.",
                    "Fixed an issue where connecting would leave syncshell chats in a strange semi-online state.",
                    "DM list in the chat window now only show users who are online or using the desktop app.",
                    "Users you've sent a message to in the current \"session\" will not be removed from the DM list if they go offline.",
                    "Chats with unread messages now show in a different colour."
                ])
            ]),
            new(VersionFromString("2.1.2"),
                [
                new ChangelogSection("Changes and Bug Fixes",
                    [
                        "Added a clarifier on the chat button that's it's beta.",
                        "Syncshell chats now require users to explicitly join rather than dumping them in automatically.",
                        "Chat user lists now properly update.",
                        "Users who go offline are now properly 'disconnected' from the chat.",
                        "Miscellaneous internal code cleanup."
                    ])
            ]),
            new(VersionFromString("2.1.1"),
            [
            new ChangelogSection("Changes and Bug Fixes",
                [
                    "Venue ads now correctly show vanity colours for the venue.",
                    "Venue ad text limit changed to 2,000 characters.",
                    "Changed venue ads to show the banner above the text instead of below.",
                    "Remembered that people sometimes send more than two words at a time through chat, and added text wrapping. Idiot."
                    ])
                ]),
            new(VersionFromString("2.1.0"),
                [
                new ChangelogSection("New Feature: Venue Ads",
                    ["A button has been added in the main window to open the venue panel.", 
                     "This panel will show any active venue advertisements for any region your current character has access to (e.g. NA and OCE).",
                     "This panel also simplifies venue registration rather than having to remember a command.",
                     "Venue ads support a short burst of text, and a 700x320 banner ad. You can also set a day and start time, which is localised to the viewer's time.",
                     "Venues with an event that's in progress will show at the top, followed by the venues with events starting soonest.",
                     "Venue ads auto-clear three hours after an event starts.",
                     "This feature will be iterated on in subsequent patches, so feel free to give feedback!",
                     "We've also removed the XIVAuth requirement for registering a venue."
                    ]),
                new ChangelogSection("New Feature: Chat",
                    ["An upgraded chat feature has been added.",
                        "This replaces the existing syncshell chat, and extends it to support 1-on-1 chats with paired users, as well as standard chat channels.",
                        "Channels support unlimited users, allowing you to bypass CWLS member caps.",
                        "You do not need to be on the same datacentre to talk to people through Snowcloak chat.",
                        "You do not need to be subscribed to the game to use Snowcloak chat.",
                        "A desktop app is available so that you can chat out of game, without having to give out your Discord. The desktop app uses your Snowcloak secret key, and will automatically retrieve paired users and syncshell chats.",
                        "This feature is considered BETA."
                    ]),
                new ChangelogSection("Misc New Features",
                [
                    "Additional texture compression options have been added. Snowcloak will automatically choose the optimal format to convert to.",
                    "The UI has been tweaked."
                    ]),
                new ChangelogSection("Changes and Bug Fixes",
                    [
                    "Snowcloak's texture heuristics have been improved.",
                    "Texture analysis now handles things like dark skin tones better.",
                    "Texture analysis now handles alpha channels better.",
                    "SCFs have been extended with new compression options; Snowcloak will automatically choose the best format for an upload.",
                    "Improved pairing stability for new pairs and syncshell members.",
                    "Frostbrand will send checkpoints every 5 seconds instead of 15.",
                    "Fixed some UI issues.",
                    "You no longer need to be standing on your plot to update an existing venue's infoplate or ad.",
                    "Removed code that's no longer needed.",
                    "Frostbrand is now per character instead of global."
                    ])
            ]),
            new(VersionFromString("2.0.8"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Nameplates are now coloured with vanity colours if a user has one set.",
                    "Server restarts should be handled a bit more gracefully by the client now.",
                    "Fixed the wrong error message being shown sometimes in a certain place."
                ])
            ]),
            new(VersionFromString("2.0.7"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Patreon backers who use XIVAuth are now able to set vanity colours for their name. (The Patreon can be accessed through the account management portal, or with the little heart button in the main window)",
                    "The Vanity ID setter introduced last patch now also allows setting a colour, if you're eligible for one.",
                    "Added a settings toggle to hold uploads until a pair is in range.",
                    "Updating vanity IDs/colours now automatically updates both your own and other users' clients.",
                    "Fixed some typos."
                ])
            ]),
            new(VersionFromString("2.0.6"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Vanity UIDs can now be set directly from the client if you use XIVAuth to log in.",
                    "Adjusted internal logic to reduce the likelihood of having to reconnect to get your game to recognise someone is nearby.",
                    "Frostbrand can now be configured to only allow pair requests from friends.",
                    "Adjusted public syncshell join error to be more clear that the shell you're trying to join is probably full, rather than a generic error."
                ])
            ]),
            new(VersionFromString("2.0.5"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Frostbrand no longer requires XIVAuth. Given the one-on-one nature and filters, it was deemed safe enough to remove the requirement. Public syncshells still require XIVAuth.",
                    "Upgraded internal dependencies."
                ])
            ]),
            new(VersionFromString("2.0.4"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                    "Added a button to log in with XIVAuth on the Service Management tab in settings that will automatically replace invalid secret keys that have been deleted for inactivity.",
                    "Adjusted autopause notifications to not display again when the paused user changes their outfit until they've switched to an outfit below your thresholds."
                ])
            ]),
            new(VersionFromString("2.0.3"),
            [
                new ChangelogSection("Changes and Bug Fixes",
                [
                   "Fixed malformed pairing requests being sent if both sender and recipient logged off and reconnected.",
                   "Updated Blake3 hashing implementation.",
                   "Fixed BC7 checkbox unchecking itself if Snowcloak thinks it's a risky texture.",
                   "Adjusted wording of the 'Unauthorised' message to let people know about the inactivity period."
                ])
            ]),
            new(VersionFromString("2.0.2"),
            [
                new ChangelogSection("Changes",
                [
                    "Improved download performance and fixed a potential error when files were downloading while changing areas or running through congested areas.",
                    "Fixed Mac and Linux users being unable to set a storage directory sometimes due to hidden OS files.",
                    "Fixed a rare error related to changing areas when mods were being applied to a pair.",
                    "Fixed some logging levels to be more appropriate to the severity.",
                    "XIVAuth now allows you to re-request permissions for characters if you verify some during the authentication flow, rather than having to start from scratch."
                ]),
                new ChangelogSection("Hotfix Rollup",
                [
                    "Fixed DTR icon for Frostbrand not showing a zero if nobody was in range.",
                    "Moodles IPC fixes.",
                    "Fixed a rare error on Frostbrand's panel.",
                    "Frostbrand DTR icon now shows pending requests in brackets next to the nearby value."
                ])
            ]),
            new(VersionFromString("2.0.1"),
            [
                new ChangelogSection("Changes",
                [
                    "Frostbrand has been moved into the main UI rather than tucked away in settings.",
                    "Frostbrand DTR icon will be a nice shade of blue when you have pending requests (configurable!)",
                    "Fixed an error message."
                ])
            ]),
            new(VersionFromString("2.0.0"),
                [
                    new ChangelogSection("New Feature: Frostbrand",
                        [
                            "You can now enable a setting to advertise that you're open for pairs. This setting is OFF by default. The features described below are all disabled until you opt-in - if you don't turn it on, things work as you're used to.",
                            "Frostbrand will colour the nameplate of anyone nearby who's opted in. The colour is configurable.",
                            "Right clicking a user will let you view their public profile and send a pair request.",
                            "Filters can be configured to automatically reject users you're not interested in based on level, gender, species, and homeworld. No need to stand around pretending to be AFK if someone you don't like sends a request!",
                            "Default filters set to reject anyone below level 15, to ensure they might have Saucer emotes. This can be turned off.",
                            "Users outside of examine range will go to pending requests as if they were unfiltered. When they wander back into range, the filters will run and reject them if needed.",
                            "If a pending request gets filtered, you'll be notified. The sender will only be notified if rejection was immediate.",
                            "An icon will appear in the server info bar, showing who's open to pairing nearby.",
                            "Clicking the icon will bring up a window that can be \"locked\" to browse easier.",
                            "The window - as well as nameplate colours - respect your filter options.",
                            "If someone sends you a request, you do not show in their pairs list until you accept.",
                            "For safety and moderation reasons, this feature can only be turned on when using XIVAuth logins."
                        ]),
                    new ChangelogSection("New Feature: Public Syncshells",
                    [
                        "You can now click a button on the Syncshells tab to join the syncshell for your region.",
                        "Public syncshells scale in maximum capacity as we add more servers to handle the load.",
                        "Each datacentre region has its own syncshell. The one you're able to join is determined by your characters homeworld.",
                        "Public syncshells have VFX and sound syncing disabled - direct pairs and other syncshells will override this setting for people.",
                        "Joining a public syncshell requires a XIVAuth login for safety and moderation reasons."
                    ]),
                    new ChangelogSection("Enhanced Venue Support",
                        [
                            "Venues wanting auto-join syncshells can now be registered entirely in-game, using the /venue command.",
                            "Using the above command while on your plot will also allow you to update your existing info.",
                            "Venue descriptions now support BBCode, allowing for images, text formatting, colour, and emoji.",
                            "Venue owners must be authenticating with XIVAuth for moderation and safety reasons."
                        ]),
                    new ChangelogSection("Profile Overhauls",
                        [
                            "You can now have public and private profiles.",
                            "Public profiles are used with Snowplow and lets you give a brief overview to potential new pairs.",
                            "Private profiles let you go a bit more in depth, and are visible only to paired users.",
                            "Both public and private profiles have BBCode support, the same way that venue infoplates do."
                        ]),
                    new ChangelogSection("Changes and Bug Fixes",
                    [
                        "Disabling VFX/Sound/Animations for individual syncshell members is now possible.",
                        "Data is now uploaded to the server without needing someone visible nearby first.",
                        "Auto-paused users now show a greyed out icon in the visible users list to make it more obviou that they're temporarily paused.",
                        "Auto-pausing now occurs before any files have been downloaded. If a user has a modset that'd be over your set thresholds, any missing files won't be downloaded.",
                        "Changing auto-pause thresholds will now re-evaluate nearby users instead of requiring one of you to disconnect/reconnect.",
                        "Skiff files have been upgraded to v2 with a few extensions to support the new features.",
                        "The Character Data Analysis window now analyses textures for suitability before converting to BC7, and will warn you about any risky conversions. It's not perfect, but it catches the worst offenders.",
                        "XIVAuth users are exempted from the inactivity cleanup now. Legacy key users are still removed after 90 days of inactivity.",
                        "Fixed MCDO permissions not working for allowed syncshells."
                    ]),
                    new ChangelogSection("Hotfix 2.0.0.1",
                    [
                        "Fixed a broken window title.",
                        "Removed some debug popups.",
                    ]),
                ]),
            new(VersionFromString("1.0.2.1"),
                [
                    new ChangelogSection("Changes",
                    [
                        "Fixed a rare(?) race condition where excessive error logs would be generated, creating lag."
                    ])
                ]),
            new(VersionFromString("1.0.2"),
                [
                    new ChangelogSection("Changes",
                    [
                        "Added command /animsync to force animations of you, your target, and party members to line up with each other. I'm sure this'll be used for only the purest reasons. Note: Only affects synced players, and only on your end.",
                        "Reworked file hashing and decompression to improve performance (probably)"
                    ])
                ]),
            new(VersionFromString("1.0.0"),
                [
                    new ChangelogSection("New features",
                    [
                        "Users can now choose to auto-join (after a confirmation prompt) the syncshells of venues registered with us upon approaching their location. This setting can be toggled.",
                        "A command has been added for venues to get their plot ID."
                    ]),
                    new ChangelogSection("Bug Fixes and Changes",
                    [
                        "Fixed trying to pause someone in a syncshell pausing the entire shell instead if you weren't directly paired.",
                        "Pausing someone no longer tells them they're paused, and they'll see you as offline (note: This may require a pause toggle to take effect).",
                        "Fixed an issue where writing file statistics could cause lag on lower-end systems.",
                        "Optimised the local file cache database. The new system will use up to 90% less disk space. The changes will automatically apply after installing the update.",
                        "Hotfix 1.0.1: Changed wording to make it clear that venue autojoin requires confirmation."
                    ])
                ]),
            new(VersionFromString("0.4.2"),
                [
                    new ChangelogSection("New features",
                    [
                        "Added extra cache clearing methods.",
                        "Added a slider to the settings page to control how compressed the files you upload are."
                    ]),
                    new ChangelogSection("Changes",
                    [
                        "Raised syncshell caps.",
                        "Added warnings/disclaimers on syncshell create/join.",
                        "Vanity ID length limit increased to 25 characters."
                    ])
                ]),
            new(VersionFromString("0.4.1"),
                [
                    new ChangelogSection("Changes",
                    [
                        "Clients who haven't updated to 0.4.0 will get progress messages during rehashing of old files."
                    ])
                ]),
            new(VersionFromString("0.4.0"),
                [
                    new ChangelogSection("New features",
                    [
                        "The file upload/download system has been rewritten. It should now use between 20 and 50% less data."
                    ]),
                    new ChangelogSection("Changes",
                    [
                        "Hashing format used by Snowcloak has been replaced with a more efficient algorithm.",
                        "File compactor service has been rewritten to not kill your SSD."
                    ])
                ]),
            new(VersionFromString("0.3.2"),
                [
                    new ChangelogSection("New features",
                    [
                        "Added a setting to autofill empty notes with player names, defaulting to on."
                    ]),
                    new ChangelogSection("Changes",
                    [
                        "Added a button to open the account management site in the main window.",
                        "Registering with XIVAuth now polls the server every 5 seconds instead of 15 to make it faster.",
                        "Added a warning for rare crashes instead of silently failing.",
                        "Profile windows now show vanity colours."
                    ])
                ]),
            new(VersionFromString("0.3.1"),
                [
                    new ChangelogSection("New features",
                    [
                        "All users who authenticate with XIVAuth can now set a vanity UID without needing staff intervention and without charge via the web UI.",
                        "Snowcloak now supports variable syncshell sizes. Large FCs and venues can request a higher member limit and vanity shell ID using this Google form and agreeing to some rules.",
                        "FC and venue Syncshells can now request a custom colour in the syncshell list through the above method."
                    ]),
                    new ChangelogSection("Changes",
                    [
                        "Syncshells no longer count the owner as part of the member limit.",
                        "Client will now show vanity colours for users and syncshells if one is set.",
                        "XIVAuth is no longer considered experimental."
                    ])
                ]),
            new(VersionFromString("0.3.0"),
                [
                    new ChangelogSection("New features",
                    [
                        "A much improved UI, courtesy of @Leyla",
                        "XIVAuth has been added as an authentication option.",
                        "Initial build of the web UI is now available."
                    ])
                ]),
            new(VersionFromString("0.2.4"),
                [
                    new ChangelogSection("Changes",
                    [
                        "Added /snow as an alias to bring up the main window.",
                        "Many UI tweaks.",
                        "VRAM sort now treats it as an actual number, and orders things in a way that actually makes sense",
                        "General code cleanup and optimisations."
                    ])
                ]),
            new(VersionFromString("0.2.3"),
                [
                    new ChangelogSection("Bug fixes",
                    [
                        "Fixed some stuff relating to pausing people in syncshells (note; There are known issues with this that are still being investigated)",
                        "Client and server now show each other some grace on temporary connection interruptions. Instances of reconnect and sync issues should now be significantly reduced, if not eliminated."
                    ]),
                    new ChangelogSection("Changes",
                    [
                        "Syncshell limit increased to 150 (we checked, it actually applied this time!)"
                    ])
                ]),
            new(VersionFromString("0.2.2"),
                [
                    new ChangelogSection("New features",
                        [
                            "[Experimental] Syncshell users can be sorted by VRAM usage with a toggle in the settings panel."
                        ]),
                    new ChangelogSection("Changes",
                        [
                            "Pausing people in syncshells is easier now."
                        ])
                ]),
            new(VersionFromString("0.2.1"),
                [
                    new ChangelogSection("Changes",
                        [
                            "Moodles IPC updated.",
                            "Profile pictures that are exactly 256px on a given dimension won't be interpreted as being 65k-ish instead and can be uploaded now.",
                            "Started writing actual patch notes."
                        ]),
                ])
        };

        return entries.OrderByDescending(e => e.Version).ToList();
    }

    private static Version NormalizeVersion(Version? version)
    {
        version ??= new Version(0, 0, 0, 0);
        var build = Math.Max(0, version.Build);
        var revision = Math.Max(0, version.Revision);
        return new Version(version.Major, version.Minor, build, revision);
    }

    private static Version? ParseVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
        {
            return null;
        }

        var split = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (split.Length is < 3 or > 4)
        {
            return null;
        }

        if (!int.TryParse(split[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return null;
        }
        if (!int.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }
        if (!int.TryParse(split[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var build))
        {
            return null;
        }
        var revision = split.Length == 4 && int.TryParse(split[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rev)
            ? rev
            : 0;

        return new Version(major, minor, build, revision);
    }

    private static string FormatVersion(Version version)
    {
        var parts = new List<int>
        {
            version.Major,
            version.Minor,
            Math.Max(0, version.Build)
        };

        if (version.Revision > 0)
        {
            parts.Add(version.Revision);
        }

        return string.Join('.', parts.Select(p => p.ToString(CultureInfo.InvariantCulture)));
    }

    private static Version VersionFromString(string versionText)
    {
        return NormalizeVersion(ParseVersion(versionText));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _logoTexture?.Dispose();
        _logoTexture = null;
    }

    private sealed record ChangelogEntry(Version Version, IReadOnlyList<ChangelogSection> Sections)
    {
        public string VersionLabel => FormatVersion(Version);
        public string HeaderLabel => $"Version {VersionLabel}##Changelog{VersionLabel}";
    }

    private sealed record ChangelogSection(string Title, IReadOnlyList<string> Notes);
}