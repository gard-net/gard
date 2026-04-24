using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Gard.Core.Utils;

/// <summary>
/// En Windows, el scheduler por defecto tiene granularidad ~15.6 ms.
/// Cualquier await / Task.Delay / IOCP completion se alinea a esa tick,
/// limitando el sender UDP a ~1250 pkt/s aunque el pacing interno sea
/// sub-ms. timeBeginPeriod(1) baja la granularidad a 1 ms para todo el
/// proceso. Debe pararse con timeEndPeriod al shutdown.
///
/// En macOS/Linux no es necesario y el método es no-op.
/// </summary>
public static class WindowsTimerResolution
{
    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    private static int _active;

    public static IDisposable RaiseToOneMs()
    {
        if (!OperatingSystem.IsWindows()) return NoOp.Instance;
        if (Interlocked.Increment(ref _active) != 1) return new Restorer();
        _ = timeBeginPeriod(1);
        return new Restorer();
    }

    private sealed class Restorer : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (!OperatingSystem.IsWindows()) return;
            if (Interlocked.Decrement(ref _active) == 0)
                _ = timeEndPeriod(1);
        }
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}
