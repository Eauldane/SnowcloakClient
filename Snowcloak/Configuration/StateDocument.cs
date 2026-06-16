using Snowcloak.Configuration.Configurations;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Snowcloak.Configuration;

public interface IStateDocument
{
    string FileName { get; }
    IReadOnlyList<string> LegacyFileNames { get; }
    object LoadFromText(string text);
    object CreateDefault();
    void SetCurrent(object value);
    string SerializeCurrent();
    void NotifyChanged();
}

public abstract class StateDocument<T> : IStateDocument where T : ISnowcloakConfiguration, new()
{
    private readonly StateDocumentStore _store;
    private T _current = default!;

    protected StateDocument(StateDocumentStore store)
    {
        _store = store;
        _store.Register(this);
    }

    public abstract string FileName { get; }

    public virtual IReadOnlyList<string> LegacyFileNames => [FileName];

    protected virtual IReadOnlyList<IConfigMigration> Migrations => [];

    public string ConfigurationDirectory => _store.ConfigurationDirectory;

    public T Current => _current;

    public event EventHandler? ConfigChanged;

    public void Update(Action<T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _store.Commit(this, () => mutate(_current));
    }

    public object LoadFromText(string text)
    {
        var migrations = Migrations;
        if (migrations.Count == 0)
            return JsonSerializer.Deserialize<T>(text) ?? new T();

        if (JsonNode.Parse(text) is not JsonObject node)
            throw new InvalidDataException("State document root is not a JSON object.");

        foreach (var migration in migrations.OrderBy(m => m.FromVersion))
        {
            var version = node["Version"]?.GetValue<int>() ?? 0;
            if (version == migration.FromVersion)
                node = migration.Apply(node);
        }

        return node.Deserialize<T>() ?? new T();
    }

    public object CreateDefault() => new T();

    public void SetCurrent(object value) => _current = (T)value;

    public string SerializeCurrent() => JsonSerializer.Serialize(_current, ConfigJson.WriteOptions);

    public void NotifyChanged() => ConfigChanged?.Invoke(this, EventArgs.Empty);
}
