namespace Snowcloak.Interop.Ipc;

public enum IpcRole
{
    Required,
    Optional,
    Special,
}

public enum IpcState
{
    Available,
    Missing,
    Disabled,
    VersionMismatch,
    Error,
}

[Flags]
public enum IpcCapability
{
    None = 0,
    ModFiles = 1 << 0,
    MetaManipulations = 1 << 1,
    Redraw = 1 << 2,
    ResourcePaths = 1 << 3,
    Appearance = 1 << 4,
    BodyScale = 1 << 5,
    HeelOffset = 1 << 6,
    Titles = 1 << 7,
    Moodles = 1 << 8,
    PetNames = 1 << 9,
    GposeActors = 1 << 10,
    Pose = 1 << 11,
}

public sealed record IpcStatus(
    string Name,
    IpcRole Role,
    IpcState State,
    IpcCapability Capabilities,
    string? Version = null,
    string? RequiredVersion = null,
    string? Detail = null)
{
    public bool IsAvailable => State == IpcState.Available;

    public static IpcStatus Available(string name, IpcRole role, IpcCapability capabilities, string? version = null)
        => new(name, role, IpcState.Available, capabilities, version);

    public static IpcStatus Missing(string name, IpcRole role, IpcCapability capabilities, string? requiredVersion = null)
        => new(name, role, IpcState.Missing, capabilities, RequiredVersion: requiredVersion);

    public static IpcStatus Disabled(string name, IpcRole role, IpcCapability capabilities, string? version = null, string? detail = null)
        => new(name, role, IpcState.Disabled, capabilities, version, Detail: detail);

    public static IpcStatus VersionMismatch(string name, IpcRole role, IpcCapability capabilities, string? version, string? requiredVersion)
        => new(name, role, IpcState.VersionMismatch, capabilities, version, requiredVersion);

    public static IpcStatus Error(string name, IpcRole role, IpcCapability capabilities, string detail, string? version = null, string? requiredVersion = null)
        => new(name, role, IpcState.Error, capabilities, version, requiredVersion, detail);
}
