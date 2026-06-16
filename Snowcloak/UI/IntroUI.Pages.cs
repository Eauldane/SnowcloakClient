using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using ElezenTools.UI;
using Snowcloak.UI.Components.Account;
using Snowcloak.WebAPI.SignalR.Utils;
using System.Numerics;

namespace Snowcloak.UI;

public partial class IntroUi
{
    private void DrawWelcomePage()
    {
        _fontService.BigText("Welcome to Snowcloak");
        ImGui.Separator();
        ElezenImgui.WrappedText("Snowcloak is a plugin that will replicate your full current character state including all active mods to other paired users. " +
                                        "You need Penumbra and Glamourer to use this plugin.");
        ElezenImgui.WrappedText("We will have to setup a few things first before you can start using this plugin. Click on next to continue.");

        ElezenImgui.ColouredWrappedText("Note: Any modifications you have applied through anything but Penumbra cannot be shared and your character state on other clients " +
                                        "might look broken because of this or others players mods might not apply on your end altogether. " +
                                        "If you want to use this plugin you will have to move your mods to Penumbra.", ImGuiColors.DalamudYellow);
        if (!_pluginAvailabilityPanel.Draw(intro: true)) return;
        ImGui.Separator();
        if (ImGui.Button("Next##toAgreement"))
        {
            BeginAgreementTimeout();
        }
    }

    private void DrawAgreementPage()
    {
        using (_fontService.UidFont.Push())
        {
            ImGui.TextUnformatted("Agreement of Usage of Service");
        }

        ImGui.Separator();
        string readThis = "READ THIS CAREFULLY";
        using (ElezenFonts.Push(ImGui.GetFontSize() * 1.5f))
        {
            Vector2 textSize = ImGui.CalcTextSize(readThis);
            ImGui.SetCursorPosX(ImGui.GetWindowSize().X / 2 - textSize.X / 2);
            ElezenImgui.ColouredText(readThis, ImGuiColors.DalamudRed);
        }
        ImGui.Separator();
        ElezenImgui.WrappedText("To use Snowcloak, you must be over the age of 18, or 21 in some jurisdictions.");
        ElezenImgui.WrappedText("All of the mod files currently active on your character as well as your current character state will be uploaded to the service you registered yourself at automatically. The plugin will exclusively upload the necessary mod files and not the whole mod.");
        ElezenImgui.WrappedText("If you are on a data capped internet connection, higher fees due to data usage depending on the amount of downloaded and uploaded mod files might occur. Mod files will be compressed on up- and download to save on bandwidth usage. Due to varying up- and download speeds, changes in characters might not be visible immediately. Files present on the service that already represent your active mod files will not be uploaded again.");
        ElezenImgui.WrappedText("The mod files you are uploading are confidential and will not be distributed to parties other than the ones who are requesting the exact same mod files. Please think about who you are going to pair since it is unavoidable that they will receive and locally cache the necessary mod files that you have currently in use. Locally cached mod files will have arbitrary file names to discourage attempts at replicating the original mod.");
        ElezenImgui.WrappedText("The plugin creator tried their best to keep you secure. However, there is no guarantee for 100% security. Do not blindly pair your client with everyone.");
        ElezenImgui.WrappedText("Mod files that are saved on the service will remain on the service as long as there are requests for the files from clients. After a period of not being used, the mod files may be automatically deleted.");
        ElezenImgui.WrappedText("Accounts that are inactive for ninety (90) days will be deleted for privacy reasons.");
        ElezenImgui.WrappedText("Snowcloak is operated from servers located in the European Union and Canada. You agree not to upload any content to the service that violates the law of either jurisdiction");
        ElezenImgui.WrappedText("You may delete your account at any time from within the Settings panel of the plugin. Any mods unique to you will then be removed from the server within 14 days.");
        ElezenImgui.WrappedText("This service is provided as-is.");

        ImGui.Separator();
        if (_timeoutTask?.IsCompleted ?? true)
        {
            if (ImGui.Button("I agree##toSetup"))
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
        using (_fontService.UidFont.Push())
            ImGui.TextUnformatted("File Storage Setup");
        ImGui.Separator();

        if (!_storageSettingsPanel.HasValidPenumbraModPath)
        {
            ElezenImgui.ColouredWrappedText("You do not have a valid mod directory path set. Open Penumbra and configure a valid mod directory.", ImGuiColors.DalamudRed);
        }
        else
        {
            ElezenImgui.WrappedText("To avoid downloading files already present on your computer, Snowcloak will have to scan your configured mod directory. " +
                                            "Additionally, a local storage folder must be set where Snowcloak will download other character files to. " +
                                            "Once the storage folder is set and the scan complete, this page will automatically forward to registration at a service.");
            ElezenImgui.WrappedText("Note: The initial scan, depending on the amount of mods you have, might take a while. Please wait until it is completed.");
            ElezenImgui.ColouredWrappedText("Warning: once past this step you should not delete SnowcloakFiles.csv or Snowcloak.db in the Plugin Configurations folder of Dalamud. " +
                                            "Otherwise on the next launch a full re-scan of the file cache database will be initiated.", ImGuiColors.DalamudYellow);
            ElezenImgui.ColouredWrappedText("Warning: if the scan is hanging and does nothing for a long time, chances are high your mod directory is not set up properly.", ImGuiColors.DalamudYellow);
            _storageSettingsPanel.DrawCacheDirectorySetting();
        }

        if (!_cacheMonitor.IsScanRunning && !string.IsNullOrEmpty(_configService.Current.CacheFolder) && _storageSettingsPanel.HasValidPenumbraModPath && Directory.Exists(_configService.Current.CacheFolder))
        {
            if (ImGui.Button("Start Scan##startScan"))
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
            var useFileCompactor = _configService.Current.UseCompactor;
            if (ImGui.Checkbox("Use File Compactor", ref useFileCompactor))
            {
                _configService.Update(c => c.UseCompactor = useFileCompactor);
            }
            ElezenImgui.ColouredWrappedText("The File Compactor can save a tremendeous amount of space on the hard disk for downloads through Snowcloak. It will incur a minor CPU penalty on download but can speed up " +
                                            "loading of other characters. It is recommended to keep it enabled. You can change this setting later anytime in the Snowcloak settings.", ImGuiColors.DalamudYellow);
        }
    }

    private void DrawServicePage()
    {
        using (_fontService.UidFont.Push())
            ImGui.TextUnformatted("Service Registration");
        ImGui.Separator();
        ElezenImgui.WrappedText("To be able to use Snowcloak you will have to register an account.");
        ElezenImgui.WrappedText("Refer to the instructions at the location you obtained this plugin for more information or support.");

        ImGui.Separator();

        ImGui.BeginDisabled(_standaloneKeyFlow.IsRunning);
        using (ImRaii.Disabled(_accountFlow.IsRunning))
        {
            _ = _serviceSelectionPanel.Draw(selectOnChange: true, intro: true);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("If you already exported a Snowcloak backup, restore it here to reuse your existing service setup.");
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.FileImport, "Import Snowcloak backup"))
        {
            BeginSecretKeyBackupImport();
        }
        ElezenImgui.AttachTooltip("Restore secret keys, character assignments, and notes from a Snowcloak backup JSON.");
        _secretKeyBackupFlow.DrawStatus();

        ImGui.Separator();
        DrawAccountSetup();

        ImGui.Separator();
        ImGui.BeginDisabled(_standaloneKeyFlow.IsRunning || _accountFlow.IsRunning || _standaloneKeyFlow.Succeeded || _secretKey.Length > 0);
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Log in with XIVAuth"))
        {
            _standaloneKeyFlow.Begin(_registerService.XIVAuth, "Account registered. Welcome to Snowcloak!",
                reply => _secretKey = reply.SecretKey ?? "", "Registration failed");
        }
        ImGui.SameLine();
        if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Plus, "Create standalone secret key"))
        {
            _standaloneKeyFlow.Begin(_registerService.RegisterAccount,
                "New standalone key created.\nPlease keep a copy of your secret key in case you need to reset your plugins, or to use it on another PC.",
                reply => _secretKey = reply.SecretKey ?? "", "Registration failed");
        }
        ImGui.EndDisabled(); // standalone/account in progress || success || secret key entered
        _standaloneKeyFlow.DrawStatus("Waiting for the server...");

        ImGui.Separator();

        var text = _standaloneKeyFlow.Succeeded ? "Secret Key" : "Enter Secret Key";
        if (!_standaloneKeyFlow.Succeeded)
        {
            ImGui.TextUnformatted("If you already have a registered account, you can enter its secret key below to use it instead.");
        }

        var textSize = ImGui.CalcTextSize(text);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ElezenImgui.GetWindowContentRegionWidth() - ImGui.GetWindowContentRegionMin().X - textSize.X);
        ImGui.InputText("", ref _secretKey, 64);
        if (_secretKey.Length > 0 && _secretKey.Length != 64)
        {
            ElezenImgui.ColouredWrappedText("Your secret key must be exactly 64 characters long.", ImGuiColors.DalamudRed);
        }
        else if (_secretKey.Length == 64 && !HexRegex().IsMatch(_secretKey))
        {
            ElezenImgui.ColouredWrappedText("Your secret key can only contain ABCDEF and the numbers 0-9.", ImGuiColors.DalamudRed);
        }
        else if (_secretKey.Length == 64)
        {
            using var saveDisabled = ImRaii.Disabled(_apiController.ServerState == ServerState.Connecting || _apiController.ServerState == ServerState.Reconnecting);
            if (ImGui.Button("Save and Connect"))
            {
                SaveAndConnectWithSecretKey();
            }
        }

        if (_apiController.ServerState != ServerState.NoSecretKey)
        {
            ElezenImgui.ColouredText(GetConnectionStatus(), GetConnectionColor());
        }

        ImGui.EndDisabled(); // _standaloneKeyFlow.IsRunning
    }

    private void DrawAccountSetup()
    {
        if (ImGui.CollapsingHeader("Which option should I use?"))
        {
            ElezenImgui.WrappedText("The preferred option is you manage your keys yourself. This option gives you full and complete control.");
            ElezenImgui.WrappedText("Failing that, Snowcloak accounts will generate fresh keys for your linked characters if you need it. You choose a username and password, and Snowcloak handles the rest. Passwords are hashed using state-of-the-art Argon2id algorithms. If in doubt, use a password manager to generate a random username and password.");
            ElezenImgui.WrappedText("XIVAuth also remains available, but is considered decrepated at this time.");
        }

        _accountFlow.Draw(new PasswordAccountFlowOptions
        {
            IdPrefix = "account",
            HeaderTitle = "Snowcloak account",
            HeaderDescription = "Use one account across characters and computers. Snowcloak restores the secret keys needed for the selected character after sign-in.",
            SignInDescription = "Sign in to restore this account's character keys for the current character.",
            CreateDescription = "Create an account on the selected service for this character.",
            SignInRunningMessage = "Signing in to the selected service...",
            CreateRunningMessage = "Registering a character key with the selected service...",
            SignIn = SignInWithPassword,
            Create = CreateAccountWithPassword
        });
    }
}
