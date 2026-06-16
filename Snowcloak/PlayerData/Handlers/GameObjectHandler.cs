using Dalamud.Game.ClientState.Objects.Types;
using ElezenTools.Services;
using Microsoft.Extensions.Logging;
using Snowcloak.Game.Objects;
using Snowcloak.Services;
using Snowcloak.Services.Mediator;
using ObjectKind = Snowcloak.API.Data.Enum.ObjectKind;

namespace Snowcloak.PlayerData.Handlers;

public sealed partial class GameObjectHandler : DisposableMediatorSubscriberBase, IGameObjectHandle
{
    private readonly byte[] _customizeData = new byte[DrawStateReader.CustomizeDataLength];
    private readonly DalamudUtilService _dalamudUtil;
    private readonly byte[] _equipSlotData = new byte[DrawStateReader.EquipmentDataLength];
    private readonly Func<nint> _getAddress;
    private readonly bool _isOwnedObject;
    private readonly ushort[] _mainHandData = new ushort[DrawStateReader.WeaponDataLength];
    private readonly GameObjectHandlerMonitor _monitor;
    private readonly ushort[] _offHandData = new ushort[DrawStateReader.WeaponDataLength];
    private byte _classJob;
    private Task? _delayedZoningTask;
    private int _disposed;
    private bool _haltProcessing;
    private ushort? _objectIndex;
    private CancellationTokenSource _zoningCts = new();

    public GameObjectHandler(
        ILogger<GameObjectHandler> logger,
        SnowMediator mediator,
        DalamudUtilService dalamudUtil,
        GameObjectHandlerMonitor monitor,
        ObjectKind objectKind,
        Func<nint> getAddress,
        bool ownedObject = true)
        : base(logger, mediator)
    {
        _dalamudUtil = dalamudUtil;
        _monitor = monitor;
        _getAddress = () =>
        {
            Service.EnsureOnFramework();
            return getAddress.Invoke();
        };
        _isOwnedObject = ownedObject;
        ObjectKind = objectKind;
        Name = string.Empty;

        if (ownedObject)
        {
            Mediator.Subscribe<TransientResourceChangedMessage>(this, HandleTransientResourceChanged);
        }

        Mediator.Subscribe<ZoneSwitchEndMessage>(this, _ => ZoneSwitchEnd());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, _ => ZoneSwitchStart());
        Mediator.Subscribe<CutsceneStartMessage>(this, _ => _haltProcessing = true);
        Mediator.Subscribe<CutsceneEndMessage>(this, _ =>
        {
            _haltProcessing = false;
            ZoneSwitchEnd();
        });
        Mediator.Subscribe<PenumbraStartRedrawMessage>(this, msg =>
        {
            if (msg.Address == Address)
            {
                _haltProcessing = true;
            }
        });
        Mediator.Subscribe<PenumbraEndRedrawMessage>(this, msg =>
        {
            if (msg.Address == Address)
            {
                _haltProcessing = false;
            }
        });

        Mediator.Publish(new GameObjectHandlerCreatedMessage(this, _isOwnedObject));

        Service.RunOnFrameworkAsync(RefreshFromFramework).GetAwaiter().GetResult();
        _monitor.Register(this);
    }

    public nint Address { get; private set; }
    public ushort? ObjectIndex => _objectIndex;
    public GameObjectDrawCondition CurrentDrawCondition { get; private set; } = GameObjectDrawCondition.None;
    public byte Gender { get; private set; }
    public string Name { get; private set; }
    public ObjectKind ObjectKind { get; }
    public byte RaceId { get; private set; }
    public byte TribeId { get; private set; }

    internal string PerformanceCounterName => "CheckAndUpdateObject>"
        + $"{(_isOwnedObject ? "Self" : "Other")}+{ObjectKind}/{(string.IsNullOrEmpty(Name) ? "Unk" : Name)}+{Address:X}";

    internal bool ShouldProcessFrameworkUpdate => _delayedZoningTask?.IsCompleted ?? true;

    internal void RefreshFromFramework()
    {
        Span<byte> equipmentData = stackalloc byte[DrawStateReader.EquipmentDataLength];
        Span<ushort> mainHandData = stackalloc ushort[DrawStateReader.WeaponDataLength];
        Span<ushort> offHandData = stackalloc ushort[DrawStateReader.WeaponDataLength];
        Span<byte> customizeData = stackalloc byte[DrawStateReader.CustomizeDataLength];

        var previousAddress = Address;
        var previousDrawObject = DrawObjectAddress;
        var state = DrawStateReader.ReadCharacterObject(
            _getAddress(),
            ObjectKind == ObjectKind.Player,
            equipmentData,
            mainHandData,
            offHandData,
            customizeData);

        Address = state.Address;
        DrawObjectAddress = state.DrawObjectAddress;
        CurrentDrawCondition = state.DrawCondition;
        _objectIndex = state.ObjectIndex;

        if (_haltProcessing)
        {
            return;
        }

        var addressChanged = Address != previousAddress;
        var drawObjectChanged = DrawObjectAddress != previousDrawObject;

        if (!state.HasAppearanceData)
        {
            HandleUnavailableObject(addressChanged || drawObjectChanged);
            return;
        }

        var nameChanged = UpdateName(state.Name);
        var equipmentChanged = UpdateEquipmentData(state, equipmentData, mainHandData, offHandData);

        if (equipmentChanged && !_isOwnedObject)
        {
            LogChanged(Logger, this);
            return;
        }

        UpdateCensusData(state);
        var customizeChanged = UpdateBytes(customizeData, _customizeData);
        if (customizeChanged)
        {
            LogCustomizeDataChecked(Logger, this, state.HasHumanData ? "as human from draw obj" : "from game obj", customizeChanged);
        }

        if ((addressChanged || drawObjectChanged || equipmentChanged || customizeChanged || nameChanged) && _isOwnedObject)
        {
            LogSendingCreateCacheObjectMessage(Logger, this);
            Mediator.Publish(new CreateCacheForObjectMessage(this));
        }
    }

    public async Task ActOnFrameworkAfterEnsureNoDrawAsync(Action<ICharacter> act, CancellationToken token)
    {
        while (await Service.RunOnFrameworkAsync(() =>
               {
                   if (_haltProcessing)
                   {
                       RefreshFromFramework();
                   }

                   if (CurrentDrawCondition != GameObjectDrawCondition.None)
                   {
                       return true;
                   }

                   if (_dalamudUtil.CreateGameObject(Address) is ICharacter character)
                   {
                       act.Invoke(character);
                   }

                   return false;
               }).ConfigureAwait(false))
        {
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    public void CompareNameAndThrow(string name)
    {
        if (!string.Equals(Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Player name not equal to requested name, pointer invalid");
        }

        if (Address == nint.Zero)
        {
            throw new InvalidOperationException("Player pointer is zero, pointer invalid");
        }
    }

    public IGameObject? GetGameObject()
    {
        return _dalamudUtil.CreateGameObject(Address);
    }

    public void Invalidate()
    {
        Address = nint.Zero;
        DrawObjectAddress = nint.Zero;
        _objectIndex = null;
        _haltProcessing = false;
    }

    public async Task<bool> IsBeingDrawnRunOnFrameworkAsync()
    {
        return await Service.RunOnFrameworkAsync(IsBeingDrawn).ConfigureAwait(false);
    }

    public override string ToString()
    {
        var owned = _isOwnedObject ? "Self" : "Other";
        return $"{owned}/{ObjectKind}:{Name} ({Address:X},{DrawObjectAddress:X})";
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _monitor.Unregister(this);
        base.Dispose(disposing);
        CancelZoningDelay();
        _zoningCts.Dispose();
        Mediator.Publish(new GameObjectHandlerDestroyedMessage(this, _isOwnedObject));
    }

    private nint DrawObjectAddress { get; set; }

    private void CancelZoningDelay()
    {
        try
        {
            _zoningCts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            LogZoningDelayDisposed(Logger, ex, this);
        }
    }

    private void CancelAndDisposeZoningDelay()
    {
        CancelZoningDelay();
        _zoningCts.Dispose();
    }

    private void HandleTransientResourceChanged(TransientResourceChangedMessage msg)
    {
        if (!ShouldProcessFrameworkUpdate || msg.Address != Address)
        {
            return;
        }

        Mediator.Publish(new CreateCacheForObjectMessage(this));
    }

    private void HandleUnavailableObject(bool changed)
    {
        if (!changed)
        {
            return;
        }

        CurrentDrawCondition = GameObjectDrawCondition.DrawObjectZero;
        LogChanged(Logger, this);
        if (_isOwnedObject && ObjectKind != ObjectKind.Player)
        {
            Mediator.Publish(new ClearCacheForObjectMessage(this));
        }
    }

    private bool IsBeingDrawn()
    {
        if (_haltProcessing)
        {
            RefreshFromFramework();
        }

        if (_dalamudUtil.IsAnythingDrawing)
        {
            LogGlobalDrawBlock(Logger, this);
            return true;
        }

        LogDrawCondition(Logger, this, CurrentDrawCondition);
        return CurrentDrawCondition != GameObjectDrawCondition.None;
    }

    private bool UpdateEquipmentData(
        CharacterObjectState state,
        ReadOnlySpan<byte> equipmentData,
        ReadOnlySpan<ushort> mainHandData,
        ReadOnlySpan<ushort> offHandData)
    {
        var changed = UpdateBytes(equipmentData, _equipSlotData);
        if (state.HasHumanData)
        {
            var oldClassJob = _classJob;
            if (state.ClassJob != oldClassJob)
            {
                _classJob = state.ClassJob;
                LogClassJobChanged(Logger, this, oldClassJob, state.ClassJob);
                Mediator.Publish(new ClassJobChangedMessage(this));
            }

            if (state.HasMainHandData)
            {
                changed |= UpdateUShorts(mainHandData, _mainHandData);
            }

            if (state.HasOffHandData)
            {
                changed |= UpdateUShorts(offHandData, _offHandData);
            }
        }

        if (changed)
        {
            LogEquipDataChecked(Logger, this, state.HasHumanData ? "as human from draw obj" : "from game obj", changed);
        }

        return changed;
    }

    private void UpdateCensusData(CharacterObjectState state)
    {
        if (!state.HasHumanData
            || !_isOwnedObject
            || ObjectKind != ObjectKind.Player
            || (state.Gender == Gender && state.RaceId == RaceId && state.TribeId == TribeId))
        {
            return;
        }

        Mediator.Publish(new CensusUpdateMessage(state.Gender, state.RaceId, state.TribeId));
        Gender = state.Gender;
        RaceId = state.RaceId;
        TribeId = state.TribeId;
    }

    private bool UpdateName(string name)
    {
        if (string.Equals(name, Name, StringComparison.Ordinal))
        {
            return false;
        }

        Name = name;
        return true;
    }

    private static bool UpdateBytes(ReadOnlySpan<byte> current, byte[] cached)
    {
        if (current.SequenceEqual(cached))
        {
            return false;
        }

        current.CopyTo(cached);
        return true;
    }

    private static bool UpdateUShorts(ReadOnlySpan<ushort> current, ushort[] cached)
    {
        if (current.SequenceEqual(cached))
        {
            return false;
        }

        current.CopyTo(cached);
        return true;
    }

    private void ZoneSwitchEnd()
    {
        if (!_isOwnedObject)
        {
            return;
        }

        try
        {
            _zoningCts.CancelAfter(2500);
        }
        catch (ObjectDisposedException ex)
        {
            LogZoningDelayDisposed(Logger, ex, this);
        }
    }

    private void ZoneSwitchStart()
    {
        if (!_isOwnedObject)
        {
            return;
        }

        CancelAndDisposeZoningDelay();
        var zoningCts = new CancellationTokenSource();
        var zoningToken = zoningCts.Token;
        _zoningCts = zoningCts;
        LogStartingZoningDelay(Logger, this);
        _delayedZoningTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(120), zoningToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (zoningToken.IsCancellationRequested)
            {
                LogZoningDelayCancelled(Logger, ex, this);
            }
            finally
            {
                LogZoningDelayComplete(Logger, this);
            }
        });
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Trace, Message = "[{Handler}] Changed")]
    private static partial void LogChanged(ILogger logger, GameObjectHandler handler);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Trace, Message = "Checking [{Handler}] customize data {Source}, result: {Changed}")]
    private static partial void LogCustomizeDataChecked(ILogger logger, GameObjectHandler handler, string source, bool changed);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Debug, Message = "[{Handler}] Changed, Sending CreateCacheObjectMessage")]
    private static partial void LogSendingCreateCacheObjectMessage(ILogger logger, GameObjectHandler handler);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Trace, Message = "Zoning delay was already disposed for {Handler}")]
    private static partial void LogZoningDelayDisposed(ILogger logger, Exception exception, GameObjectHandler handler);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Trace, Message = "[{Handler}] IsBeingDrawn, Global draw block")]
    private static partial void LogGlobalDrawBlock(ILogger logger, GameObjectHandler handler);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Trace, Message = "[{Handler}] IsBeingDrawn, Condition: {Condition}")]
    private static partial void LogDrawCondition(ILogger logger, GameObjectHandler handler, GameObjectDrawCondition condition);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Trace, Message = "[{Handler}] classjob changed from {OldClassJob} to {NewClassJob}")]
    private static partial void LogClassJobChanged(ILogger logger, GameObjectHandler handler, byte oldClassJob, byte newClassJob);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Trace, Message = "Checking [{Handler}] equip data {Source}, result: {Changed}")]
    private static partial void LogEquipDataChecked(ILogger logger, GameObjectHandler handler, string source, bool changed);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Debug, Message = "[{Handler}] Starting Delay After Zoning")]
    private static partial void LogStartingZoningDelay(ILogger logger, GameObjectHandler handler);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Trace, Message = "Zoning delay cancelled for {Handler}")]
    private static partial void LogZoningDelayCancelled(ILogger logger, Exception exception, GameObjectHandler handler);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Debug, Message = "[{Handler}] Delay after zoning complete")]
    private static partial void LogZoningDelayComplete(ILogger logger, GameObjectHandler handler);
}
