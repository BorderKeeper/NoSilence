namespace NoSilence.Playback;

/// <summary>
/// The play order. Pure: no I/O, no clock, no audio — which is what makes it worth unit
/// testing, and this is the part of a music player people notice when it is wrong.
/// </summary>
/// <remarks>
/// Two anti-repeat guarantees on top of a plain Fisher–Yates shuffle:
/// <list type="number">
/// <item>At a cycle boundary the track that just played is never first, which kills the
/// classic "shuffle repeated the song immediately".</item>
/// <item>Recently played tracks are pushed towards the back half of the new order, which
/// makes a small library feel far less repetitive than chance alone allows.</item>
/// </list>
/// </remarks>
internal sealed class ShuffleQueue
{
    private readonly Random _rng;
    private readonly List<TrackInfo> _tracks = [];
    private readonly Stack<int> _history = new();
    private readonly Queue<string> _recent = new();

    private int[] _order = [];

    /// <summary>Position of each track index within <see cref="_order"/>, so seeking is O(1).</summary>
    private int[] _positionOf = [];

    private int _position = -1;
    private int _lastPlayed = -1;

    public ShuffleQueue(Random? rng = null) => _rng = rng ?? Random.Shared;

    public bool Shuffle { get; set; } = true;

    /// <summary>How many recent tracks to keep out of the front half of a new shuffle.</summary>
    public int NoRepeatWindow { get; set; } = 25;

    public int Count => _tracks.Count;

    public TrackInfo? Current => _position >= 0 && _position < _order.Length ? _tracks[_order[_position]] : null;

    /// <summary>
    /// Replaces the track set, keeping the current track playing if it is still there.
    /// Called after a rescan, so it must not restart the music the user is listening to.
    /// </summary>
    public void Rebuild(IReadOnlyList<TrackInfo> tracks)
    {
        // A rescan that found exactly the same files must not disturb the play order.
        // Without this, every scan reshuffles: startup alone rebuilds twice, and a
        // FileSystemWatcher event for an unrelated file would reorder the queue.
        if (SameTrackSet(tracks))
        {
            return;
        }

        string? currentPath = Current?.Path;

        _tracks.Clear();
        _tracks.AddRange(tracks);
        _history.Clear();
        _lastPlayed = -1;

        BuildOrder();

        if (currentPath is not null)
        {
            int index = _tracks.FindIndex(t => string.Equals(t.Path, currentPath, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                // Move the playing track to the front of the new order rather than seeking
                // to wherever the shuffle happened to put it. If it landed near the end,
                // seeking there would cut the cycle short and skip most of the library.
                MoveToFront(index);
                _position = 0;
                _lastPlayed = index;
                return;
            }
        }

        _position = _order.Length > 0 ? 0 : -1;
    }

    private bool SameTrackSet(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count != _tracks.Count)
        {
            return false;
        }

        for (int i = 0; i < tracks.Count; i++)
        {
            if (!string.Equals(tracks[i].Path, _tracks[i].Path, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void MoveToFront(int trackIndex)
    {
        int position = _positionOf[trackIndex];
        if (position == 0)
        {
            return;
        }

        (_order[0], _order[position]) = (_order[position], _order[0]);
        _positionOf[_order[0]] = 0;
        _positionOf[_order[position]] = position;
    }

    /// <summary>Advances one track, reshuffling at the end of a cycle. Null only if the library is empty.</summary>
    public TrackInfo? Next()
    {
        if (_order.Length == 0)
        {
            return null;
        }

        if (_position >= 0)
        {
            _history.Push(_order[_position]);
            if (_history.Count > 100)
            {
                // Stack has no bounded variant; rebuilding from the newest 100 is cheap
                // and happens once every hundred skips.
                int[] keep = _history.Take(100).ToArray();
                _history.Clear();
                for (int i = keep.Length - 1; i >= 0; i--)
                {
                    _history.Push(keep[i]);
                }
            }

            MarkPlayed(_order[_position]);
        }

        _position++;
        if (_position >= _order.Length)
        {
            BuildOrder();
            _position = 0;
        }

        return Current;
    }

    /// <summary>Steps back through what was actually played. Returns null only if the library is empty.</summary>
    public TrackInfo? Previous()
    {
        if (_order.Length == 0)
        {
            return null;
        }

        if (_history.Count == 0)
        {
            return Current;
        }

        int index = _history.Pop();
        _position = _positionOf[index];
        return Current;
    }

    public bool JumpTo(TrackInfo track)
    {
        int index = _tracks.FindIndex(t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        if (_position >= 0)
        {
            _history.Push(_order[_position]);
        }

        _position = _positionOf[index];
        return true;
    }

    public IReadOnlyList<TrackInfo> Upcoming(int count)
    {
        if (_order.Length == 0 || count <= 0)
        {
            return [];
        }

        var result = new List<TrackInfo>(Math.Min(count, _order.Length));
        for (int i = 1; i <= count && i <= _order.Length; i++)
        {
            result.Add(_tracks[_order[(_position + i) % _order.Length]]);
        }

        return result;
    }

    private void MarkPlayed(int index)
    {
        _lastPlayed = index;

        // Half the library at most: on a 10-track library a 25-deep window would make
        // every track "recent" and the anti-repeat pass would have nothing to work with.
        int window = Math.Min(NoRepeatWindow, Math.Max(0, _tracks.Count / 2));
        if (window == 0)
        {
            _recent.Clear();
            return;
        }

        _recent.Enqueue(_tracks[index].Path);
        while (_recent.Count > window)
        {
            _recent.Dequeue();
        }
    }

    private void BuildOrder()
    {
        int n = _tracks.Count;
        _order = new int[n];
        _positionOf = new int[n];

        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
        }

        if (Shuffle && n > 1)
        {
            for (int i = n - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            AvoidImmediateRepeat(n);
            PushRecentTracksBack(n);
        }
        else if (!Shuffle)
        {
            Array.Sort(_order, (a, b) => string.Compare(_tracks[a].Path, _tracks[b].Path, StringComparison.OrdinalIgnoreCase));
        }

        for (int i = 0; i < n; i++)
        {
            _positionOf[_order[i]] = i;
        }
    }

    private void AvoidImmediateRepeat(int n)
    {
        if (_lastPlayed < 0 || _order[0] != _lastPlayed || n < 2)
        {
            return;
        }

        int swapWith = 1 + _rng.Next(n - 1);
        (_order[0], _order[swapWith]) = (_order[swapWith], _order[0]);
    }

    private void PushRecentTracksBack(int n)
    {
        if (_recent.Count == 0 || n < 4)
        {
            return;
        }

        var recent = new HashSet<string>(_recent, StringComparer.OrdinalIgnoreCase);
        int half = n / 2;

        // Walk the front half; whenever it holds a recently played track, trade it for a
        // fresh one from the back half. Bounded by n, and it only runs once per cycle.
        int backCursor = half;
        for (int i = 0; i < half; i++)
        {
            if (!recent.Contains(_tracks[_order[i]].Path))
            {
                continue;
            }

            while (backCursor < n && recent.Contains(_tracks[_order[backCursor]].Path))
            {
                backCursor++;
            }

            if (backCursor >= n)
            {
                return; // Everything is "recent" — nothing left to trade with.
            }

            (_order[i], _order[backCursor]) = (_order[backCursor], _order[i]);
            backCursor++;
        }
    }
}
