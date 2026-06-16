using Dalamud.Bindings.ImGui;
using ElezenTools.UI;
using Snowcloak.PlayerData.Pairs;
using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Components;

internal sealed class CompactPairNotePopup
{
    private const string PopupTitle = "Set Notes for New User";
    private readonly NotesStore _notesStore;
    private Pair? _lastAddedUser;
    private string _lastAddedUserComment = string.Empty;
    private bool _showModalForUserAddition;

    public CompactPairNotePopup(NotesStore notesStore)
    {
        _notesStore = notesStore;
    }

    public void Draw(PairManager pairManager, bool openPopupOnAdd)
    {
        if (openPopupOnAdd && pairManager.LastAddedUser != null)
        {
            _lastAddedUser = pairManager.LastAddedUser;
            pairManager.LastAddedUser = null;
            ImGui.OpenPopup(PopupTitle);
            _showModalForUserAddition = true;
            _lastAddedUserComment = string.Empty;
        }

        if (!ImGui.BeginPopupModal(PopupTitle, ref _showModalForUserAddition, SnowcloakUi.PopupWindowFlags))
        {
            return;
        }

        if (_lastAddedUser == null)
        {
            _showModalForUserAddition = false;
        }
        else
        {
            ElezenImgui.WrappedText($"You have successfully added {_lastAddedUser.UserData.AliasOrUID}. Set a local note for the user in the field below:");
            ImGui.InputTextWithHint("##noteforuser", $"Note for {_lastAddedUser.UserData.AliasOrUID}", ref _lastAddedUserComment, 100);
            if (ElezenImgui.ShowIconButton(Dalamud.Interface.FontAwesomeIcon.Save, "Save Note"))
            {
                _notesStore.SetNoteForUid(_lastAddedUser.UserData.UID, _lastAddedUserComment);
                _lastAddedUser = null;
                _lastAddedUserComment = string.Empty;
                _showModalForUserAddition = false;
            }
        }

        ElezenImgui.SetScaledWindowSize(275);
        ImGui.EndPopup();
    }
}
