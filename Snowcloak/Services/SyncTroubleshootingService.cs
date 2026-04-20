using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snowcloak.API.Data;
using Snowcloak.API.Data.Enum;
using Snowcloak.API.Data.Extensions;
using Snowcloak.Configuration;
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
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, FileDownloadStatus>> _downloadsByUid =
        new(StringComparer.Ordinal);

    public SyncTroubleshootingService(ILogger<SyncTroubleshootingService> logger, SnowMediator mediator,
        SnowcloakConfigService configService, DalamudUtilService dalamudUtilService,
        FileTransferOrchestrator fileTransferOrchestrator, IpcManager ipcManager,
        ServerConfigurationManager serverConfigurationManager)
        : base(logger, mediator)
    {
        _configService = configService;
        _dalamudUtilService = dalamudUtilService;
        _fileTransferOrchestrator = fileTransferOrchestrator;
        _ipcManager = ipcManager;
        _serverConfigurationManager = serverConfigurationManager;

        Mediator.Subscribe<DownloadStartedMessage>(this, msg =>
        {
            if (string.IsNullOrWhiteSpace(msg.UID)) return;
            _downloadsByUid[msg.UID] = msg.DownloadStatus;
        });

        Mediator.Subscribe<DownloadFinishedMessage>(this, msg =>
        {
            if (string.IsNullOrWhiteSpace(msg.UID)) return;
            _downloadsByUid.TryRemove(msg.UID, out _);
        });
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public SyncTroubleshootingReport BuildReport(Pair pair)
    {
        List<SyncTroubleshootingFinding> findings = [];
        List<SyncTroubleshootingSection> sections = [];
        List<string> localState = [];
        List<string> permissions = [];
        List<string> transferState = [];
        List<string> dataState = [];
        List<string> pluginState = [];

        bool isBlacklisted = _serverConfigurationManager.IsUserBlacklisted(pair.UserData);
        bool isWhitelisted = _serverConfigurationManager.IsUserWhitelisted(pair.UserData);
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
            .Where(entry => !entry.Key.GroupUserPermissions.IsPaused() && !entry.Value.GroupUserPermissions.IsPaused())
            .ToList();
        var currentDownloads = _downloadsByUid.TryGetValue(pair.UserData.UID, out var downloads)
            ? downloads.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToList()
            : [];

        List<string> globalBlockers = [];
        if (!_ipcManager.Penumbra.APIAvailable)
            globalBlockers.Add("Penumbra is unavailable");
        if (!_ipcManager.Glamourer.APIAvailable)
            globalBlockers.Add("Glamourer is unavailable");
        if (_dalamudUtilService.IsZoning)
            globalBlockers.Add("you are zoning");
        if (_dalamudUtilService.IsInCutscene)
            globalBlockers.Add("you are in a cutscene");
        if (_dalamudUtilService.IsInGpose)
            globalBlockers.Add("you are in GPose");
        if (_configService.Current.HoldCombatApplication && _dalamudUtilService.IsInCombatOrPerforming)
            globalBlockers.Add("Hold application during combat is active while you are in combat/performance");

        List<string> matchingForbiddenTransfers = GetForbiddenTransferMatches(pair);
        List<string> optionalPluginBlocks = GetOptionalPluginBlocks(pair.LastReceivedCharacterData);

        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Snapshot: client-side only, generated {0:u}", DateTime.UtcNow));
        localState.Add(string.Format(CultureInfo.InvariantCulture,
            "Presence: online={0}, visible={1}, chat-only={2}, player={3}",
            YesNo(pair.IsOnline), YesNo(pair.IsVisible), YesNo(pair.IsChatOnly), pair.PlayerName ?? "-"));
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
                "Direct pair: mutual={0}, you-paused={1}, they-paused={2}, your-flags={3}, their-flags={4}",
                YesNo(isMutualDirectPair), YesNo(isDirectPausedByYou), YesNo(isDirectPausedByThem),
                DescribeUserPermissions(pair.UserPair!.OwnPermissions),
                DescribeUserPermissions(pair.UserPair.OtherPermissions)));
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
                    "{0}: your-group-flags={1}, member-flags={2}, group-flags={3}, member-role={4}",
                    group.Group.AliasOrGID,
                    DescribeGroupPermissions(group.GroupUserPermissions),
                    DescribeGroupPermissions(member.GroupUserPermissions),
                    DescribeGroupPolicy(group.GroupPermissions),
                    memberRoles.Count > 0 ? string.Join(", ", memberRoles) : "member"));
            }
        }

        transferState.Add("Active downloads: " + (currentDownloads.Count == 0 ? "none" : currentDownloads.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var download in currentDownloads)
        {
            transferState.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}: status={1}, files={2}/{3}, bytes={4}/{5}",
                download.Key,
                download.Value.DownloadStatus,
                download.Value.TransferredFiles,
                download.Value.TotalFiles,
                UiSharedService.ByteToString(download.Value.TransferredBytes),
                UiSharedService.ByteToString(download.Value.TotalBytes)));
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

        pluginState.Add(string.Format(CultureInfo.InvariantCulture,
            "Required IPC: Penumbra={0}, Glamourer={1}",
            YesNo(_ipcManager.Penumbra.APIAvailable),
            YesNo(_ipcManager.Glamourer.APIAvailable)));
        pluginState.Add(string.Format(CultureInfo.InvariantCulture,
            "Optional IPC: Customize+={0}, Heels={1}, Honorific={2}, PetNames={3}, Moodles={4}",
            YesNo(_ipcManager.CustomizePlus.APIAvailable),
            YesNo(_ipcManager.Heels.APIAvailable),
            YesNo(_ipcManager.Honorific.APIAvailable),
            YesNo(_ipcManager.PetNames.APIAvailable),
            YesNo(_ipcManager.Moodles.APIAvailable)));
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

        if (isMutualDirectPair && isDirectPausedByThem)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "This user's direct pair with you is paused on their side.",
                "They need to resume the pair for direct sync to work again."));
        }

        if (!isMutualDirectPair && groupEntries.Count > 0 && activeGroupRoutes.Count == 0)
        {
            findings.Add(new SyncTroubleshootingFinding(SyncTroubleshootingSeverity.Warning,
                "All shared syncshell routes are currently paused.",
                "Check the raw permissions below to see whether the pause is coming from your side, their side, or both."));
        }

        if (!pair.IsOnline)
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
        else if (!pair.IsVisible)
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
                    "{0} {1}/{2}", download.Key, download.Value.TransferredFiles, download.Value.TotalFiles)))));
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

        return _fileTransferOrchestrator.ForbiddenTransfers
            .OfType<DownloadFileTransfer>()
            .Where(transfer => hashes.Contains(transfer.Hash))
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
        if (charaData.CustomizePlusData.Values.Any(value => !string.IsNullOrWhiteSpace(value)) && !_ipcManager.CustomizePlus.APIAvailable)
            missing.Add("Customize+");
        if (!string.IsNullOrWhiteSpace(charaData.HeelsData) && !_ipcManager.Heels.APIAvailable)
            missing.Add("SimpleHeels");
        if (!string.IsNullOrWhiteSpace(charaData.HonorificData) && !_ipcManager.Honorific.APIAvailable)
            missing.Add("Honorific");
        if (!string.IsNullOrWhiteSpace(charaData.PetNamesData) && !_ipcManager.PetNames.APIAvailable)
            missing.Add("PetNames");
        if (!string.IsNullOrWhiteSpace(charaData.MoodlesData) && !_ipcManager.Moodles.APIAvailable)
            missing.Add("Moodles");
        return missing;
    }

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
        return bytes >= 0 ? UiSharedService.ByteToString(bytes) : "none";
    }

    private static string FormatNullableByteMetric(long? bytes)
    {
        return bytes is >= 0 ? UiSharedService.ByteToString(bytes.Value) : "none";
    }

    private static string FormatTriangleMetric(long triangles)
    {
        return triangles >= 0 ? UiSharedService.TrisToString(triangles) : "none";
    }

    private static string FormatNullableTriangleMetric(long? triangles)
    {
        return triangles is >= 0 ? UiSharedService.TrisToString(triangles.Value) : "none";
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
