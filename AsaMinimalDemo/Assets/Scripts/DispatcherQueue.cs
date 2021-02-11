using System;
using System.Collections.Concurrent;

public static class DispatcherQueue
{
    private static readonly ConcurrentQueue<Action> _dispatchQueue;

    static DispatcherQueue()
    {
        _dispatchQueue = new ConcurrentQueue<Action>();
    }

    public static void Enqueue(Action action)
    {
        _dispatchQueue.Enqueue(action);
    }

    public static bool DequeueAndExecute()
    {
        if (_dispatchQueue.TryDequeue(out Action action))
        {
            action();
            return true;
        }
        return false;
    }
}