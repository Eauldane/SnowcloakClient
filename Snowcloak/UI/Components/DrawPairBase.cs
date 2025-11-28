using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.UI.Handlers;
using Snowcloak.WebAPI.SignalR;

namespace Snowcloak.UI.Components;

public abstract class DrawPairBase
{
    protected readonly ApiController _apiController;
    protected readonly UidDisplayHandler _displayHandler;
    protected readonly UiSharedService _uiSharedService;
    protected Pair _pair;
    private readonly string _id;

    protected DrawPairBase(string id, Pair entry, ApiController apiController, UidDisplayHandler uIDDisplayHandler, UiSharedService uiSharedService)
    {
        _id = id;
        _pair = entry;
        _apiController = apiController;
        _displayHandler = uIDDisplayHandler;
        _uiSharedService = uiSharedService;
    }

    public string ImGuiID => _id;
    public string UID => _pair.UserData.UID;

    public void DrawPairedClient()
    {
        var originalY = ImGui.GetCursorPosY();
        var pauseIconSize = _uiSharedService.GetIconButtonSize(FontAwesomeIcon.Play);
        var textSize = ImGui.CalcTextSize(_pair.UserData.AliasOrUID);

        var startPos = ImGui.GetCursorStartPos();

        var framePadding = ImGui.GetStyle().FramePadding;
        var lineHeight = textSize.Y + framePadding.Y * 2;

        var off = startPos.Y;
        var height = UiSharedService.GetWindowContentRegionHeight();

        if ((originalY + off) < -lineHeight || (originalY + off) > height)
        {
            ImGui.Dummy(new System.Numerics.Vector2(0f, lineHeight));
            return;
        }

        var textPosY = originalY + pauseIconSize.Y / 2 - textSize.Y / 2;
        DrawLeftSide(textPosY, originalY);
        ImGui.SameLine();
        var posX = ImGui.GetCursorPosX();
        var rightSide = DrawRightSide(textPosY, originalY);
        DrawName(originalY, posX, rightSide);
    }

    protected abstract void DrawLeftSide(float textPosY, float originalY);

    protected abstract float DrawRightSide(float textPosY, float originalY);

    private void DrawName(float originalY, float leftSide, float rightSide)
    {
        _displayHandler.DrawPairText(_id, _pair, leftSide, originalY, () => rightSide - leftSide);
    }
}