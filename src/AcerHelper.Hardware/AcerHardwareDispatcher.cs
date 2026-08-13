// SPDX-License-Identifier: GPL-3.0-or-later
//
// Single-threaded apartment-stable executor for all WMI work.
//
// Why this exists:
//
// WbemMethodChannel holds raw COM interface pointers. A raw pointer is only
// valid in the apartment that created it - using one from another apartment
// fails with RPC_E_WRONG_THREAD (0x8001010E) rather than marshalling.
//
// A UI app makes that easy to get wrong. Avalonia's main thread is STA, so
// opening the channel from a view-model constructor puts the pointers in the
// STA, while Task.Run calls arrive on MTA thread-pool threads. The open
// succeeds and every later call throws.
//
// Task.Run is not a fix either: thread-pool threads are MTA, but the channel
// must then also have been created on an MTA thread, and nothing guarantees
// the same thread services later calls.
//
// So: one dedicated MTA thread owns the channel for its whole lifetime, and
// every call - including construction and disposal - is marshalled onto it.

using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace AcerHelper.Hardware;

[SupportedOSPlatform("windows")]
public sealed class AcerHardwareDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public AcerHardwareDispatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AcerHardware",
        };

        // Must be set before Start. This is the apartment every COM pointer
        // created by queued work will belong to.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch { /* each work item reports its own failure via its Task */ }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the hardware thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _queue.Add(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
        }
        catch (InvalidOperationException)
        {
            tcs.SetCanceled();   // queue completed during shutdown
        }

        return tcs.Task;
    }

    /// <summary>Runs <paramref name="work"/> on the hardware thread.</summary>
    public Task InvokeAsync(Action work)
        => InvokeAsync(() => { work(); return true; });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.CompleteAdding();

        // Give queued disposal work - releasing the fan guard in particular -
        // a chance to run before the process goes away.
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
