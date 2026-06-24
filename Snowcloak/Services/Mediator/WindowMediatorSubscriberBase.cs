using System.Numerics;
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using Snowcloak.UI.Components;

namespace Snowcloak.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;
    private bool _windowStylePushed;

    protected WindowMediatorSubscriberBase(ILogger logger, SnowMediator mediator, string name,
        PerformanceCollectorService performanceCollectorService) : base(name)
    {
        _logger = logger;
        Mediator = mediator;
        _performanceCollectorService = performanceCollectorService;
        _logger.LogTrace("Creating {type}", GetType());

        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                Toggle();
            }
        });
    }

    public SnowMediator Mediator { get; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected void SetScaledSizeConstraints(Vector2 minimumSize, Vector2? maximumSize = null)
    {
        // Dalamud's Window.SizeConstraints is already scaled by ImGuiHelpers.GlobalScale
        // internally
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = minimumSize,
            MaximumSize = maximumSize ?? new Vector2(float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        ModernWindowStyle.PushTitleBar();
        _windowStylePushed = true;
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        if (!_windowStylePushed)
            return;

        ModernWindowStyle.PopTitleBar();
        _windowStylePushed = false;
    }

    public override void Draw()
    {
        using var palette = ModernWindowStyle.PushContentPalette();
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
    }

    protected abstract void DrawInternal();

    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}