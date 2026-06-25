namespace Tanks.Net;

internal sealed class ObjectPool<T> where T : new()
{
    private readonly Stack<T> _pool = new();

    public T Rent()
    {
        return _pool.Count > 0 ? _pool.Pop() : new();
    }

    public void Return(T obj)
    {
        _pool.Push(msg);
    }
}