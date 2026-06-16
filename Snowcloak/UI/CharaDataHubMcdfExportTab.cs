using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using ElezenTools.UI;
using Snowcloak.Configuration;
using Snowcloak.Services.CharaData;
using Snowcloak.UI.Components;

namespace Snowcloak.UI;

internal sealed class CharaDataHubMcdfExportTab
{
    private readonly CharaDataConfigService _configService;
    private readonly FileDialogManager _fileDialogManager;
    private readonly CharaDataManager _charaDataManager;
    private string _exportDescription = string.Empty;
    private bool _readExport;

    public CharaDataHubMcdfExportTab(CharaDataConfigService configService, FileDialogManager fileDialogManager, CharaDataManager charaDataManager)
    {
        _configService = configService;
        _fileDialogManager = fileDialogManager;
        _charaDataManager = charaDataManager;
    }

    public void Draw()
    {
        ModernSection.Header(FontAwesomeIcon.FileExport, "MCDF Export");

        CharaDataHubWidgets.DrawHelpFoldout(_configService, "This feature allows you to pack your character into a MCDF file and manually send it to other people. MCDF files can be imported into Snowcloak or Brio.");

        ImGuiHelpers.ScaledDummy(5);

        ImGui.Checkbox("##readExport", ref _readExport);
        ImGui.SameLine();
        ElezenImgui.WrappedText("I understand that by exporting my character data into a file and sending it to other people I am giving away my current character appearance irrevocably. People I am sharing my data with have the ability to share it with other people without limitations.");

        if (_readExport)
        {
            ImGui.Indent();

            ImGui.InputTextWithHint("Export Descriptor", "This description will be shown on loading the data", ref _exportDescription, 255);
            if (ElezenImgui.ShowIconButton(FontAwesomeIcon.Save, "Export Character as MCDF"))
            {
                string defaultFileName = string.IsNullOrEmpty(_exportDescription)
                    ? "export.mcdf"
                    : string.Join('_', $"{_exportDescription}.mcdf".Split(Path.GetInvalidFileNameChars()));
                _fileDialogManager.SaveFileDialog("Export Character to file", ".mcdf", defaultFileName, ".mcdf", (success, path) =>
                {
                    if (!success) return;

                    _configService.Update(c => c.LastSavedCharaDataLocation = Path.GetDirectoryName(path) ?? string.Empty);

                    _charaDataManager.SaveMareCharaFile(_exportDescription, path);
                    _exportDescription = string.Empty;
                }, Directory.Exists(_configService.Current.LastSavedCharaDataLocation) ? _configService.Current.LastSavedCharaDataLocation : null);
            }
            ElezenImgui.ColouredWrappedText("Note: For best results make sure you have everything you want to be shared as well as the correct character appearance" +
                                            " equipped and redraw your character before exporting.", ImGuiColors.DalamudYellow);

            ImGui.Unindent();
        }
    }
}
