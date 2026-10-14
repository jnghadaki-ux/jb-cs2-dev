namespace SwbsJailbreak.Core;

/// <summary>What the spam guard decided about one chat line.</summary>
public readonly record struct SpamVerdict(bool Blocked, string? WarnKey, bool Gag);

/// <summary>
/// Engine-free rules of SMPlugins NoTextSpam.sp v1.2 (unit-tested), per player slot:
/// a characters-per-second budget (15, reduced by 2 for every message sent within 3 s of the last; a line longer than
/// the time since the last message allows is blocked), a gag after 3 messages at a zero budget, and a repeat of the
/// same text within clamp(length, 5, 50) seconds dropped silently.
/// The verdict of the latest line is kept so other chat listeners (crew chat tags) can honour it.
/// </summary>
public sealed class SpamGuard
{
    public int MaxCharactersPerSecond { get; init; } = 15;
    public int ReduceCharactersPerSecond { get; init; } = 2;
    public double ResetSeconds { get; init; } = 3.0;
    public int MaxMessagesAtZero { get; init; } = 3;
    public int RepeatDelayMin { get; init; } = 5;
    public int RepeatDelayMax { get; init; } = 50;

    private sealed class State
    {
        public int Cps;
        public double LastMessage = -9999;
        public int AtZero;
        public readonly Dictionary<string, double> LastUsed = new(StringComparer.Ordinal);
        public string LastText = "";
        public double LastTextTime = -1;
        public SpamVerdict LastVerdict;
    }

    private readonly Dictionary<int, State> _states = new();

    public SpamVerdict Check(int slot, string text, double now)
    {
        if (!_states.TryGetValue(slot, out var s)) _states[slot] = s = new State { Cps = MaxCharactersPerSecond };

        // Several listeners see the same line: answer them all with the first verdict.
        if (s.LastText == text && Math.Abs(s.LastTextTime - now) < 0.001) return s.LastVerdict;

        var verdict = Evaluate(s, text, now);
        s.LastText = text;
        s.LastTextTime = now;
        s.LastVerdict = verdict;
        return verdict;
    }

    private SpamVerdict Evaluate(State s, string text, double now)
    {
        string? warn = null;
        var gag = false;
        var length = text.Length;

        if (s.LastMessage + ResetSeconds > now)
        {
            var allowed = (int)Math.Round((now - s.LastMessage) * s.Cps);
            if (s.Cps <= 0)
            {
                s.AtZero++;
                if (s.AtZero >= MaxMessagesAtZero) gag = true;
                else warn = "spam.gag_warning";
            }

            s.Cps -= ReduceCharactersPerSecond;
            if (allowed < length)
            {
                s.LastMessage = now;
                return new SpamVerdict(true, gag ? null : warn ?? "spam.slow_down", gag);
            }
        }
        else
        {
            s.Cps = MaxCharactersPerSecond;
            s.AtZero = 0;
        }

        s.LastMessage = now;

        if (s.LastUsed.TryGetValue(text, out var lastUsed))
        {
            var delay = Math.Clamp(length, RepeatDelayMin, RepeatDelayMax);
            if (lastUsed + delay > now) return new SpamVerdict(true, warn, gag);
        }

        s.LastUsed[text] = now;
        return new SpamVerdict(false, warn, gag);
    }

    /// <summary>The verdict for this exact line if it was checked at this moment (for other chat listeners).</summary>
    public bool WasBlocked(int slot, string text, double now) =>
        _states.TryGetValue(slot, out var s) && s.LastText == text && Math.Abs(s.LastTextTime - now) < 0.001 && s.LastVerdict.Blocked;

    public void Forget(int slot) => _states.Remove(slot);

    public void Clear() => _states.Clear();
}
