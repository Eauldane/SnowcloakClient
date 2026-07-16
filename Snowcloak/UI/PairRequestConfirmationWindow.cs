using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using System.Numerics;

namespace Snowcloak.UI;

public sealed class PairRequestConfirmationWindow : WindowMediatorSubscriberBase
{
    private readonly PairRequestService _pairRequestService;
    private Task? _sendTask;

    public PairRequestConfirmationWindow(
        ILogger<PairRequestConfirmationWindow> logger,
        SnowMediator mediator,
        PairRequestService pairRequestService,
        PerformanceCollectorService performanceCollectorService,
        string ident,
        string characterName,
        string pluginName)
        : base(logger, mediator, $"Pair with {characterName}###SnowcloakIpcPairRequest{ident}", performanceCollectorService)
    {
        _pairRequestService = pairRequestService;
        Ident = ident;
        CharacterName = characterName;
        PluginName = pluginName;
        SetScaledSizeConstraints(new Vector2(390, 150), new Vector2(540, 260));
        Size = new Vector2(430, 185);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
        IsOpen = true;
    }

    public string Ident { get; }
    public string CharacterName { get; }
    public string PluginName { get; }

    public override void OnClose()
    {
        Mediator.Publish(new RemoveWindowMessage(this));
    }

    protected override void DrawInternal()
    {
        ImGui.TextWrapped($"{PluginName} has asked Snowcloak to prepare a pair request for {CharacterName}.");
        ImGuiHelpers.ScaledDummy(5f);
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Nothing is sent until you confirm here.");
        ImGuiHelpers.ScaledDummy(12f);

        var sending = _sendTask is { IsCompleted: false };
        ImGui.BeginDisabled(sending);
        if (ImGui.Button(sending ? "Sending..." : "Send pair request", new Vector2(150f, 0f)))
        {
            _sendTask = _pairRequestService.SendPairRequestAsync(Ident);
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(90f, 0f)))
        {
            IsOpen = false;
        }
        ImGui.EndDisabled();

        if (_sendTask?.IsCompletedSuccessfully == true)
        {
            IsOpen = false;
        }
    }
}
