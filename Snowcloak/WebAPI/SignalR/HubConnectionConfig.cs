using Microsoft.AspNetCore.Http.Connections;
using System.Text.Json.Serialization;

namespace Snowcloak.WebAPI.SignalR;

public record HubConnectionConfig
{
    [JsonPropertyName("api_url")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("hub_url")]
    public string HubUrl { get; set; } = string.Empty;

    private readonly bool? _skipNegotiation;

    [JsonPropertyName("skip_negotiation")]
    public bool SkipNegotiation
    {
        get => _skipNegotiation ?? true;
        init => _skipNegotiation = value;
    }

    [JsonPropertyName("transports")]
    public string[]? Transports { get; set; }

    [JsonIgnore]
    public HttpTransportType TransportType
    {
        get
        {
            if (Transports == null || Transports.Length == 0)
                return HttpTransportType.WebSockets;

            HttpTransportType result = HttpTransportType.None;

            foreach (var transport in Transports)
            {
                result |= transport.ToLowerInvariant() switch
                {
                    "websockets" => HttpTransportType.WebSockets,
                    "serversentevents" => HttpTransportType.ServerSentEvents,
                    "longpolling" => HttpTransportType.LongPolling,
                    _ => HttpTransportType.None
                };
            }

            return result;
        }
    }
}
