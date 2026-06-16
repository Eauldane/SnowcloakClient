using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Microsoft.Extensions.Logging;

namespace Snowcloak.Game.Housing;

public sealed class HousingPlacardReader
{
    private const string PlacardAddonName = "HousingSignBoard";

    private readonly IGameGui _gameGui;
    private readonly ILogger<HousingPlacardReader> _logger;

    public HousingPlacardReader(IGameGui gameGui, ILogger<HousingPlacardReader> logger)
    {
        _gameGui = gameGui;
        _logger = logger;
    }
    
    public unsafe bool TryReadPlacard(out List<string> lines)
    {
        lines = [];

        var addon = TryGetVisiblePlacardAddon();
        if (addon == null)
            return false;

        lines = ExtractPlacardLines(addon);
        return true;
    }

    public unsafe bool IsPlacardVisible() => TryGetVisiblePlacardAddon() != null;

    private unsafe AtkUnitBase* TryGetVisiblePlacardAddon()
    {
        var addon = _gameGui.GetAddonByName<AtkUnitBase>(PlacardAddonName);
        if (addon != null && addon->IsVisible)
            return addon;

        addon = _gameGui.GetAddonByName<AtkUnitBase>(PlacardAddonName, 1);
        if (addon != null && addon->IsVisible)
            return addon;

        return null;
    }

    private unsafe List<string> ExtractPlacardLines(AtkUnitBase* addon)
    {
        var lines = new List<string>();

        if (addon == null)
            return lines;

        var visited = new HashSet<nint>();
        var stack = new Stack<nint>();

        void Push(AtkResNode* node)
        {
            if (node == null)
                return;

            var key = (nint)node;
            if (visited.Add(key))
                stack.Push(key);
        }

        Push(addon->RootNode);
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            Push(addon->UldManager.NodeList[i]);

        while (stack.Count > 0)
        {
            var node = (AtkResNode*)stack.Pop();

            if (node->Type == NodeType.Text)
            {
                var text = ReadNodeText((AtkTextNode*)node);
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(text);
            }
            else if (node->Type == NodeType.Component)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                {
                    Push(component->UldManager.RootNode);
                    for (var i = 0; i < component->UldManager.NodeListCount; i++)
                        Push(component->UldManager.NodeList[i]);
                }
            }

            Push(node->ChildNode);
            Push(node->PrevSiblingNode);
            Push(node->NextSiblingNode);
        }

        for (var i = 0; i < lines.Count; i++)
            _logger.LogDebug("Placard extracted line {LineIndex}: {Text}", i, lines[i]);

        return lines;
    }

    private static unsafe string ReadNodeText(AtkTextNode* textNode)
    {
        var text = textNode->NodeText.ToString();
        if (!string.IsNullOrWhiteSpace(text))
            return text;

        if (textNode->NodeText.StringPtr != (byte*)null)
        {
            var span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(textNode->NodeText.StringPtr);

            try
            {
                var seString = SeString.Parse(span);
                if (!string.IsNullOrWhiteSpace(seString.TextValue))
                    return seString.TextValue;
            }
            catch
            {
                // ignored - fall through to empty
            }
        }

        return string.Empty;
    }
}
