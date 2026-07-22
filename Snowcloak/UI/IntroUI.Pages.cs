using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "WHAT SNOWCLOAK SHARES");
            ElezenImgui.WrappedText("All of the mod files currently active on your character as well as your current character state will be uploaded to the service you registered yourself at automatically. The plugin will exclusively upload the necessary mod files and not the whole mod.");
            ElezenImgui.WrappedText("Data supplied by third-party plugins using the Snowcloak IPC may be relayed to paired users as part of your character state. The Snowcloak developer does not endorse, check, or approve any plugins using the IPC.");

            ImGui.Spacing();
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "BANDWIDTH AND STORAGE");
            ElezenImgui.WrappedText("If you are on a data capped internet connection, higher fees due to data usage depending on the amount of downloaded and uploaded mod files might occur. Mod files will be compressed on up- and download to save on bandwidth usage. Due to varying up- and download speeds, changes in characters might not be visible immediately. Files present on the service that already represent your active mod files will not be uploaded again.");
            ElezenImgui.WrappedText("The mod files you are uploading are confidential and will not be distributed to parties other than the ones who are requesting the exact same mod files. Please think about who you are going to pair since it is unavoidable that they will receive and locally cache the necessary mod files that you have currently in use. Locally cached mod files will have arbitrary file names to discourage attempts at replicating the original mod.");

            ImGui.Spacing();
            ImGui.TextColored(SnowcloakColours.OnlineBlue, "SERVICE TERMS");
            ElezenImgui.WrappedText("The plugin creator tried their best to keep you secure. However, there is no guarantee for 100% security. Do not blindly pair your client with everyone.");
            ElezenImgui.WrappedText("Mod files that are saved on the service will remain on the service as long as there are requests for the files from clients. After a period of not being used, the mod files may be automatically deleted.");
            ElezenImgui.WrappedText("Accounts that are inactive for ninety (90) days will be deleted for privacy reasons.");
            ElezenImgui.WrappedText("Snowcloak is operated from servers located in the European Union and Canada. You agree not to upload any content to the service that violates the law of either jurisdiction.");
            ElezenImgui.WrappedText("You may delete your account at any time from within the Settings panel of the plugin. Any mods unique to you will then be removed from the server within 14 days.");
            ElezenImgui.WrappedText("This service is provided as-is.");
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
