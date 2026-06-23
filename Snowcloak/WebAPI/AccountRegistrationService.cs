using Microsoft.Extensions.Logging;
using Snowcloak.Services;
using Snowcloak.Services.ServerConfiguration;
using Snowcloak.WebAPI.SignalR;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;

namespace Snowcloak.WebAPI;

public sealed partial class AccountRegistrationService : IDisposable
{
    private const string PatreonStartRoute = "/auth/patreon/link/start";
    private const string PatreonPollRoutePrefix = "/auth/patreon/link/poll/";
    private const string PatreonStatusRoute = "/auth/patreon/status";
    private const string PasswordLoginRoute = "/auth/password/login";
    private const string AccountPasswordRoute = "/auth/account/password";
    private const string AccountKeysRoute = "/auth/account/keys";
    private const string AccountKeyLinkRoute = "/auth/account/keys/link";
    private const string AccountUidRoute = "/auth/account/uid";
    private const string XivAuthRegisterStartRoute = "/auth/xivauth/register/start";
    private const string XivAuthRegisterPollRoutePrefix = "/auth/xivauth/register/poll/";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountRegistrationService> _logger;
    private readonly ServerRegistry _serverManager;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly TokenProvider _tokenProvider;

    private static string GenerateSecretKey()
    {
        return Convert.ToHexString(SHA256.HashData(RandomNumberGenerator.GetBytes(64)));
    }

    public AccountRegistrationService(ILogger<AccountRegistrationService> logger, DalamudUtilService dalamudUtilService, ServerRegistry serverManager, TokenProvider tokenProvider)
    {
        _logger = logger;
        _serverManager = serverManager;
        _dalamudUtilService = dalamudUtilService;
        _tokenProvider = tokenProvider;
        HttpClientHandler? handler = CreateHttpHandler();
        try
        {
            _httpClient = new HttpClient(handler, disposeHandler: true);
            handler = null;
        }
        finally
        {
            handler?.Dispose();
        }

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snowcloak", ver!.Major + "." + ver!.Minor + "." + ver!.Build));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static HttpClientHandler CreateHttpHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CheckCertificateRevocationList = true,
            MaxAutomaticRedirections = 5
        };
    }
}
