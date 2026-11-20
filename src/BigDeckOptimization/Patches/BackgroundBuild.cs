using System;
using System.Threading;
using MegaCrit.Sts2.Core.Logging;

namespace BigDeckOptimization.Patches;

/// <summary>
/// One computation over the game's own code (its IL and types: no patch state, no game state, nothing another mod can change)
/// run once on its own background thread while the game loads (P1 of 2026-11-20: RelicFlashMerge's and CardFlightLimit's code
/// sets, 100-200 ms that froze the main menu's first seconds), as LimitlessCloneMemo's closures worker does: below-normal
/// priority, never a lock the main thread waits on, the result published whole. The caller, on the main thread, takes the
/// result if it is there (<see cref="Result"/>) and otherwise computes it itself as before (never waits): the same function, so
/// the same result either way. What the result is used for (who patches what) is asked at use time, never here.
/// </summary>
internal sealed class BackgroundBuild<T> where T : class
{
    private readonly string _name;
    private readonly Func<T> _build;
    private T? _result;
    private string? _error;
    private double _ms;
    /// <summary>0 not started, 1 running, 2 done (built or failed).</summary>
    private int _state;

    public BackgroundBuild(string name, Func<T> build)
    {
        _name = name;
        _build = build;
    }

    /// <summary>Starts the thread, once. Never throws: a thread that can't start leaves the build to the main thread.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            return;
        }
        try
        {
            new Thread(Run)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = ModEntry.Id + " " + _name,
            }.Start();
        }
        catch (Exception e)
        {
            _error = e.Message;
            Volatile.Write(ref _state, 2);
            Log.Info($"[{ModEntry.Id}] {_name}: the background build didn't start ({e.Message}): built on the main thread when first needed");
        }
    }

    private void Run()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            T result = _build();
            _ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Volatile.Write(ref _result, result);
            Log.Info($"[{ModEntry.Id}] {_name}: built in the background in {_ms:F0} ms");
        }
        catch (Exception e)
        {
            // The main thread builds it again when needed, and logs its own failure as before.
            _error = e.Message;
            Log.Info($"[{ModEntry.Id}] {_name}: the background build failed ({e.Message}): built on the main thread when first needed");
        }
        finally
        {
            Volatile.Write(ref _state, 2);
        }
    }

    /// <summary>The result once built in the background; null while it runs, when it failed or never started.</summary>
    public T? Result => Volatile.Read(ref _state) == 2 ? Volatile.Read(ref _result) : null;

    /// <summary>The background build is over (built or failed).</summary>
    public bool Done => Volatile.Read(ref _state) == 2;

    /// <summary>Test builds and logs: the state, in words.</summary>
    public string Describe() => Volatile.Read(ref _state) switch
    {
        0 => "not started",
        1 => "running",
        _ => Volatile.Read(ref _result) != null ? $"built in the background in {_ms:F0} ms" : $"failed ({_error})",
    };
}
