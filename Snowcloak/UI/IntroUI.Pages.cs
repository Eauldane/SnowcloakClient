using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ElezenTools;
using ElezenTools.UI;
using Snowcloak.UI.Components;
using Snowcloak.UI.Components.Account;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public partial class IntroUi
{
    private bool _secretKeyValidationVisible;

    private void DrawWelcomePage()
    {
        DrawSetupPageHeader("Welcome to Snowcloak",
            "Snowcloak shares your current Penumbra-based appearance with people you pair with. First, check that the required plugins are ready.");

        CharaDataHubCard.Warning("Modifications applied outside Penumbra cannot be shared and may make characters look incomplete. Move anything you want to share into Penumbra.");
        ImGuiHelpers.ScaledDummy(5);
        if (!_pluginAvailabilityPanel.Draw(intro: true)) return;
        ImGui.Separator();
        if (DrawSetupPrimaryAction("toAgreement", "Continue", stickToBottom: true))
        {
            BeginAgreementTimeout();
        }
    }

    private void DrawAgreementPage()
    {
        DrawSetupPageHeader("Agreement of Usage of Service",
            "Please read the service terms before continuing.");
        CharaDataHubCard.Warning("You must be at least 18 years old, or 21 in some jurisdictions.");

        var documentHeight = MathF.Max(180f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 3);
        using var agreementBackground = ImRaii.PushColor(ImGuiCol.ChildBg, SnowcloakColours.CompactPanel);
        using var agreementPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,
            new Vector2(14f, 12f) * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginChild("##serviceAgreement", new Vector2(0, documentHeight), border: true))
        {
            using (_fontService.UidFont.Push())
            {
                ElezenImgui.ColouredText("TERMS OF SERVICE", SnowcloakColours.OnlineBlue);
            }
            
            ImGui.Spacing();
            ElezenImgui.ColouredText("SERVICE TERMS", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("The plugin creator tried their best to keep you secure. However, there is no guarantee for 100% security. Do not blindly pair your client with everyone.");
            ElezenImgui.WrappedText("Mod files that are saved on the service will remain on the service as long as there are requests for the files from clients. " +
                                    "After a period of not being used, the mod files may be automatically deleted, and will then need to be reuploaded.");
            ElezenImgui.WrappedText("Snowcloak is operated from servers located in the European Union and Canada. You agree not to upload any content to the service that violates the law of either jurisdiction.");
            ElezenImgui.WrappedText("You may delete your account at any time from within the Settings panel of the plugin. If you used mods that only you had, they will be naturally removed from the server during automated" +
                                    "cleanup.");
            ElezenImgui.WrappedText("Snowcloak is independent of and is not affiliated with, endorsed by, or supported by Square Enix. Square Enix states that use of third-party " +
                                    "tools is prohibited. Use of Snowcloak or any other plugins may result in action against your FFXIV account; you use it entirely at your own risk.");
            ImGui.Spacing();
            ElezenImgui.ColouredText("MOD DATA", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("All of the mod files currently active on your character as well as your current character state will be uploaded to the server " +
                                    "automatically. The plugin will exclusively upload the necessary mod files and not the whole mod.");
            ElezenImgui.WrappedText("Data supplied by third-party plugins using the Snowcloak IPC may be relayed to paired users as part of your character state. " +
                                    "The Snowcloak developer does not endorse, check, or approve any plugins using the IPC - using them is at your own discretion. " +
                                    "Data received for IPC-capable plugins you're not using is discarded.");

            ImGui.Spacing();
            ElezenImgui.ColouredText("BANDWIDTH AND STORAGE", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("If you are on a data capped internet connection, higher fees due to data usage depending on the amount of downloaded and uploaded " +
                                    "mod files might occur. Mod files will be compressed on upload and download to save on bandwidth usage. Due to varying upload and download " +
                                    "speeds, changes in characters might not be visible immediately. Files present on the service that already represent your active mod files will not be uploaded again. " +
                                    "If you are on a metered connection, or one that has a data cap, you're strongly advised to limit who you pair with, or pause liberally when not engaged with that player.");
            ElezenImgui.WrappedText("The mod files you are uploading are confidential and will not be distributed to parties other than the ones who are requesting the " +
                                    "exact same mod files. Please think about who you are going to pair with - they'll be able to see your appearance, even if they just " +
                                    "happen to stroll by a few days later. Locally cached mod files will have arbitrary file names to discourage attempts at replicating " +
                                    "the original mod, but neither Snowcloak nor any other plugin can prevent this from occuring if a sufficiently determined person wants to.");

            ImGui.Spacing();
            ElezenImgui.ColouredText("ROLEPLAY, COMMUNITY, AND USER CONSENT", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("Snowcloak allows users to create and share roleplay profiles, venues, events, chat messages, rooms, syncshells, scene information and other community content. " +
                                    "You are responsible for content you submit and for ensuring that you have the rights and permissions needed to upload, display and share it through Snowcloak.");
            ElezenImgui.WrappedText("You grant Snowcloak a limited, non-exclusive licence to host, process, display and transmit your content only as needed to operate the features you select. " +
                                    "This includes making public-directory content available to eligible Snowcloak users, and making private content available to the participants you choose. Snowcloak" +
                                    "does not use this license for any purpose other than to provide the service.");
            ElezenImgui.WrappedText("Public profiles, rooms, venues and events may be visible to other eligible users. A public event may allow eligible users to join its " +
                                    "associated syncshell without a password. Do not treat public-directory content as private.");
            ElezenImgui.WrappedText("Room and syncshell owners and moderators may manage participation, including removing or banning members. When a roleplay scene is finished, its messages and scene " +
                                    "information may be archived and made available for download to participants who had access to that scene. Do not treat room chat as ephemeral or assume " +
                                    "that other participants will not retain copies.");
            ElezenImgui.WrappedText("You must not use Snowcloak to share unlawful content; infringe another person's rights; harass, threaten, impersonate or dox another person; distribute malware; submit " +
                                    "knowingly false reports; or share sexual or exploitative content involving minors. Adult content is permitted only where lawful and with consent.");
            ElezenImgui.WrappedText("Snowcloak may remove, delist or restrict content, access or accounts where necessary for safety, moderation, legal compliance or operation of the service. " +
                                    "Snowcloak is not responsible for user-generated content, community events, or the conduct of other users.");
            ElezenImgui.WrappedText("If you configure a Discord webhook or another external destination, you are responsible for having authority to use it. Content sent there " +
                                    "is subject to that third party's terms and privacy practices.");
            ImGui.Spacing();
            using (_fontService.UidFont.Push())
            {
                ElezenImgui.ColouredText("PRIVACY POLICY", SnowcloakColours.OnlineBlue);
            }
            ImGui.Spacing();
            ElezenImgui.ColouredText("WHAT WE PROCESS", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("Snowcloak processes the following information, required to operate the service:");
            ElezenImgui.WrappedText("- Account and authentication information: your Snowcloak UID, account username if you " +
                                    "create one, hashed and salted passwords for accounts if you create one, credential-use " +
                                    "timestamps, and account settings. Snowcloak does not store email addresses or plaintext passwords.");
            ElezenImgui.WrappedText("- Conection and security information: IP address, client user-agent and version, connection/session identifiers, " +
                                    "timestamps, and rate-limit events. IP addresses are rotated out of logs daily, and are a natural part of webserver logging. " +
                                    "IP addresses are also used to route downloads to geographically favourable servers. IP addresses are checked at the country level only.");
            ElezenImgui.WrappedText("- Character and social information: A one-way hashed character identity supplied by the client while logged in, " +
                                    "a generated UID, a user-provided alias, pair relationships, blocks, syncshell and room membership, permissions, availability, " +
                                    "and related settings. \"Social information\" specifically refers to only internal Snowcloak information, it does not attempt to store information " +
                                    "from other sources.");
            ElezenImgui.WrappedText("- Content you choose to provide: Profile text and images, venue listings, event details, chat and roleplay messages, reports, moderation " +
                                    "information, and data supplied through enabled third-party plugin integrations who transmit data through Snowcloak.");
            ElezenImgui.WrappedText("- Appearance-sharing information: active Penumbra-based appearance manifests and the files necessary to provide that appearance to paired " +
                                    "users.");
            ElezenImgui.WrappedText("- Optional connected-account information: If you choose to link Patreon, we process identifiers, tokens, and pledge-status information needed to " +
                                    "provide that integration.");
            ElezenImgui.WrappedText("Snowcloak does not sell personal data, or use any data stored by the service for behavioural analysis or advertising.");

            ImGui.Spacing();
            ElezenImgui.ColouredText("WHY WE USE IT", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("We use this information to:");
            ElezenImgui.WrappedText("- Create and secure accounts and connections;");
            ElezenImgui.WrappedText("- Provide appearance sharing, pairing, rooms, syncshells, profiles, venues, and chat;");
            ElezenImgui.WrappedText("- Deliver content to eligible recipients;");
            ElezenImgui.WrappedText("- Operate, maintain, troubleshoot, and defend the service;");
            ElezenImgui.WrappedText("- Meet legal obligations; and");
            ElezenImgui.WrappedText("- Provide optional features you request, such as roleplay discovery or Patreon integration.");
            ElezenImgui.WrappedText("Our usual legal bases are performance of the service agreement, our legitimate interests in keeping the " +
                                    "service sercure and usable, your consent where a feature is optional, and legal obligations " +
                                    "where applicable. ");
            ElezenImgui.WrappedText("Public roleplay directory listing, adult-content settings, connected accounts and sharing through external " +
                                    "plugins are optional. Turning on a public or external feature may make the selected information available " +
                                    "to people beyond your direct pairs. Exercise due caution when using these features or plugins.");
            
            ImGui.Spacing();
            ElezenImgui.ColouredText("WHO RECEIVES YOUR DATA", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("Data is transmitted only as needed:");
            ElezenImgui.WrappedText("- To people who are eligible to receive it through the sharing, pairing, room, syncshell, directory, or venue feature you use;");
            ElezenImgui.WrappedText("- To Snowcloak staff where needed to investigate reports, abuse, or service safety; and");
            ElezenImgui.WrappedText("- To a webhook or external destination configured by a group or venue owner, where you choose to use a feature that sends data there.");
            ElezenImgui.WrappedText("Snowcloak cannot control what another user does with content they receive. In particular, " +
                                    "recipients may retain, copy or share appearance files, screenshots, messages or information you disclose.");

            ImGui.Spacing();
            ElezenImgui.ColouredText("INTERNATIONAL PROCESSING", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("The service is operated using servers in the European Union and Canada. Snowcloak does NOT operate servers in the United States, in line " +
                                    "with the Schrems II ruling and the updated EU-US Data Privacy Framework.");
            
            ImGui.Spacing();
            ElezenImgui.ColouredText("RETENTION", SnowcloakColours.OnlineBlue);
            ElezenImgui.WrappedText("We keep data ony for as long as needed for the purposes above.");
            ElezenImgui.WrappedText("- Account, identity, and relationship records are kept while the relevant UID or account remains active.");
            ElezenImgui.WrappedText("- Chat and roleplay messages are retained for 30 days, unless deletion is required sooner or a legal/safety hold applies.");
            ElezenImgui.WrappedText("- Profile and venue images are removed 14 days after they cease to be referenced.");
            ElezenImgui.WrappedText("- Syncshell audit logs are held for 90 days.");
            ElezenImgui.WrappedText("- Appearance files are retained while in use and then removed under the service's unused-file retention process, normally 30 days of the last request of the file.");
            ElezenImgui.WrappedText("- Session and presence data are deleted on logout or disconnection.");
            ElezenImgui.WrappedText("- Backups may retain deleted information for an additional 14 days after the periods outlined above.");
            ElezenImgui.WrappedText("Deleting a UID removes its owned service data, pairing relationships and room/syncshell membership as applicable. It does not delete other UIDs or the wider Snowcloak account.");
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (_timeoutTask?.IsCompleted ?? true)
        {
            if (DrawSetupPrimaryAction("toSetup", "I have read and agree", stickToBottom: true))
            {
                _configService.Update(c => c.AcceptedAgreement = true);
            }
        }
        else
        {
            ElezenImgui.WrappedText(_timeoutLabel);
        }
    }

    private void DrawStoragePage()
    {
        DrawSetupPageHeader("File Storage Setup",
            "Choose an empty local folder for Snowcloak downloads, then run the initial scan.");

        if (!_storageSettingsPanel.HasValidPenumbraModPath)
        {
            CharaDataHubCard.Error("Penumbra does not have a valid mod directory. Configure one in Penumbra before continuing.");
        }
        else
        {
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "STORAGE LOCATION");
            ElezenImgui.WrappedText("Use a new empty folder outside your game and Penumbra folders. Snowcloak manages its contents automatically.");
            _storageSettingsPanel.DrawCacheDirectorySetting(showLocationGuidance: false, stackedLayout: true);

            ImGui.Separator();
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "INITIAL SCAN");
            ElezenImgui.WrappedText("The first scan avoids downloading files you already have. It may take a while for large mod libraries.");
        }

        if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _storageSettingsPanel.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
        {
            if (DrawSetupPrimaryAction("startScan", "Start initial scan"))
            {
                _cacheMonitor.InvokeScan();
            }
        }
        else
        {
            _storageSettingsPanel.DrawFileScanState();
        }
        if (!_dalamudUtilService.IsWine)
        {
            ImGui.Separator();
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "STORAGE EFFICIENCY");
            var useFileCompactor = _configService.Current.UseCompactor;
            if (ImGui.Checkbox("Use File Compactor", ref useFileCompactor))
            {
                _configService.Update(c => c.UseCompactor = useFileCompactor);
            }
            ElezenImgui.DrawHelpText("The File Compactor can save substantial disk space. It adds a small CPU cost during downloads and can be changed later in Settings.");
        }
    }

    private void DrawSetupPageHeader(string title, string description)
    {
        using (_fontService.UidFont.Push())
            ImGui.TextUnformatted(title);
        using (ImRaii.PushColor(ImGuiCol.Text, SnowcloakColours.CompactTextMuted))
            ImGui.TextWrapped(description);
        ImGuiHelpers.ScaledDummy(6);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);
    }

    private static bool DrawSetupPrimaryAction(string id, string label, bool stickToBottom = false)
    {
        var width = MathF.Min(220f * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().X);
        var height = 32f * ImGuiHelpers.GlobalScale;
        if (stickToBottom)
        {
            var verticalSpace = ImGui.GetContentRegionAvail().Y;
            if (verticalSpace > height)
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + verticalSpace - height);
        }
        var available = ImGui.GetContentRegionAvail().X;
        if (available > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - width);
        return ImGui.Button($"{label}##{id}", new Vector2(width, height));
    }

    private void DrawServicePage()
    {
        DrawSetupPageHeader("Connect to a Service",
            "Choose the service and sign-in method for this character.");

        ImGui.BeginDisabled(_standaloneKeyFlow.IsRunning || _standaloneKeyFlow.Succeeded);
        using (ImRaii.Disabled(_accountFlow.IsRunning))
        {
            _ = _serviceSelectionPanel.Draw(selectOnChange: true, intro: true);
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextColored(SnowcloakColours.OnlineBlue, "CONNECTION METHOD");
        using (ImRaii.Disabled(_standaloneKeyFlow.IsRunning || _accountFlow.IsRunning))
            DrawServiceRegistrationModePicker();

        ImGui.Spacing();
        ImGui.Separator();
        switch (_serviceRegistrationMode)
        {
            case ServiceRegistrationMode.CreateKey:
                DrawNewStandaloneKeySetup();
                break;
            case ServiceRegistrationMode.UseExistingKey:
                DrawExistingStandaloneKeySetup();
                break;
            case ServiceRegistrationMode.RestoreBackup:
                DrawSecretKeyBackupSetup();
                break;
            case ServiceRegistrationMode.Account:
                DrawAccountSetup();
                break;
        }
    }

    private void DrawServiceRegistrationModePicker()
    {
        var cardGap = ImGui.GetStyle().ItemSpacing.X;
        var cardWidth = (ImGui.GetContentRegionAvail().X - cardGap) / 2;
        var cardSize = new Vector2(cardWidth, 62f * ImGuiHelpers.GlobalScale);

        if (DrawServiceRegistrationModeCard(ServiceRegistrationMode.CreateKey, FontAwesomeIcon.PlusCircle,
                "Create a new key", "Recommended for first-time setup", cardSize))
            _serviceRegistrationMode = ServiceRegistrationMode.CreateKey;
        ImGui.SameLine();
        if (DrawServiceRegistrationModeCard(ServiceRegistrationMode.UseExistingKey, FontAwesomeIcon.Key,
                "Use an existing key", "Connect with a key you already have", cardSize))
            _serviceRegistrationMode = ServiceRegistrationMode.UseExistingKey;
        if (DrawServiceRegistrationModeCard(ServiceRegistrationMode.RestoreBackup, FontAwesomeIcon.FileImport,
                "Restore a backup", "Recover an exported setup", cardSize))
            _serviceRegistrationMode = ServiceRegistrationMode.RestoreBackup;
        ImGui.SameLine();
        if (DrawServiceRegistrationModeCard(ServiceRegistrationMode.Account, FontAwesomeIcon.User,
                "Snowcloak account", "Sign in or create an account", cardSize))
            _serviceRegistrationMode = ServiceRegistrationMode.Account;
    }

    private bool DrawServiceRegistrationModeCard(ServiceRegistrationMode mode, FontAwesomeIcon icon,
        string title, string description, Vector2 size)
    {
        var selected = _serviceRegistrationMode == mode;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##registration-{mode}", size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        var max = min + size;
        var fill = selected
            ? SnowcloakColours.CompactPanelAlt
            : hovered ? new Vector4(0.055f, 0.115f, 0.165f, 0.94f) : SnowcloakColours.CompactPanel;
        var border = selected || hovered ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactBorderSubtle;
        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;

        drawList.AddRectFilled(min, max, Colour.Vector4ToColour(fill), 4f * scale);
        drawList.AddRect(min, max, Colour.Vector4ToColour(border), 4f * scale,
            ImDrawFlags.None, selected ? 2f * scale : 1f * scale);
        if (selected)
            drawList.AddRectFilled(min, min + new Vector2(3f * scale, size.Y),
                Colour.Vector4ToColour(SnowcloakColours.OnlineBlue), 4f * scale);

        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        drawList.AddText(new Vector2(min.X + 12f * scale, min.Y + (size.Y - iconSize.Y) / 2),
            Colour.Vector4ToColour(selected ? SnowcloakColours.OnlineBlue : SnowcloakColours.CompactTextMuted), iconText);
        ImGui.PopFont();

        var textX = min.X + 12f * scale + iconSize.X + 10f * scale;
        drawList.AddText(new Vector2(textX, min.Y + 11f * scale),
            Colour.Vector4ToColour(selected || hovered ? Vector4.One : SnowcloakColours.CompactTextMuted), title);
        drawList.AddText(new Vector2(textX, min.Y + 34f * scale),
            Colour.Vector4ToColour(SnowcloakColours.CompactTextMuted), description);
        return clicked;
    }

    private void DrawNewStandaloneKeySetup()
    {
        ImGui.TextColored(SnowcloakColours.OnlineBlue, "CREATE A STANDALONE KEY");
        ElezenImgui.WrappedText("This key is your credential. Keep a secure copy so you can use it again later.");

        ImGui.BeginDisabled(_standaloneKeyFlow.IsRunning || _accountFlow.IsRunning || _standaloneKeyFlow.Succeeded || _secretKey.Length > 0);
        if (ImGui.Button("Create standalone key", new Vector2(-1, 0)))
        {
            _standaloneKeyFlow.Begin(_registerService.RegisterAccount,
                "New standalone key created. Copy and store it securely before connecting.",
                reply => _secretKey = NormalizeSecretKey(reply.SecretKey ?? string.Empty), "Registration failed");
        }
        ImGui.EndDisabled();
        _standaloneKeyFlow.DrawStatus("Waiting for the server...");

        if (_standaloneKeyFlow.Succeeded)
            DrawSecretKeyInput("Your new secret key", "64-character secret key", readOnly: true);
    }

    private void DrawExistingStandaloneKeySetup()
    {
        ImGui.TextColored(SnowcloakColours.OnlineBlue, "USE AN EXISTING STANDALONE KEY");
        ElezenImgui.WrappedText("Paste the key you already use with this service to connect this character.");
        DrawSecretKeyInput("Secret key", "64-character secret key", readOnly: false);
    }

    private void DrawSecretKeyBackupSetup()
    {
        ImGui.TextColored(SnowcloakColours.OnlineBlue, "RESTORE A SNOWCLOAK BACKUP");
        ElezenImgui.WrappedText("Import a backup to restore its service setup, keys, character assignments, and notes.");
        if (ImGui.Button("Import Snowcloak backup", new Vector2(-1, 0)))
            BeginSecretKeyBackupImport();
        _secretKeyBackupFlow.DrawStatus();
        _secretKeyBackupFlow.DrawInitialSetupAssignment();
    }

    private void DrawSecretKeyInput(string label, string hint, bool readOnly)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1);
        var inputFlags = readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None;
        if (ImGui.InputTextWithHint("##standaloneSecretKey", hint, ref _secretKey, 80, inputFlags))
        {
            _secretKey = NormalizeSecretKey(_secretKey);
            _secretKeyValidationVisible = false;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            _secretKeyValidationVisible = true;

        if (!readOnly && ElezenImgui.ShowIconButton(FontAwesomeIcon.Paste, "Paste key from clipboard"))
        {
            _secretKey = NormalizeSecretKey(ImGui.GetClipboardText());
            _secretKeyValidationVisible = !IsValidSecretKey(_secretKey);
        }

        var validSecretKey = IsValidSecretKey(_secretKey);
        if (validSecretKey && ElezenImgui.ShowIconButton(FontAwesomeIcon.Copy, "Copy key"))
        {
            ImGui.SetClipboardText(_secretKey);
        }

        if (_secretKeyValidationVisible && _secretKey.Length != 64)
        {
            ElezenImgui.ColouredWrappedText("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
        }
        else if (_secretKeyValidationVisible && !validSecretKey)
        {
            ElezenImgui.ColouredWrappedText("Your secret key can only contain ABCDEF and the numbers 0-9.", ImGuiColors.DalamudRed);
        }

        using (ImRaii.Disabled(!validSecretKey || _apiController.ServerState == ServerState.Connecting || _apiController.ServerState == ServerState.Reconnecting))
        {
            if (ImGui.Button("Save and connect", new Vector2(-1, 0)))
                SaveAndConnectWithSecretKey();
        }

        if (_apiController.ServerState != ServerState.NoSecretKey)
        {
            ElezenImgui.ColouredText(GetConnectionStatus(), GetConnectionColor());
        }
    }

    private void DrawAccountSetup()
    {
        _accountFlow.Draw(new PasswordAccountFlowOptions
        {
            IdPrefix = "account",
            HeaderTitle = "Snowcloak account",
            HeaderDescription = "Restore account-linked keys, or create a new account for this character.",
            SignInModeLabel = "Sign in to an account",
            CreateModeLabel = "Create a new account",
            CreateDescription = "A new key will be created for this character and linked to the account.",
            SignInRunningMessage = "Signing in to the selected service...",
            CreateRunningMessage = "Registering a character key with the selected service...",
            SignIn = SignInWithPassword,
            Create = CreateAccountWithPassword
        });
    }

    private static string NormalizeSecretKey(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static bool IsValidSecretKey(string value)
    {
        return value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }
}
