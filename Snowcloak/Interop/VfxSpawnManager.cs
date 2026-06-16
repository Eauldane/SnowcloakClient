using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Microsoft.Extensions.Logging;
using Snowcloak.Services.Mediator;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Snowcloak.Interop;

public unsafe sealed partial class VfxSpawnManager : DisposableMediatorSubscriberBase
{
    private const string WispPath = "bgcommon/world/common/vfx_for_event/eff/b0150_eext_y.avfx";
    private static readonly byte[] PoolName = "Client.System.Scheduler.Instance.VfxObject\0"u8.ToArray();

#pragma warning disable CS0649
    [Signature("E8 ?? ?? ?? ?? F3 0F 10 35 ?? ?? ?? ?? 48 89 43 08")]
    private readonly delegate* unmanaged<byte*, byte*, VfxStruct*> _createStaticVfx;

    [Signature("E8 ?? ?? ?? ?? ?? ?? ?? 8B 4A ?? 85 C9")]
    private readonly delegate* unmanaged<VfxStruct*, float, int, ulong> _runStaticVfx;

    [Signature("40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9")]
    private readonly delegate* unmanaged<VfxStruct*, nint> _removeStaticVfx;
#pragma warning restore CS0649

    private readonly Dictionary<Guid, SpawnedVfx> _spawnedObjects = [];

    public VfxSpawnManager(ILogger<VfxSpawnManager> logger, IGameInteropProvider gameInteropProvider, SnowMediator snowMediator)
        : base(logger, snowMediator ?? throw new ArgumentNullException(nameof(snowMediator)))
    {
        ArgumentNullException.ThrowIfNull(gameInteropProvider);

        gameInteropProvider.InitializeFromAttributes(this);
        snowMediator.Subscribe<GposeStartMessage>(this, _ => SetVisibilityForAll(0f));
        snowMediator.Subscribe<GposeEndMessage>(this, _ => RestoreVisibilityForAll());
        snowMediator.Subscribe<CutsceneStartMessage>(this, _ => SetVisibilityForAll(0f));
        snowMediator.Subscribe<CutsceneEndMessage>(this, _ => RestoreVisibilityForAll());
    }

    public Guid? SpawnObject(Vector3 position, Quaternion rotation, Vector3 scale, float r = 1f, float g = 1f, float b = 1f, float a = 0.5f)
    {
        LogTryingToSpawnVfx(Logger, position, rotation);
        var vfx = CreateStatic(WispPath, position, rotation, scale, r, g, b, a);
        if (vfx == null || (nint)vfx == nint.Zero)
        {
            LogFailedToSpawnVfx(Logger, position, rotation);
            return null;
        }

        var id = Guid.NewGuid();
        _spawnedObjects[id] = new SpawnedVfx((nint)vfx, a);
        LogSpawnedVfx(Logger, position, rotation, (nint)vfx);
        return id;
    }

    public void MoveObject(Guid id, Vector3 newPosition)
    {
        if (!_spawnedObjects.TryGetValue(id, out var spawned) || spawned.Address == nint.Zero)
        {
            return;
        }

        var vfx = (VfxStruct*)spawned.Address;
        vfx->Position = newPosition with { Y = newPosition.Y + 1 };
        vfx->Flags |= 2;
    }

    public void DespawnObject(Guid? id)
    {
        if (id == null || !_spawnedObjects.Remove(id.Value, out var spawned))
        {
            return;
        }

        Remove(spawned);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            RemoveAll();
        }
    }

    private VfxStruct* CreateStatic(string path, Vector3 position, Quaternion rotation, Vector3 scale, float r, float g, float b, float a)
    {
        VfxStruct* vfx;
        fixed (byte* terminatedPath = Encoding.UTF8.GetBytes(path).NullTerminate())
        fixed (byte* pool = PoolName)
        {
            vfx = _createStaticVfx(terminatedPath, pool);
        }

        if (vfx == null)
        {
            return null;
        }

        vfx->Position = position with { Y = position.Y + 1 };
        vfx->Rotation = rotation;
        vfx->SomeFlags &= 0xF7;
        vfx->Flags |= 2;
        vfx->Red = r;
        vfx->Green = g;
        vfx->Blue = b;
        vfx->Scale = scale;
        vfx->Alpha = a;

        _runStaticVfx(vfx, 0.0f, -1);
        return vfx;
    }

    private void SetVisibilityForAll(float visibility)
    {
        foreach (var spawned in _spawnedObjects.Values)
        {
            if (spawned.Address != nint.Zero)
            {
                ((VfxStruct*)spawned.Address)->Alpha = visibility;
            }
        }
    }

    private void RestoreVisibilityForAll()
    {
        foreach (var spawned in _spawnedObjects.Values)
        {
            if (spawned.Address != nint.Zero)
            {
                ((VfxStruct*)spawned.Address)->Alpha = spawned.Visibility;
            }
        }
    }

    private void RemoveAll()
    {
        foreach (var spawned in _spawnedObjects.Values)
        {
            Remove(spawned);
        }

        _spawnedObjects.Clear();
    }

    private void Remove(SpawnedVfx spawned)
    {
        if (spawned.Address == nint.Zero)
        {
            return;
        }

        LogDespawningVfx(Logger, spawned.Address);
        _removeStaticVfx((VfxStruct*)spawned.Address);
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Debug, Message = "Trying to spawn VFX at {Position}, {Rotation}")]
    private static partial void LogTryingToSpawnVfx(ILogger logger, Vector3 position, Quaternion rotation);

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Failed to spawn VFX at {Position}, {Rotation}")]
    private static partial void LogFailedToSpawnVfx(ILogger logger, Vector3 position, Quaternion rotation);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Spawned VFX at {Position}, {Rotation}: 0x{Pointer:X}")]
    private static partial void LogSpawnedVfx(ILogger logger, Vector3 position, Quaternion rotation, nint pointer);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Despawning VFX 0x{Pointer:X}")]
    private static partial void LogDespawningVfx(ILogger logger, nint pointer);

    private readonly record struct SpawnedVfx(nint Address, float Visibility);

    [StructLayout(LayoutKind.Explicit)]
    internal struct VfxStruct
    {
        [FieldOffset(0x38)]
        public byte Flags;

        [FieldOffset(0x50)]
        public Vector3 Position;

        [FieldOffset(0x60)]
        public Quaternion Rotation;

        [FieldOffset(0x70)]
        public Vector3 Scale;

        [FieldOffset(0x128)]
        public int ActorCaster;

        [FieldOffset(0x130)]
        public int ActorTarget;

        [FieldOffset(0x1B8)]
        public int StaticCaster;

        [FieldOffset(0x1C0)]
        public int StaticTarget;

        [FieldOffset(0x248)]
        public byte SomeFlags;

        [FieldOffset(0x260)]
        public float Red;

        [FieldOffset(0x264)]
        public float Green;

        [FieldOffset(0x268)]
        public float Blue;

        [FieldOffset(0x26C)]
        public float Alpha;
    }
}
