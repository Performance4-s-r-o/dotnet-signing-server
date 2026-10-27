using System.Threading;

namespace DotNetSigningServer.Services;

/// <summary>
/// Counts how often a request opens a PDF with iText (#70: at most once per request). Counts only
/// inside a <see cref="Begin"/> scope, so production pays one null check.
/// </summary>
internal static class PdfOpenCounter
{
    private static readonly AsyncLocal<int[]?> Scope = new();

    /// <summary>Opens counted so far in the current scope (0 outside one).</summary>
    public static int Current => Scope.Value?[0] ?? 0;

    public static void Record()
    {
        var counter = Scope.Value;
        if (counter != null) Interlocked.Increment(ref counter[0]);
    }

    /// <summary>Starts counting in the current async flow; read <c>Count</c> of the result.</summary>
    public static Handle Begin()
    {
        var counter = new int[1];
        Scope.Value = counter;
        return new Handle(counter);
    }

    public readonly struct Handle(int[] counter)
    {
        public int Count => Volatile.Read(ref counter[0]);
    }
}
