using Snowcloak.Configuration.Configurations;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Snowcloak.Configuration;

public interface IConfigDocument
{
    string FileName { get; }
    object LoadFromText(string text);
    object CreateDefault();
    void SetCurrent(object value);
    string SerializeCurrent();
    void RaiseChanged();
}

internal static class ConfigJson
{
    public static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
}

public abstract class ConfigDocument<T> : IConfigDocument where T : ISnowcloakConfiguration, new()
{
    private readonly ConfigStore _store;
    private T _current = default!;

    protected ConfigDocument(ConfigStore store)
    {
        _store = store;
        _store.Register(this);
    }

    public abstract string FileName { get; }

    protected virtual IReadOnlyList<IConfigMigration> Migrations => [];

    public string ConfigurationDirectory => _store.ConfigurationDirectory;

    public T Current => _current;

    public event Action? ConfigChanged;

    public void Update(Action<T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _store.Commit(this, () => mutate(_current));
    }

    object IConfigDocument.LoadFromText(string text)
    {
        var migrations = Migrations;
        if (migrations.Count == 0)
            return JsonSerializer.Deserialize<T>(text) ?? new T();

        if (JsonNode.Parse(text) is not JsonObject node)
            throw new InvalidDataException("Config root is not a JSON object.");

        foreach (var migration in migrations.OrderBy(m => m.FromVersion))
        {
            var version = node["Version"]?.GetValue<int>() ?? 0;
            if (version == migration.FromVersion)
                node = migration.Apply(node);
        }

        return node.Deserialize<T>() ?? new T();
    }

    object IConfigDocument.CreateDefault() => new T();

    void IConfigDocument.SetCurrent(object value) => _current = (T)value;

    string IConfigDocument.SerializeCurrent() => JsonSerializer.Serialize(_current, ConfigJson.WriteOptions);

    void IConfigDocument.RaiseChanged() => ConfigChanged?.Invoke();
}
