using System.Text.Json.Nodes;

namespace Snowcloak.Configuration;

public interface IConfigMigration
{
    int FromVersion { get; }

    JsonObject Apply(JsonObject node);
}
