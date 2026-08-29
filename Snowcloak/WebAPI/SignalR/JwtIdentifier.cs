namespace Snowcloak.WebAPI.SignalR;

public record JwtIdentifier(string ApiUrl, string CharaHash, string SecretKey, ulong ContentId)
{
    public override string ToString()
    {
        return "{JwtIdentifier; Url: " + ApiUrl + ", Chara: " + CharaHash + ", HasSecretKey: " + !string.IsNullOrEmpty(SecretKey) + "}";
    }
}