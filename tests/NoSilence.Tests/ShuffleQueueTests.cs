using NoSilence.Playback;

namespace NoSilence.Tests;

public class ShuffleQueueTests
{
    private static TrackInfo Track(string name) => new($@"C:\music\{name}.mp3", 1024, DateTime.UnixEpoch);

    private static List<TrackInfo> Library(int count) =>
        Enumerable.Range(0, count).Select(i => Track($"track{i:D3}")).ToList();

    /// <summary>A seeded Random keeps these tests deterministic despite the shuffle.</summary>
    private static ShuffleQueue Build(int trackCount, int seed = 12345)
    {
        var queue = new ShuffleQueue(new Random(seed));
        queue.Rebuild(Library(trackCount));
        return queue;
    }

    [Fact]
    public void EmptyLibrary_DoesNotLoopOrThrow()
    {
        var queue = new ShuffleQueue(new Random(1));
        queue.Rebuild([]);

        Assert.Null(queue.Current);
        Assert.Null(queue.Next());
        Assert.Null(queue.Previous());
        Assert.Empty(queue.Upcoming(5));
    }

    [Fact]
    public void SingleTrack_KeepsReturningIt()
    {
        ShuffleQueue queue = Build(1);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(@"C:\music\track000.mp3", queue.Next()!.Path);
        }
    }

    [Fact]
    public void EveryTrackPlaysExactlyOncePerCycle()
    {
        const int Count = 40;
        ShuffleQueue queue = Build(Count);

        var seen = new List<string> { queue.Current!.Path };
        for (int i = 1; i < Count; i++)
        {
            seen.Add(queue.Next()!.Path);
        }

        Assert.Equal(Count, seen.Distinct().Count());
    }

    /// <summary>
    /// The classic shuffle complaint: the cycle rolls over and the very same track plays
    /// again. It must never happen, whatever the seed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(4242)]
    public void CycleBoundary_NeverRepeatsTheTrackThatJustPlayed(int seed)
    {
        const int Count = 12;
        var queue = new ShuffleQueue(new Random(seed));
        queue.Rebuild(Library(Count));

        // Two full cycles: every boundary crossing has to hold.
        string previous = queue.Current!.Path;
        for (int i = 0; i < Count * 2; i++)
        {
            string next = queue.Next()!.Path;
            Assert.NotEqual(previous, next);
            previous = next;
        }
    }

    [Fact]
    public void PreviousAfterNext_ReturnsTheTrackYouCameFrom()
    {
        ShuffleQueue queue = Build(20);

        string first = queue.Current!.Path;
        string second = queue.Next()!.Path;
        Assert.NotEqual(first, second);

        Assert.Equal(first, queue.Previous()!.Path);
        Assert.Equal(second, queue.Next()!.Path);
    }

    [Fact]
    public void PreviousWithNoHistory_StaysPut()
    {
        ShuffleQueue queue = Build(10);
        string current = queue.Current!.Path;

        Assert.Equal(current, queue.Previous()!.Path);
        Assert.Equal(current, queue.Current!.Path);
    }

    /// <summary>
    /// A rescan must not interrupt what is playing — the user would hear their track
    /// restart every time a file landed in the folder.
    /// </summary>
    [Fact]
    public void Rebuild_KeepsThePlayingTrackWhenItStillExists()
    {
        ShuffleQueue queue = Build(30);
        queue.Next();
        string playing = queue.Current!.Path;

        List<TrackInfo> grown = Library(30);
        grown.Add(Track("newly-added"));
        queue.Rebuild(grown);

        Assert.Equal(playing, queue.Current!.Path);
    }

    /// <summary>
    /// Rescans fire on any file event anywhere in a watched folder. If an unchanged set
    /// reshuffled, the play order would jump around for reasons the user cannot see.
    /// </summary>
    [Fact]
    public void Rebuild_WithAnIdenticalTrackSet_LeavesTheOrderAlone()
    {
        ShuffleQueue queue = Build(30);
        queue.Next();
        queue.Next();

        string current = queue.Current!.Path;
        List<string> upcoming = queue.Upcoming(10).Select(t => t.Path).ToList();

        queue.Rebuild(Library(30));

        Assert.Equal(current, queue.Current!.Path);
        Assert.Equal(upcoming, queue.Upcoming(10).Select(t => t.Path));
    }

    /// <summary>
    /// Regression: a rebuild used to seek to wherever the reshuffle put the playing track.
    /// Landing near the end cut the cycle short and skipped most of the library — with a
    /// three-track folder it played one track and immediately reshuffled.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(10)]
    public void Rebuild_StillPlaysEveryOtherTrackBeforeReshuffling(int count)
    {
        for (int seed = 0; seed < 40; seed++)
        {
            var queue = new ShuffleQueue(new Random(seed));
            queue.Rebuild(Library(count));

            // A changed set (one extra file appeared) forces a real rebuild mid-cycle.
            List<TrackInfo> grown = Library(count);
            grown.Add(Track("added"));
            queue.Rebuild(grown);

            var played = new List<string> { queue.Current!.Path };
            for (int i = 1; i <= count; i++)
            {
                played.Add(queue.Next()!.Path);
            }

            Assert.Equal(count + 1, played.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void Rebuild_FallsBackGracefullyWhenThePlayingTrackIsGone()
    {
        ShuffleQueue queue = Build(30);
        queue.Next();

        queue.Rebuild(Library(30).Take(5).ToList());

        Assert.NotNull(queue.Current);
        Assert.Equal(5, queue.Count);
    }

    [Fact]
    public void SequentialMode_PlaysInPathOrder()
    {
        var queue = new ShuffleQueue(new Random(7)) { Shuffle = false };
        queue.Rebuild(Library(5));

        var order = new List<string> { queue.Current!.Path };
        for (int i = 0; i < 4; i++)
        {
            order.Add(queue.Next()!.Path);
        }

        Assert.Equal(order.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), order);
    }

    [Fact]
    public void JumpTo_MovesToThatTrackAndPreviousComesBack()
    {
        ShuffleQueue queue = Build(20);
        string start = queue.Current!.Path;
        TrackInfo target = Track("track017");

        Assert.True(queue.JumpTo(target));
        Assert.Equal(target.Path, queue.Current!.Path);
        Assert.Equal(start, queue.Previous()!.Path);
    }

    [Fact]
    public void JumpTo_UnknownTrackIsRejected()
    {
        ShuffleQueue queue = Build(5);
        Assert.False(queue.JumpTo(Track("not-in-the-library")));
    }

    [Fact]
    public void Upcoming_LooksAheadWithoutAdvancing()
    {
        ShuffleQueue queue = Build(20);
        string current = queue.Current!.Path;

        IReadOnlyList<TrackInfo> upcoming = queue.Upcoming(3);

        Assert.Equal(3, upcoming.Count);
        Assert.Equal(current, queue.Current!.Path);
        Assert.Equal(upcoming[0].Path, queue.Next()!.Path);
    }

    /// <summary>
    /// Recently played tracks should be pushed towards the back of the next cycle. Measured
    /// statistically over many seeds because a single shuffle proves nothing.
    /// </summary>
    [Fact]
    public void RecentlyPlayedTracks_TendTowardsTheBackOfTheNextCycle()
    {
        const int Count = 40;
        const int Trials = 60;
        int frontHalfHits = 0;

        for (int seed = 0; seed < Trials; seed++)
        {
            var queue = new ShuffleQueue(new Random(seed)) { NoRepeatWindow = 10 };
            queue.Rebuild(Library(Count));

            // Play a whole cycle so the last ten are "recent", then cross the boundary.
            var lastTen = new List<string>();
            for (int i = 0; i < Count; i++)
            {
                lastTen.Add(queue.Current!.Path);
                queue.Next();
                if (lastTen.Count > 10)
                {
                    lastTen.RemoveAt(0);
                }
            }

            var recent = lastTen.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextTen = new List<string> { queue.Current!.Path };
            for (int i = 0; i < 9; i++)
            {
                nextTen.Add(queue.Next()!.Path);
            }

            frontHalfHits += nextTen.Count(recent.Contains);
        }

        // Pure chance would put roughly a quarter of the recent ten in the next ten
        // (10 recent * 10 slots / 40 tracks = 2.5 per trial, so ~150 across 60 trials).
        Assert.True(frontHalfHits < 60, $"expected recent tracks to be pushed back, but {frontHalfHits} landed in the next ten across {Trials} trials");
    }
}
