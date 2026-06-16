using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
using Snowcloak.Core.PlayerData;
using Snowcloak.Interop.Ipc;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.Mediator;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.UI;
using Snowcloak.WebAPI.Files;
using Snowcloak.WebAPI.Files.Models;

namespace Snowcloak.Services;

public sealed class SyncTroubleshootingService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly SnowcloakConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly FileTransferOrchestrator _fileTransferOrchestrator;
    private readonly IpcManager _ipcManager;
    private readonly BlockListStore _blockListStore;
    private readonly DownloadStatusStore _statusStore;

    public SyncTroubleshootingService(ILogger<SyncTroubleshootingService> logger, SnowMediator mediator,
        SnowcloakConfigService configService, DalamudUtilService dalamudUtilService,
        FileTransferOrchestrator fileTransferOrchestrator, DownloadStatusStore statusStore, IpcManager ipcManager,
        BlockListStore blockListStore)
        : base(logger, mediator)
    {
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _statusStore = statusStore;
        _ipcManager = ipcManager;
        _blockListStore = blockListStore;

    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeAll();
        return Task.CompletedTask;
    }

    public SyncTroubleshootingReport BuildReport(Pair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        List<SyncTroubleshootingFinding> findings = [];
        List<SyncTroubleshootingSection> sections = [];
        List<string> localState = [];
        List<string> permissions = [];
        List<string> transferState = [];
        List<string> dataState = [];
        List<string> pluginState = [];

        bool isBlacklisted = _blockListStore.IsUserBlacklisted(pair.UserData);
        bool isWhitelisted = _blockListStore.IsUserWhitelisted(pair.UserData);
        bool hasDirectPair = pair.UserPair != null;
        bool isMutualDirectPair = hasDirectPair
            && pair.UserPair!.OwnPermissions.IsPaired()
            && pair.UserPair.OtherPermissions.IsPaired();
        bool isDirectPausedByYou = pair.UserPair?.OwnPermissions.IsPaused() ?? false;
        bool isDirectPausedByThem = pair.UserPair?.OtherPermissions.IsPaused() ?? false;
        var groupEntries = pair.GroupPair
            .OrderBy(entry => entry.Key.Group.AliasOrGID, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var activeGroupRoutes = groupEntries
            .Where(entry => !entry.Key.GroupUserPermissions.IsPaused()
                            && !entry.Value.OwnGroupUserPermissions.IsPaused()
                            && !entry.Value.OtherGroupUserPermissions.IsPaused())
            .ToList();
        var groupRoutesHiddenByRemotePause = groupEntries
            .Where(entry => !entry.Key.GroupUserPermissions.IsPaused()
                            && !entry.Value.OwnGroupUserPermissions.IsPaused()
                            && entry.Value.OtherGroupUserPermissions.IsPaused())
            .ToList();
        var activeDirectRoute = isMutualDirectPair && !isDirectPausedByYou && !isDirectPausedByThem;
        var directRouteHiddenByRemotePause = isMutualDirectPair && !isDirectPausedByYou && isDirectPausedByThem;
        var showAsOfflineForRemotePause = !activeDirectRoute
            && activeGroupRoutes.Count == 0
            && (directRouteHiddenByRemotePause || groupRoutesHiddenByRemotePause.Count > 0);
        var onlineForReport = pair.IsOnline && !showAsOfflineForRemotePause;
        var visibleForReport = pair.IsVisible && !showAsOfflineForRemotePause;
        var currentDownloads = _statusStore.SnapshotForUid(pair.UserData.UID)?.Groups
            .OrderBy(group => group.Server, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        List<string> globalBlockers = [];
        foreach (var status in _ipcManager.GetRequiredStatuses().Where(status => !status.IsAvailable))
        {
            globalBlockers.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1}", status.Name, DescribeIpcIssue(status)));
        }
        if (_dalamudUtilService.IsZoning)
            globalBlockers.Add("you are zoning");
        if (_dalamudUtilService.IsInCutscene)
            globalBlockers.Add("you are in a cutscene");
        if (_dalamudUtilService.IsInGpose)
            globalBlockers.Add("you are in GPose");
        if (ApplicationHoldPolicy.ShouldHoldForCombatOrPerformance(_configService.Current.HoldCombatApplication,
            _dalamudUtilService.IsInCombatOrPerforming))
            globalBlockers.Add("Hold application during combat is active while you are in combat/performance");

        List<string> matchingForbiddenTransfers = GetForbiddenTransferMatches(pair);
        List<string> optionalPluginBlocks = GetOptionalPluginBlocks(pair.LastReceivedCharacterData);

        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Snapshot: client-side only, generated {0:u}", DateTime.UtcNow));
        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Presence: online={0}, visible={1}, chat-only={2}, player={3}",
            YesNo(onlineForReport), YesNo(visibleForReport), YesNo(pair.IsChatOnly), visibleForReport ? pair.PlayerName ?? "-" : "-"));
        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Routing: direct-pair={0}, mutual-direct={1}, shared-syncshells={2}, active-syncshell-routes={3}",
            YesNo(hasDirectPair), YesNo(isMutualDirectPair), groupEntries.Count, activeGroupRoutes.Count));
        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Local flags: blacklisted={0}, whitelisted={1}, application-blocked={2}, download-blocked={3}, auto-paused={4}",
            YesNo(isBlacklisted), YesNo(isWhitelisted), YesNo(pair.IsApplicationBlocked), YesNo(pair.IsDownloadBlocked), YesNo(pair.IsAutoPaused)));
        localState.Add("Application holds: " + JoinOrNone(pair.HoldApplicationReasons));
        localState.Add("Download holds: " + JoinOrNone(pair.HoldDownloadReasons));
        localState.Add("Auto-pause reasons: " + JoinOrNone(pair.AutoPauseReasons));
        localState.Add("Global blockers: " + JoinOrNone(globalBlockers));

        if (hasDirectPair)
        {
            permissions.Add(string.Format(CultureInfo.InvariantCulture,
                "Direct pair: mutual={0}, you-paused={1}, your-flags={2}, their-flags={3}",
                YesNo(isMutualDirectPair), YesNo(isDirectPausedByYou),
                DescribeUserPermissions(pair.UserPair!.OwnPermissions),
                DescribeUserPermissions(RedactRemotePause(pair.UserPair.OtherPermissions))));
        }
        else
        {
            permissions.Add("Direct pair: none");
        }

        if (groupEntries.Count == 0)
        {
            permissions.Add("Shared syncshells: none");
        }
        else
        {
            foreach (var entry in groupEntries)
            {
                var group = entry.Key;
                var member = entry.Value;
                List<string> memberRoles = [];
                if (string.Equals(group.OwnerUID, pair.UserData.UID, StringComparison.Ordinal))
                    memberRoles.Add("owner");
                if (member.GroupPairStatusInfo.IsModerator())
                    memberRoles.Add("moderator");
                if (member.GroupPairStatusInfo.IsPinned())
                    memberRoles.Add("pinned");

                permissions.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: your-group-flags={1}, your-member-flags={2}, their-member-flags={3}, group-flags={4}, member-role={5}",
                    group.Group.AliasOrGID,
                    DescribeGroupPermissions(group.GroupUserPermissions),
                    DescribeGroupPermissions(member.OwnGroupUserPermissions),
                    DescribeGroupPermissions(RedactRemotePause(member.OtherGroupUserPermissions)),
                    DescribeGroupPolicy(group.GroupPermissions),
                    memberRoles.Count > 0 ? string.Join(", ", memberRoles) : "member"));
            }
        }

        transferState.Add("Active downloads: " + (currentDownloads.Count == 0 ? "none" : currentDownloads.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var download in currentDownloads)
        {
            transferState.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}: status={1}, files={2}/{3}, bytes={4}/{5}",
                download.Server,
                download.Status,
                download.TransferredFiles,
                download.TotalFiles,
                ElezenImgui.ByteToString(download.TransferredBytes),
                ElezenImgui.ByteToString(download.TotalBytes)));
        }
        transferState.Add("Matching forbidden transfers: " + JoinOrNone(matchingForbiddenTransfers));

        long totalReplacementCount = pair.LastReceivedCharacterData?.FileReplacements.Sum(entry => entry.Value.Count) ?? 0;
        long totalFileSwapCount = pair.LastReceivedCharacterData?.FileReplacements.Sum(entry => entry.Value.Count(replacement => !string.IsNullOrEmpty(replacement.FileSwapPath))) ?? 0;
        dataState.Add(string.Format(CultureInfo.InvariantCulture,
            "Last received data: present={0}, hash={1}, replacements={2}, file-swaps={3}",
            YesNo(pair.LastReceivedCharacterData != null),
            pair.LastReceivedCharacterData?.DataHash.Value ?? "-",
            totalReplacementCount,
            totalFileSwapCount));
        dataState.Add("Last applied size: " + FormatByteMetric(pair.LastAppliedDataBytes));
        dataState.Add("Last applied VRAM: " + FormatByteMetric(pair.LastAppliedApproximateVRAMBytes));
        dataState.Add("Last applied triangles: " + FormatTriangleMetric(pair.LastAppliedDataTris));
        dataState.Add("Last reported VRAM: " + FormatNullableByteMetric(pair.LastReportedApproximateVRAMBytes));
        dataState.Add("Last reported triangles: " + FormatNullableTriangleMetric(pair.LastReportedTriangles));

        pluginState.Add("Required IPC: " + DescribeIpcStatuses(_ipcManager.GetRequiredStatuses()));
        pluginState.Add("Optional IPC: " + DescribeIpcStatuses(_ipcManager.GetOptionalStatuses()));
        pluginState.Add("Special IPC: " + DescribeIpcStatuses(_ipcManager.GetStatuses().Where(status => status.Role == IpcRole.Special)));
        pluginState.Add(string.Format(CultureInfo.InvariantCulture,
            "Optional plugin warnings disabled={0}", YesNo(_configService.Current.DisableOptionalPluginWarnings)));
        pluginState.Add("Missing optional plugins for this target: " + JoinOrNone(optionalPluginBlocks));

        if (isBlacklisted)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Error,
                "You blacklisted this user locally.",
                "Remove them from Settings -> Performance -> Blacklist to allow their appearance again."));
        }

        if (globalBlockers.Count > 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Error,
                "Global client blockers are preventing or deferring application.",
                string.Join("; ", globalBlockers)));
        }

        if (pair.IsAutoPaused)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "Local auto-pause is active for this user.",
                JoinOrNone(pair.AutoPauseReasons)));
        }

        if (pair.IsApplicationBlocked && !pair.IsAutoPaused)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "Snowcloak is holding this user locally.",
                "Application holds: " + JoinOrNone(pair.HoldApplicationReasons)));
        }

        if (pair.IsDownloadBlocked && !pair.IsAutoPaused)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "Snowcloak is holding downloads for this user locally.",
                "Download holds: " + JoinOrNone(pair.HoldDownloadReasons)));
        }

        if (matchingForbiddenTransfers.Count > 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Error,
                "Some of this user's required files cannot be transferred to you.",
                "Acquire those files manually or ask them to share them outside Snowcloak."));
        }

        if (hasDirectPair && !isMutualDirectPair && activeGroupRoutes.Count == 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "They have not added you back and you have no active syncshell route.",
                "Direct appearance sync will not start until they add you back or you share an active syncshell."));
        }

        if (isMutualDirectPair && isDirectPausedByYou)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "Your direct pair with this user is paused.",
                "Resume the direct pair from their pair actions menu."));
        }

        if (!isMutualDirectPair && groupEntries.Count > 0 && activeGroupRoutes.Count == 0)
        {
            var hasLocalGroupPause = groupEntries.Any(entry => entry.Key.GroupUserPermissions.IsPaused()
                                                               || entry.Value.OwnGroupUserPermissions.IsPaused());
            findings.Add(hasLocalGroupPause
                ? new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                    "All shared syncshell routes are paused by your settings.",
                    "Resume your syncshell or member-level pause to make a route available.")
                : new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                    "No active syncshell route is available for this user.",
                    "They may be offline or currently unavailable through this syncshell."));
        }

        if (!onlineForReport)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "This user is offline to your client right now.",
                "They need to be online before you can see them."));
        }
        else if (pair.IsChatOnly)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "This user is online in chat-only mode.",
                "Chat-only mode disables appearance sync."));
        }
        else if (!visibleForReport)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "This user is online but not currently visible.",
                "They are likely out of range, occluded by client visibility, or waiting on visibility initialization."));
        }

        if (currentDownloads.Count > 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "Downloads are still in progress for this user.",
                string.Join("; ", currentDownloads.Select(download => string.Format(CultureInfo.InvariantCulture,
                    "{0} {1}/{2}", download.Server, download.TransferredFiles, download.TotalFiles)))));
        }

        if (pair.IsOnline && !pair.IsChatOnly && pair.LastReceivedCharacterData == null && !pair.IsApplicationBlocked && matchingForbiddenTransfers.Count == 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "No character data has been received for this user yet.",
                "If you expected sync already, wait for an upload or have them reapply their state."));
        }

        if (optionalPluginBlocks.Count > 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "This target contains optional plugin data you cannot currently render.",
                string.Join(", ", optionalPluginBlocks)));
        }

        if (findings.Count == 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Info,
                "No obvious blocker was found in your local client state.",
                "If the result still looks wrong, copy this report and include when the problem happened."));
        }

        sections.Add(new SyncTroubleshootingSection("Local State", localState));
        sections.Add(new SyncTroubleshootingSection("Permissions", permissions));
        sections.Add(new SyncTroubleshootingSection("Transfers", transferState));
        sections.Add(new SyncTroubleshootingSection("Data", dataState));
        sections.Add(new SyncTroubleshootingSection("Plugin State", pluginState));

        return new SyncTroubleshootingReport(pair.UserData.AliasOrUID, findings.Take(8).ToList(), sections, BuildClipboardText(pair, findings, sections));
    }

    private static string BuildClipboardText(Pair pair, IReadOnlyCollection<SyncTroubleshootingFinding> findings,
        IReadOnlyCollection<SyncTroubleshootingSection> sections)
    {
        StringBuilder sb = new();
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "Snowcloak sync troubleshooting for {0}", pair.UserData.AliasOrUID));
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "UID: {0}", pair.UserData.UID));
        sb.AppendLine();
        sb.AppendLine("Summary");
        foreach (var finding in findings)
        {
            sb.Append("- [");
            sb.Append(finding.Severity);
            sb.Append("] ");
            sb.Append(finding.Title);
            if (!string.IsNullOrWhiteSpace(finding.Detail))
            {
                sb.Append(": ");
                sb.Append(finding.Detail);
            }
            sb.AppendLine();
        }

        foreach (var section in sections)
        {
            sb.AppendLine();
            sb.AppendLine(section.Title);
            foreach (var line in section.Lines)
            {
                sb.Append("- ");
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }

    private List<string> GetForbiddenTransferMatches(Pair pair)
    {
        if (pair.LastReceivedCharacterData == null)
        {
            return [];
        }

        var hashes = pair.LastReceivedCharacterData.FileReplacements
            .SelectMany(entry => entry.Value)
            .Select(entry => entry.Hash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _fileTransferOrchestrator.GetForbiddenTransfers()
            .Where(transfer => transfer.Kind == ForbiddenTransferKind.Download && hashes.Contains(transfer.Hash))
            .Select(transfer => transfer.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hash => hash, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private List<string> GetOptionalPluginBlocks(CharacterData? charaData)
    {
        if (charaData == null)
        {
            return [];
        }

        List<string> missing = [];
        AddOptionalPluginBlock(missing, charaData.CustomizePlusData.Values.Any(value => !string.IsNullOrWhiteSpace(value)),
            "Customize+", _ipcManager.GetStatus(IpcManager.CustomizePlusIpcName));
        AddOptionalPluginBlock(missing, !string.IsNullOrWhiteSpace(charaData.HeelsData),
            "SimpleHeels", _ipcManager.GetStatus(IpcManager.HeelsIpcName));
        AddOptionalPluginBlock(missing, !string.IsNullOrWhiteSpace(charaData.HonorificData),
            "Honorific", _ipcManager.GetStatus(IpcManager.HonorificIpcName));
        AddOptionalPluginBlock(missing, !string.IsNullOrWhiteSpace(charaData.PetNamesData),
            "PetNames", _ipcManager.GetStatus(IpcManager.PetNamesIpcName));
        AddOptionalPluginBlock(missing, !string.IsNullOrWhiteSpace(charaData.MoodlesData),
            "Moodles", _ipcManager.GetStatus(IpcManager.MoodlesIpcName));
        return missing;
    }

    private static void AddOptionalPluginBlock(List<string> missing, bool dataPresent, string displayName, IpcStatus status)
    {
        if (dataPresent && !status.IsAvailable)
        {
            missing.Add(string.Format(CultureInfo.InvariantCulture, "{0} ({1})", displayName, DescribeIpcReason(status)));
        }
    }

    private static string DescribeIpcStatuses(IEnumerable<IpcStatus> statuses)
    {
        var descriptions = statuses.Select(DescribeIpcStatus).ToList();
        return descriptions.Count == 0 ? "none" : string.Join(", ", descriptions);
    }

    private static string DescribeIpcStatus(IpcStatus status)
    {
        var reason = DescribeIpcReason(status);
        if (status.IsAvailable && !string.IsNullOrWhiteSpace(status.Version))
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}=available ({1})", status.Name, status.Version);
        }

        return string.Format(CultureInfo.InvariantCulture, "{0}={1}", status.Name, reason);
    }

    private static string DescribeIpcIssue(IpcStatus status)
        => status.State switch
        {
            IpcState.Missing => "is missing",
            IpcState.Disabled => string.IsNullOrWhiteSpace(status.Detail) ? "is disabled" : "is disabled: " + status.Detail,
            IpcState.VersionMismatch => string.Format(CultureInfo.InvariantCulture, "has an unsupported version (found {0}, requires {1})",
                string.IsNullOrWhiteSpace(status.Version) ? "unknown" : status.Version,
                string.IsNullOrWhiteSpace(status.RequiredVersion) ? "current IPC" : status.RequiredVersion),
            IpcState.Error => string.IsNullOrWhiteSpace(status.Detail) ? "reported an IPC error" : "reported an IPC error: " + status.Detail,
            _ => "is unavailable",
        };

    private static string DescribeIpcReason(IpcStatus status)
        => status.State switch
        {
            IpcState.Available => "available",
            IpcState.Missing => "missing",
            IpcState.Disabled => "disabled",
            IpcState.VersionMismatch => "unsupported version",
            IpcState.Error => "error",
            _ => "unavailable",
        };

    private static string DescribeUserPermissions(UserPermissions permissions)
    {
        List<string> flags = [];
        if (permissions.IsPaused()) flags.Add("paused");
        if (!permissions.IsPaired()) flags.Add("not-paired");
        if (permissions.IsDisableAnimations()) flags.Add("animations-off");
        if (permissions.IsDisableSounds()) flags.Add("sounds-off");
        if (permissions.IsDisableVFX()) flags.Add("vfx-off");
        return flags.Count > 0 ? string.Join(", ", flags) : "none";
    }

    private static UserPermissions RedactRemotePause(UserPermissions permissions)
    {
        permissions.SetPaused(false);
        return permissions;
    }

    private static GroupUserPermissions RedactRemotePause(GroupUserPermissions permissions)
    {
        permissions.SetPaused(false);
        return permissions;
    }

    private static string DescribeGroupPermissions(GroupUserPermissions permissions)
    {
        List<string> flags = [];
        if (permissions.IsPaused()) flags.Add("paused");
        if (permissions.IsDisableAnimations()) flags.Add("animations-off");
        if (permissions.IsDisableSounds()) flags.Add("sounds-off");
        if (permissions.IsDisableVFX()) flags.Add("vfx-off");
        return flags.Count > 0 ? string.Join(", ", flags) : "none";
    }

    private static string DescribeGroupPolicy(GroupPermissions permissions)
    {
        List<string> flags = [];
        if (permissions.IsDisableAnimations()) flags.Add("animations-off");
        if (permissions.IsDisableSounds()) flags.Add("sounds-off");
        if (permissions.IsDisableVFX()) flags.Add("vfx-off");
        if (permissions.HasFlag(GroupPermissions.DisableInvites)) flags.Add("invites-off");
        return flags.Count > 0 ? string.Join(", ", flags) : "none";
    }

    private static string FormatByteMetric(long bytes)
    {
        return bytes >= 0 ? ElezenImgui.ByteToString(bytes) : "none";
    }

    private static string FormatNullableByteMetric(long? bytes)
    {
        return bytes is >= 0 ? ElezenImgui.ByteToString(bytes.Value) : "none";
    }

    private static string FormatTriangleMetric(long triangles)
    {
        return triangles >= 0 ? ElezenImgui.TrisToString(triangles) : "none";
    }

    private static string FormatNullableTriangleMetric(long? triangles)
    {
        return triangles is >= 0 ? ElezenImgui.TrisToString(triangles.Value) : "none";
    }

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var filtered = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return filtered.Count > 0 ? string.Join("; ", filtered) : "none";
    }

    private static string YesNo(bool value)
    {
        return value ? "yes" : "no";
    }
}

public sealed record SyncTroubleshootingReport(
    string TargetLabel,
    IReadOnlyList<SyncTroubleshootingFinding> Findings,
    IReadOnlyList<SyncTroubleshootingSection> Sections,
    string ClipboardText);

public sealed record SyncTroubleshootingFinding(
    SyncTroubleshootingSeverity Severity,
    string Title,
    string Detail);

public sealed record SyncTroubleshootingSection(
    string Title,
    IReadOnlyList<string> Lines);

public enum SyncTroubleshootingSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
