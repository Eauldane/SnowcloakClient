using Snowcloak.Configuration;
using Snowcloak.Core.BbCode;
using Snowcloak.Services.Mediator;
using Snowcloak.UI.Components.BbCode;

namespace Snowcloak.UI.Components;

public sealed class BbCodeRenderService
{
    private readonly BbCodeRenderer _bbCodeRenderer;
    private readonly SnowcloakConfigService _configService;
    private readonly SnowMediator _mediator;

    public BbCodeRenderService(BbCodeRenderer bbCodeRenderer, SnowcloakConfigService configService, SnowMediator mediator)
    {
        _bbCodeRenderer = bbCodeRenderer;
        _configService = configService;
        _mediator = mediator;
    }

    public void Render(string text, float wrapWidth, BbCodeRenderOptions? options = null)
    {
        var renderOptions = options ?? new BbCodeRenderOptions();

        if (!_configService.Current.AllowBbCodeImages)
        {
            renderOptions = renderOptions with { AllowImages = false };
        }

        if (renderOptions.OnLinkClicked == null)
        {
            renderOptions = renderOptions with
            {
                OnLinkClicked = url => _mediator.Publish(new OpenBbCodeLinkPopupMessage(url)),
            };
        }

        _bbCodeRenderer.Render(text, wrapWidth, renderOptions);
    }
}
