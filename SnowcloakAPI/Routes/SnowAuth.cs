namespace Snowcloak.API.Routes;

public class SnowAuth
{
    public const string Auth = "/auth";
    public const string Auth_CreateIdent = "createWithIdent";
    public const string Auth_CreateIdentV2 = "createWithIdentV2";
    public const string Auth_Register = "registerNewKey";
    public const string Auth_RegisterV2 = "registerNewKeyV2";
    public static Uri AuthFullPath(Uri baseUri) => new Uri(baseUri, Auth + "/" + Auth_CreateIdent);
    public static Uri AuthV2FullPath(Uri baseUri) => new Uri(baseUri, Auth + "/" + Auth_CreateIdentV2);
    public static Uri AuthRegisterFullPath(Uri baseUri) => new Uri(baseUri, Auth + "/" + Auth_Register);
    public static Uri AuthRegisterV2FullPath(Uri baseUri) => new Uri(baseUri, Auth + "/" + Auth_RegisterV2);
}