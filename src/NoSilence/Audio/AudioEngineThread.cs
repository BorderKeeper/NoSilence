using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NoSilence.Audio;

/// <summary>
/// The single thread that owns every piece of WASAPI state.
/// </summary>
/// <remarks>
/// It is explicitly <see cref="ApartmentState.MTA"/>. WinForms' UI thread is STA, and
/// WASAPI session notifications (<c>IAudioSessionNotification</c>) are apartment-sensitive:
/// registered from an STA thread they are known to silently never fire. Confining all of it
/// to one MTA thread also means the session cache, the device state machine and the output
/// stream need no locking between themselves.
/// <para>
/// Endpoint notifications arrive on an audio-service pool thread and must be handed off
/// here immediately — calling back into the enumerator from inside one deadlocks.
/// </para>
/// </remarks>
internal sealed class AudioEngineThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<AudioEngineThread> _log;
    private readonly int _tickMs;

    private Thread? _thread;
    private bool _disposed;

    public AudioEngineThread(ILogger<AudioEngineThread> log, int tickMs = 250)
    {
        _log = log;
        _tickMs = Math.Max(20, tickMs);
    }

    /// <summary>Raised on the engine thread at the tick interval. Exceptions are logged, never fatal.</summary>
    public event Action? Tick;

    public bool IsOnEngineThread => Environment.CurrentManagedThreadId == _thread?.ManagedThreadId;

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            Name = "NoSilence.AudioEngine",
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Queues work for the engine thread and returns immediately.</summary>
    public void Post(Action work)
    {
        if (_disposed || _queue.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _queue.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Shutdown raced with the caller; dropping the work is correct.
        }
    }

    /// <summary>Runs work on the engine thread and waits for it. Re-entrant if already there.</summary>
    public void Invoke(Action work)
    {
        if (IsOnEngineThread)
        {
            work();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfoHolder holder = new();

        Post(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                holder.Error = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        holder.Rethrow();
    }

    public T Invoke<T>(Func<T> work)
    {
        T result = default!;
        Invoke(() => result = work());
        return result;
    }

    private void Run()
    {
        _log.LogDebug("Audio engine thread started (MTA).");
        CancellationToken token = _cts.Token;
        long nextTick = Environment.TickCount64 + _tickMs;

        try
        {
            while (!token.IsCancellationRequested)
            {
                long now = Environment.TickCount64;

                if (now >= nextTick)
                {
                    // Schedule from now rather than accumulating, so a slow tick cannot
                    // build up a backlog it then tries to catch up on in a burst.
                    nextTick = now + _tickMs;
                    SafeInvoke(() => Tick?.Invoke(), "tick");
                    continue;
                }

                if (_queue.TryTake(out Action? work, (int)(nextTick - now), token))
                {
                    SafeInvoke(work, "queued work");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            // Drain anything queued during shutdown so teardown actions still run.
            while (_queue.TryTake(out Action? work))
            {
                SafeInvoke(work, "shutdown work");
            }

            _log.LogDebug("Audio engine thread stopped.");
        }
    }

    private void SafeInvoke(Action work, string what)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            // One bad tick must never take the audio engine down; the device state machine
            // is what recovers from real faults.
            _log.LogError(ex, "Unhandled exception in audio engine {What}.", what);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _cts.Cancel();

        if (_thread is { IsAlive: true } && !IsOnEngineThread && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.LogWarning("Audio engine thread did not stop within 5 seconds.");
        }

        _cts.Dispose();
        _queue.Dispose();
    }

    private sealed class ExceptionDispatchInfoHolder
    {
        public Exception? Error { get; set; }

        public void Rethrow()
        {
            if (Error is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();
            }
        }
    }
}
