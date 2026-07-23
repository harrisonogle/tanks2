using System.Diagnostics;
using Tanks.Net;
using Tanks.Sim;

namespace Tanks.Engine;

public static unsafe class EngineEventExtensions
{
    // Emit a message with an empty payload
    public static void Emit(this ref EngineState engine, EngineEventKind kind, int length = 0)
    {
        Debug.Assert(length < ushort.MaxValue);

        // Local handle on purpose: engine.Handle is the drain loops' iteration scratch,
        // and emits run from inside those loops. Sharing it would clobber the in-flight
        // RX handle and return a TX-pool handle to the RX pool.
        BufferHandle handle = default;
        if (!engine.RenderTx.TryRent(ref handle))
            return;

        bool bufferConsumed = false;

        try
        {
            byte* buffer = engine.RenderTx.Deref(handle);
            var metadata = (EngineEventMetadata*)buffer;
            byte* payload = buffer + sizeof(EngineEventMetadata);

            metadata->Kind = kind;
            metadata->Offset = 0;
            metadata->Length = (ushort)length;

            if (!(bufferConsumed = engine.RenderTx.TryEnqueue(handle)))
            {
                engine.RenderTx.Abandon(handle);
                bufferConsumed = true;
            }
        }
        finally
        {
            if (!bufferConsumed)
            {
                engine.RenderTx.Abandon(handle);
            }
        }
    }

    public static void EmitMatchStarted(this ref EngineState engine)
    {
        // Local handle on purpose: engine.Handle is the drain loops' iteration scratch,
        // and emits run from inside those loops. Sharing it would clobber the in-flight
        // RX handle and return a TX-pool handle to the RX pool.
        BufferHandle handle = default;
        if (!engine.RenderTx.TryRent(ref handle))
            return;

        bool bufferConsumed = false;

        try
        {
            byte* buffer = engine.RenderTx.Deref(handle);
            var metadata = (EngineEventMetadata*)buffer;
            byte* payload = buffer + sizeof(EngineEventMetadata);

            metadata->Kind = EngineEventKind.MatchStarted;
            metadata->Offset = 0;

            if (!engine.GameView.Arena.TrySerialize(
                    payload,
                    length: engine.RenderTx.SlotSize - sizeof(EngineEventMetadata),
                    out int bytesWritten) ||
                bytesWritten > ushort.MaxValue ||
                bytesWritten < 1)
            {
                return;
            }
            metadata->Length = (ushort)bytesWritten;

            if (!(bufferConsumed = engine.RenderTx.TryEnqueue(handle)))
            {
                engine.RenderTx.Abandon(handle);
                bufferConsumed = true;
            }
        }
        finally
        {
            if (!bufferConsumed)
            {
                engine.RenderTx.Abandon(handle);
            }
        }
    }

    public static void EmitGameState(this ref EngineState engine, GameState gameState)
    {
        // Local handle on purpose: engine.Handle is the drain loops' iteration scratch,
        // and emits run from inside those loops. Sharing it would clobber the in-flight
        // RX handle and return a TX-pool handle to the RX pool.
        BufferHandle handle = default;
        if (!engine.RenderTx.TryRent(ref handle))
            return;

        bool bufferConsumed = false;

        try
        {
            byte* buffer = engine.RenderTx.Deref(handle);
            var metadata = (EngineEventMetadata*)buffer;
            byte* payload = buffer + sizeof(EngineEventMetadata);

            metadata->Kind = EngineEventKind.GameState;
            metadata->Offset = 0;
            if (!gameState.TrySerialize(payload, engine.RenderTx.SlotSize - sizeof(EngineEventMetadata), out int bytesWritten) ||
                bytesWritten > ushort.MaxValue ||
                bytesWritten < 1)
            {
                return;
            }
            metadata->Length = (ushort)bytesWritten;

            if (!(bufferConsumed = engine.RenderTx.TryEnqueue(handle)))
            {
                engine.RenderTx.Abandon(handle);
                bufferConsumed = true;
            }
        }
        finally
        {
            if (!bufferConsumed)
            {
                engine.RenderTx.Abandon(handle);
            }
        }
    }
}