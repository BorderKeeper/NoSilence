namespace NoSilence.Detection;

/// <summary>
/// Picks the rule that applies to a session and folds the global defaults into it.
/// Pure and first-match-wins, so the order the user sees in the UI is the order that runs.
/// </summary>
internal static class RuleMatcher
{
    public static ResolvedRule Resolve(SessionObservation session, DetectionConfig config)
    {
        foreach (ProcessRule rule in config.Rules)
        {
            if (!rule.Enabled || !Matches(rule, session))
            {
                continue;
            }

            return Fold(rule, config);
        }

        return new ResolvedRule(RuleMode.Trigger, config.ThresholdDb, config.MinDurationMs, "default");
    }

    private static bool Matches(ProcessRule rule, SessionObservation session) => rule.MatchKind switch
    {
        RuleMatchKind.SystemSounds => session.IsSystemSounds,
        RuleMatchKind.ExeName => GlobMatch(rule.Match, session.ExeName),
        RuleMatchKind.ExePathContains => session.SessionInstanceId.Contains(rule.Match, StringComparison.OrdinalIgnoreCase),
        RuleMatchKind.DisplayNameContains => session.DisplayName is { } name
            && name.Contains(rule.Match, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static ResolvedRule Fold(ProcessRule rule, DetectionConfig config)
    {
        double threshold = rule.ThresholdDb ?? config.ThresholdDb;

        int duration = rule.Mode switch
        {
            // Ignore never fires, so its duration is irrelevant, but a sentinel keeps any
            // caller that forgets to check Ignored from accidentally triggering.
            RuleMode.Ignore => int.MaxValue,
            RuleMode.AlwaysTrigger => 0,
            RuleMode.Tolerant => rule.MinDurationMs ?? config.TolerantMinDurationMs,
            _ => rule.MinDurationMs ?? config.MinDurationMs,
        };

        return new ResolvedRule(rule.Mode == RuleMode.Default ? RuleMode.Trigger : rule.Mode, threshold, duration, rule.Match);
    }

    /// <summary>Case-insensitive match supporting <c>*</c> anywhere, e.g. <c>voicemeeter*.exe</c>.</summary>
    public static bool GlobMatch(string pattern, string value)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        return IsMatch(pattern.AsSpan(), value.AsSpan());
    }

    private static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        // Iterative wildcard match: linear, no allocation, no regex, no catastrophic
        // backtracking on a pattern the user typed.
        int p = 0, v = 0, star = -1, mark = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(value[v])))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
