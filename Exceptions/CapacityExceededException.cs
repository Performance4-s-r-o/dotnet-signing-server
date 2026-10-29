namespace DotNetSigningServer.Exceptions;

/// <summary>A capacity pool stayed full for the whole wait (#69); the caller gets 503 and retries.</summary>
public sealed class CapacityExceededException : Exception
{
    public string Pool { get; }

    public CapacityExceededException(string pool) : base($"Capacity pool '{pool}' is full")
    {
        Pool = pool;
    }
}
