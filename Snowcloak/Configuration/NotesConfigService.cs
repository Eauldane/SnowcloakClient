using Snowcloak.Configuration.Configurations;

namespace Snowcloak.Configuration;

public class NotesConfigService : StateDocument<UidNotesConfig>
{
    public const string ConfigName = "notes.json";

    public NotesConfigService(StateDocumentStore store) : base(store)
    {
    }

    public override string FileName => ConfigName;
}
