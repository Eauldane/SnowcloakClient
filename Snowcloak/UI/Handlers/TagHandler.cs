using Snowcloak.Services.ServerConfiguration;

namespace Snowcloak.UI.Handlers;

public class TagHandler
{
    public const string CustomOfflineTag = "Snow_Offline";
    public const string CustomOfflineSyncshellTag = "Snow_OfflineSyncshell";
    public const string CustomOnlineTag = "Snow_Online";
    public const string CustomUnpairedTag = "Snow_Unpaired";
    public const string CustomVisibleTag = "Snow_Visible";
    public const string CustomPausedTag = "Snow_Paused";
    public const string CustomPairRequestsTag = "Snow_PairRequests";
    private readonly TagStore _tagStore;

    public TagHandler(TagStore tagStore)
    {
        _tagStore = tagStore;
    }

    public void AddTag(string tag)
    {
        _tagStore.AddTag(tag);
    }

    public void AddTagToPairedUid(string uid, string tagName)
    {
        _tagStore.AddTagForUid(uid, tagName);
    }

    public List<string> GetAllTagsSorted()
    {
        return
        [
            .. _tagStore.GetServerAvailablePairTags()
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
,
        ];
    }

    public HashSet<string> GetOtherUidsForTag(string tag)
    {
        return _tagStore.GetUidsForTag(tag);
    }

    public bool HasAnyTag(string uid)
    {
        return _tagStore.HasTags(uid);
    }

    public bool HasTag(string uid, string tagName)
    {
        return _tagStore.ContainsTag(uid, tagName);
    }

    /// <summary>
    /// Is this tag opened in the paired clients UI?
    /// </summary>
    /// <param name="tag">the tag</param>
    /// <returns>open true/false</returns>
    public bool IsTagOpen(string tag)
    {
        return _tagStore.ContainsOpenPairTag(tag);
    }

    public void RemoveTag(string tag)
    {
        _tagStore.RemoveTag(tag);
    }

    public void RemoveTagFromPairedUid(string uid, string tagName)
    {
        _tagStore.RemoveTagForUid(uid, tagName);
    }

    public void SetTagOpen(string tag, bool open)
    {
        if (open)
        {
            _tagStore.AddOpenPairTag(tag);
        }
        else
        {
            _tagStore.RemoveOpenPairTag(tag);
        }
    }
}
