namespace Tanks.Net;

public unsafe interface IBufferResolver
{
    public byte* Deref(BufferHandle handle); // caller must explicitly skip metadata
    public byte* Deref(ref PipeEvent evt);
    public int BufferCapacity { get; }
}

public interface IPipeWriter : IBufferResolver
{
    public bool TryRent(ref BufferHandle handle);
    public void Abandon(BufferHandle handle);
    public bool TryEnqueue(ref PipeEvent evt);
    public bool TryEnqueue(BufferHandle handle); // caller must write metadata in slot
}

public interface IPipeReader : IBufferResolver
{
    public bool TryDequeue(ref BufferHandle handle); // caller must read metadata in slot
    public bool TryDequeue(ref PipeEvent evt);
    public void Return(BufferHandle handle);
}

internal sealed unsafe class PipeReader : IPipeReader
{
    private readonly Ring2<BufferHandle> _ring;
    private readonly Pool2 _pool;

    public PipeReader(
        Ring2<BufferHandle> ring,
        Pool2 pool)
    {
        ThrowHelper.ThrowIfLessThan(pool.SlotSize, NetworkConstants.PipeEventHeadroom);

        _ring = ring;
        _pool = pool;
    }

    public int BufferCapacity => _pool.SlotSize;

    public byte* Deref(BufferHandle handle)
    {
        return _pool.Deref(handle);
    }

    public bool TryDequeue(ref BufferHandle handle)
    {
        return _ring.TryDequeue(ref handle);
    }

    public void Return(BufferHandle handle)
    {
        _pool.Return(handle);
    }

    public byte* Deref(ref PipeEvent evt)
    {
        return _pool.Deref(evt.Buffer) + NetworkConstants.PipeEventHeadroom;
    }

    public bool TryDequeue(ref PipeEvent evt)
    {
        if (_ring.TryDequeue(ref evt.Buffer))
        {
            evt.Metadata = *(PipeEventMetadata*)_pool.Deref(evt.Buffer);
            return true;
        }
        return false;
    }

    public void Return(ref PipeEvent evt)
    {
        _pool.Return(evt.Buffer);
    }
}

internal sealed unsafe class PipeWriter : IPipeWriter
{
    private readonly Ring2<BufferHandle> _ring;
    private readonly Pool2 _pool;

    public PipeWriter(
        Ring2<BufferHandle> ring,
        Pool2 pool)
    {
        ThrowHelper.ThrowIfLessThan(pool.SlotSize, NetworkConstants.PipeEventHeadroom);

        _ring = ring;
        _pool = pool;
    }

    public int BufferCapacity => _pool.SlotSize;

    public bool TryRent(ref BufferHandle handle)
    {
        return _pool.TryRent(ref handle);
    }

    public void Abandon(BufferHandle handle)
    {
        _pool.Abandon(handle);
    }

    public bool TryEnqueue(BufferHandle handle)
    {
        return _ring.TryEnqueue(handle);
    }

    public byte* Deref(BufferHandle handle)
    {
        return _pool.Deref(handle);
    }

    public byte* Deref(ref PipeEvent evt)
    {
        return _pool.Deref(evt.Buffer) + NetworkConstants.PipeEventHeadroom;
    }

    public bool TryEnqueue(ref PipeEvent evt)
    {
        *(PipeEventMetadata*)_pool.Deref(evt.Buffer) = evt.Metadata;
        return _ring.TryEnqueue(evt.Buffer);
    }

    public bool TryRent(ref PipeEvent evt)
    {
        return _pool.TryRent(ref evt.Buffer);
    }

    public void Abandon(ref PipeEvent evt)
    {
        _pool.Abandon(evt.Buffer);
    }
}