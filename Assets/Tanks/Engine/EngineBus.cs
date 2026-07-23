using System;
using System.Diagnostics;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Engine;

// Render thread component to interact with engine
public sealed unsafe class EngineBus
{
    private readonly UmemPool _rx;
    private readonly UmemPool _tx;
    private readonly ILog _logger;
    private readonly SimConfig _config;

    private BufferHandle? _arenaHandle;
    private BufferHandle? _gameStateHandle;
    private PhaseState _phase;

    public EngineBus(Umem umem, ILog logger, SimConfig config)
    {
        ThrowHelper.ThrowIfNull(umem);
        ThrowHelper.ThrowIfNull(logger);
        ThrowHelper.ThrowIfNull(config);

        _rx = umem.Rx;
        _tx = umem.Tx;
        _logger = logger;
        _config = config;
    }

    public PhaseState Phase => _phase;
    public SimConfig Config => _config;

    public Action? OnMatchStarted;
    public Action? OnMatchEnded;

    public bool TryGetGameState(out GameStateView gameState)
    {
        if (_gameStateHandle.HasValue)
        {
            // _tx on purpose: retained snapshot handles were dequeued from the engine's
            // render-TX pool; a cross-pool Deref fails the (per-pool randomized) gen check.
            byte* buffer = _tx.Deref(_gameStateHandle.Value);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            int totalLength = sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length;
            if (totalLength > _tx.SlotSize)
            {
                gameState = default;
                return false;
            }
            return GameStateView.TryCreate(buffer + sizeof(EngineEventMetadata) + metadata->Offset, metadata->Length, out gameState);
        }
        gameState = default;
        return false;
    }

    public bool TryGetArena(out ArenaView arena)
    {
        if (_arenaHandle.HasValue)
        {
            byte* buffer = _tx.Deref(_arenaHandle.Value);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            int totalLength = sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length;
            if (totalLength > _tx.SlotSize)
            {
                arena = default;
                return false;
            }
            return ArenaView.TryCreate(buffer + sizeof(EngineEventMetadata) + metadata->Offset, metadata->Length, out arena);
        }
        arena = default;
        return false;
    }

    // Drain ring for state messages from engine
    public void Pump()
    {
        BufferHandle handle = default;

        while (_tx.TryDequeue(ref handle))
        {
            bool retainBuffer = false;

            try
            {
                byte* buffer = _tx.Deref(handle);
                Debug.Assert(_tx.SlotSize >= sizeof(EngineEventMetadata));
                var metadata = (EngineEventMetadata*)buffer;
                int totalLength = sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length;
                if (totalLength > _tx.SlotSize)
                {
                    _logger.LogWarning($"engine TX dropped: total length {totalLength} exceeds slot size {_tx.SlotSize}");
                    continue;
                }

                // Lifecycle facts get handled phase-independently
                switch (metadata->Kind)
                {
                    case EngineEventKind.ConnectingToPeer:
                        _phase = PhaseState.ConnectingToPeer;
                        continue;
                    case EngineEventKind.MatchStarted:
                        ReturnArena();
                        ReturnGameState();
                        _arenaHandle = handle;
                        retainBuffer = true;
                        _phase = PhaseState.GameView;
                        InvokeCallback(OnMatchStarted, nameof(OnMatchStarted));
                        continue;
                    case EngineEventKind.MatchEnded:
                        ReturnArena();
                        ReturnGameState();
                        _phase = PhaseState.ConnectScreen;
                        InvokeCallback(OnMatchEnded, nameof(OnMatchEnded));
                        continue;
                }

                // Phase-dependent messages
                switch (_phase)
                {
                    case PhaseState.ConnectScreen:
                        break;
                    case PhaseState.ConnectingToPeer:
                        break;
                    case PhaseState.GameView:
                        switch (metadata->Kind)
                        {
                            case EngineEventKind.GameState:
                                ReturnGameState();
                                _gameStateHandle = handle;
                                retainBuffer = true;
                                break;
                        }
                        break;
                }
            }
            finally
            {
                if (!retainBuffer)
                    _tx.Return(handle);
            }
        }
    }

    private void InvokeCallback(Action? action, /*[CallerArgumentExpression(nameof(action))]*/string name)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError($"error invoking user-provided callback '{name}': {ex}");
        }
    }

    private void ReturnGameState()
    {
        if (_gameStateHandle.HasValue)
        {
            _tx.Return(_gameStateHandle.Value);
            _gameStateHandle = null;
        }
    }

    private void ReturnArena()
    {
        if (_arenaHandle.HasValue)
        {
            _tx.Return(_arenaHandle.Value);
            _arenaHandle = null;
        }
    }

    public void StartMatch(in PeerId peerId, in NetAddress netAddress)
    {
        BufferHandle handle = default;
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to start match: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.StartMatch;
            metadata->Offset = 0;
            metadata->Length = (ushort)sizeof(StartMatchMessage);
            if (_rx.SlotSize < sizeof(StartMatchMessage))
            {
                _logger.LogError("failed to start match: engine RX slot too small");
                return;
            }
            byte* payload = buffer + sizeof(EngineEventMetadata);
            var msg = (StartMatchMessage*)payload;

            msg->PeerId = peerId;
            msg->NetAddress = netAddress;

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to start match: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }

    public void StartLocalMatch()
    {
        BufferHandle handle = default;
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to start local match: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.StartLocalMatch;
            metadata->Offset = 0;
            metadata->Length = (ushort)sizeof(StartLocalMatchMessage);
            if (_rx.SlotSize < sizeof(StartLocalMatchMessage))
            {
                _logger.LogError("failed to start local match: engine RX slot too small");
                return;
            }

            /* empty message */

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to start local match: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }

    public void LeaveMatch()
    {
        BufferHandle handle = default;
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to start match: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.LeaveMatch;
            metadata->Offset = 0;
            metadata->Length = (ushort)sizeof(EndMatchMessage);
            if (_rx.SlotSize < sizeof(EndMatchMessage))
            {
                _logger.LogError("failed to start match: engine RX slot too small");
                return;
            }

            /* empty payload */

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to start match: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }

    public void SendInput(byte playerIndex, in PlayerInput playerInput)
    {
        BufferHandle handle = default;
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to send input: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.LocalInput;
            metadata->Offset = 0;
            metadata->Length = (ushort)(sizeof(LocalInputMessage) + sizeof(LocalInputPayload));
            if (_rx.SlotSize < sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length)
            {
                _logger.LogError("failed to send input: engine RX slot too small");
                return;
            }
            byte* payload = buffer + sizeof(EngineEventMetadata);
            byte* cursor = payload;
            var header = (LocalInputMessage*)cursor;
            header->Count = 1;
            cursor += sizeof(LocalInputMessage);
            var msg = (LocalInputPayload*)cursor;
            msg->Player = playerIndex;
            msg->Input = playerInput;
            cursor += sizeof(LocalInputPayload);
            Debug.Assert((int)(cursor - payload) == metadata->Length);

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to send input: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }

    public void SendInputs(ReadOnlySpan<PlayerInput> playerInputs)
    {
        BufferHandle handle = default;
        if (playerInputs.Length > byte.MaxValue)
        {
            _logger.LogError("failed to send inputs: count exceeded maximum players");
            return;
        }
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to send inputs: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.LocalInput;
            metadata->Offset = 0;
            metadata->Length = (ushort)(sizeof(LocalInputMessage) + playerInputs.Length * sizeof(LocalInputPayload));
            if (_rx.SlotSize < sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length)
            {
                _logger.LogError("failed to send inputs: engine RX slot too small");
                return;
            }
            byte* payload = buffer + sizeof(EngineEventMetadata);
            byte* cursor = payload;
            var header = (LocalInputMessage*)cursor;
            byte count = (byte)playerInputs.Length;
            header->Count = count;
            cursor += sizeof(LocalInputMessage);
            for (byte player = 0; player < count; player++)
            {
                var msg = (LocalInputPayload*)cursor;
                msg->Player = player;
                msg->Input = playerInputs[player];
                cursor += sizeof(LocalInputPayload);
            }
            Debug.Assert((int)(cursor - payload) == metadata->Length);

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to send inputs: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }

    public void SendInput(in PlayerInput player0, in PlayerInput player1)
    {
        BufferHandle handle = default;
        if (!_rx.TryRent(ref handle))
        {
            _logger.LogError("failed to send inputs: engine RX pool exhausted");
            return;
        }
        bool consumed = false;
        try
        {
            byte* buffer = _rx.Deref(handle);
            Debug.Assert(_rx.SlotSize >= sizeof(EngineEventMetadata));
            var metadata = (EngineEventMetadata*)buffer;
            metadata->Kind = EngineEventKind.LocalInput;
            metadata->Offset = 0;
            metadata->Length = (ushort)(sizeof(LocalInputMessage) + 2 * sizeof(LocalInputPayload));
            if (_rx.SlotSize < sizeof(EngineEventMetadata) + metadata->Offset + metadata->Length)
            {
                _logger.LogError("failed to send inputs: engine RX slot too small");
                return;
            }
            byte* payload = buffer + sizeof(EngineEventMetadata);
            byte* cursor = payload;
            var header = (LocalInputMessage*)cursor;
            header->Count = 2;
            cursor += sizeof(LocalInputMessage);

            var msg = (LocalInputPayload*)cursor;
            msg->Player = 0;
            msg->Input = player0;
            cursor += sizeof(LocalInputPayload);

            msg = (LocalInputPayload*)cursor;
            msg->Player = 1;
            msg->Input = player1;
            cursor += sizeof(LocalInputPayload);

            Debug.Assert((int)(cursor - payload) == metadata->Length);

            if (!(consumed = _rx.TryEnqueue(handle)))
            {
                _logger.LogError("failed to send inputs: engine RX ring full");
                return;
            }
        }
        finally
        {
            if (!consumed)
                _rx.Abandon(handle);
        }
    }
}