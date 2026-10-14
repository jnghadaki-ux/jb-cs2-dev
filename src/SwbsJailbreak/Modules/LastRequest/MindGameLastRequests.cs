using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using SwbsJailbreak.Core;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace SwbsJailbreak.Modules.LastRequest;

internal enum MindKind
{
    None,
    RockPaperScissors,
    MathRace,
    TypeRace,
    SimonSays
}

internal sealed class MindState
{
    public MindKind Kind;
    public bool Active, Started;
    public int OpponentSlot = -1;
    public Timer? Timer, Pause;
    public int Round, ScoreP, ScoreO;

    // rock paper scissors
    public LrLogic.Rps? PickP, PickO;

    // math / type
    public string Question = "";
    public int Answer;
    public string Word = "";
    public bool Open;

    // simon says
    public LrLogic.SimonAction Action;
    public LrLogic.SimonAction? LastAction;
    public float WindowEnd;
    public readonly bool[] Jumped = new bool[2], Moved = new bool[2];
    public readonly CounterStrikeSharp.API.Modules.Utils.Vector?[] Start = new CounterStrikeSharp.API.Modules.Utils.Vector?[2];
}

/// <summary>
/// 0.15 mind games (category "Mind Games"): Rock Paper Scissors (best of 3 from a menu), Math Race and Type Race (first
/// to answer in chat wins the point, first to 2 points wins; the contestants' answers are hidden from chat) and Simon
/// Says (do the action shown before the window closes - jump, crouch, look up, look down or freeze; the window shrinks
/// each round; the first to fail loses). Losers are slain with the kill credited to the winner.
/// </summary>
public sealed class MindGameLastRequests : JbModule
{
    private LrExtraConfig Cfg => Section<LrExtraConfig>("LastRequestsExtra");

    public override string Name => "LastRequests.MindGames";
    public override int Order => 10;

    private const int PointsToWin = 2;
    private const int MaxQuestions = 6;
    private const int MaxRpsRounds = 7;
    private const int MaxSimonRounds = 20;

    private static MindState S(JbPlayer p) => p.Data<MindState>();

    public override void OnLoad(bool hotReload)
    {
        if (Jb.LastRequests is not LastRequestModule) return;
        if (Cfg.RockPaperScissorsEnabled) Register("Rock Paper Scissors", MindKind.RockPaperScissors, "Best of 3; the loser dies.");
        if (Cfg.MathRaceEnabled) Register("Math Race", MindKind.MathRace, "Answer the sum in chat first; first to 2 points wins.");
        if (Cfg.TypeRaceEnabled) Register("Type Race", MindKind.TypeRace, "Type the word in chat first; first to 2 points wins.");
        if (Cfg.SimonSaysEnabled) Register("Simon Says", MindKind.SimonSays, "Do what the screen says in time; first to fail loses.");
        Plugin.AddCommandListener("say", OnSay, HookMode.Pre);
        Plugin.AddCommandListener("say_team", OnSay, HookMode.Pre);
        Plugin.RegisterEventHandler<EventPlayerJump>(OnJump);
        Plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    private void Register(string name, MindKind kind, string description) =>
        Jb.LastRequests.Register(new LastRequestDefinition
        {
            Name = name, Category = "Mind Games", Flags = LrFlags.DontAllowDamagingOpponent, Description = description,
            OnStart = p =>
            {
                var s = S(p);
                Reset(s);
                s.Kind = kind;
                s.Active = true;
                Jb.LastRequests.DisplayOpponentSelection(p, OnOpponent);
            },
            OnEnd = OnEnd
        });

    private static void Reset(MindState s)
    {
        LrKit.Kill(ref s.Timer);
        LrKit.Kill(ref s.Pause);
        s.Kind = MindKind.None;
        s.Active = s.Started = s.Open = false;
        s.OpponentSlot = -1;
        s.Round = s.ScoreP = s.ScoreO = 0;
        s.PickP = s.PickO = null;
        s.LastAction = null;
    }

    private void OnOpponent(JbPlayer p, JbPlayer o)
    {
        var s = S(p);
        if (!s.Active) return;
        s.OpponentSlot = o.Slot;
        s.Started = true;
        switch (s.Kind)
        {
            case MindKind.RockPaperScissors: RpsRound(p); break;
            case MindKind.MathRace:
            case MindKind.TypeRace:
                LrKit.TellBoth(p, o, s.Kind == MindKind.MathRace ? "lr.math.how" : "lr.type.how", PointsToWin);
                NextQuestion(p);
                break;
            case MindKind.SimonSays:
                LrKit.TellBoth(p, o, "lr.simon.how");
                SimonPause(p);
                break;
        }
    }

    private static void OnEnd(JbPlayer p, JbPlayer? o)
    {
        var s = S(p);
        if (!s.Active) return;
        Reset(s);
        foreach (var x in new[] { p, o })
        {
            if (x?.Controller is not { IsValid: true } c) continue;
            Hud.Clear(c, "lr.mind");
            if (MenuService.Current(c) is { } m && m.Title.StartsWith(Lang.T("lr.rps.title_prefix"), StringComparison.Ordinal)) MenuService.Close(c, false);
        }
    }

    private static JbPlayer Opp(JbPlayer p) => Players.Get(S(p).OpponentSlot);

    private static JbPlayer? Running(JbPlayer x, MindKind kind)
    {
        var owner = LrKit.Owner(x);
        if (owner == null) return null;
        var s = S(owner);
        return s.Active && s.Started && s.Kind == kind && s.OpponentSlot >= 0 ? owner : null;
    }

    /// <summary>Point to the prisoner (side 0) or the guard (side 1); ends the LR when someone reaches PointsToWin or the round cap.</summary>
    private bool Point(JbPlayer owner, int side, int maxRounds)
    {
        var s = S(owner);
        if (side == 0) s.ScoreP++;
        else if (side == 1) s.ScoreO++;
        var o = Opp(owner);
        var w = LrLogic.MatchWinner(s.ScoreP, s.ScoreO, PointsToWin);
        if (w < 0 && s.Round >= maxRounds) w = LrLogic.FinalWinner(s.ScoreP, s.ScoreO, Random.Shared.Next(2));
        if (w < 0)
        {
            LrKit.TellBoth(owner, o, "lr.mind.score", owner.Name, s.ScoreP, o.Name, s.ScoreO);
            return false;
        }

        var winner = w == 0 ? owner : o;
        var loser = w == 0 ? o : owner;
        Chat.TellAll("lr.mind.won", winner.Name, Math.Max(s.ScoreP, s.ScoreO), Math.Min(s.ScoreP, s.ScoreO), loser.Name);
        s.Open = false;
        LrKit.Win(winner, loser);
        return true;
    }

    // ====================================================================== Rock Paper Scissors

    private void RpsRound(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active) return;
        s.Round++;
        s.PickP = s.PickO = null;
        s.Open = true;
        foreach (var x in new[] { owner, Opp(owner) })
        {
            var c = x.Controller;
            if (c == null) continue;
            if (c.IsBot)
            {
                SetPick(owner, x.Slot, (LrLogic.Rps)Random.Shared.Next(3));
                continue;
            }

            var m = new JbMenu(Lang.T("lr.rps.title_prefix") + " " + Lang.T("lr.rps.title", s.Round)) { ExitButton = false };
            foreach (var pick in new[] { LrLogic.Rps.Rock, LrLogic.Rps.Paper, LrLogic.Rps.Scissors })
            {
                var choice = pick;
                m.Add(Lang.T("lr.rps." + choice.ToString().ToLowerInvariant()), pc => SetPick(owner, pc.Slot, choice));
            }

            m.Open(c);
        }

        var slot = owner.Slot;
        LrKit.Kill(ref s.Timer);
        s.Timer = Round.Timer(12f, () => RpsResolve(Players.Get(slot)));
    }

    private void SetPick(JbPlayer owner, int slot, LrLogic.Rps pick)
    {
        var s = S(owner);
        if (!s.Active || !s.Open || s.Kind != MindKind.RockPaperScissors) return;
        if (slot == owner.Slot) s.PickP ??= pick;
        else if (slot == s.OpponentSlot) s.PickO ??= pick;
        if (s.PickP != null && s.PickO != null) Server.NextFrame(() => RpsResolve(owner));
    }

    private void RpsResolve(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active || !s.Open || s.Kind != MindKind.RockPaperScissors) return;
        s.Open = false;
        LrKit.Kill(ref s.Timer);
        var o = Opp(owner);
        foreach (var x in new[] { owner, o })
            if (x.Controller is { } c && MenuService.Current(c) is { } m && m.Title.StartsWith(Lang.T("lr.rps.title_prefix"), StringComparison.Ordinal))
                MenuService.Close(c, false);
        string Name(LrLogic.Rps? r) => r == null ? Lang.T("lr.rps.nothing") : Lang.T("lr.rps." + r.Value.ToString().ToLowerInvariant());
        Chat.TellAll("lr.rps.reveal", owner.Name, Name(s.PickP), o.Name, Name(s.PickO));
        var r = LrLogic.RpsRound(s.PickP, s.PickO);
        if (Point(owner, r > 0 ? 0 : r < 0 ? 1 : -1, MaxRpsRounds)) return;
        var slot = owner.Slot;
        s.Pause = Round.Timer(2f, () => RpsRound(Players.Get(slot)));
    }

    // ====================================================================== Math Race / Type Race

    private void NextQuestion(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active) return;
        s.Round++;
        var o = Opp(owner);
        string text;
        if (s.Kind == MindKind.MathRace)
        {
            (s.Question, s.Answer) = LrLogic.MathQuestion(Random.Shared);
            text = s.Question + " = ?";
        }
        else
        {
            var words = Cfg.TypeRaceWords.Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
            s.Word = words.Count == 0 ? "jailbreak" : words[Random.Shared.Next(words.Count)].Trim();
            text = s.Word;
        }

        s.Open = true;
        LrKit.HudBoth(owner, o, "lr.mind", Lang.T("lr.mind.question", s.Round, Hud.Escape(text)), 45, 20f);
        LrKit.TellBoth(owner, o, "lr.mind.question_chat", s.Round, text);
        var slot = owner.Slot;
        var round = s.Round;
        LrKit.Kill(ref s.Timer);
        s.Timer = Round.Timer(20f, () =>
        {
            var pl = Players.Get(slot);
            var st = S(pl);
            if (!st.Active || !st.Open || st.Round != round) return;
            st.Open = false;
            LrKit.TellBoth(pl, Opp(pl), "lr.mind.nobody", st.Kind == MindKind.MathRace ? st.Answer.ToString() : st.Word);
            if (Point(pl, -1, MaxQuestions)) return;
            st.Pause = Round.Timer(2f, () => NextQuestion(Players.Get(slot)));
        });
    }

    private HookResult OnSay(CCSPlayerController? c, CommandInfo info)
    {
        if (c == null || !c.IsValid || c.Jb() is not { } x) return HookResult.Continue;
        var owner = Running(x, MindKind.MathRace) ?? Running(x, MindKind.TypeRace);
        if (owner == null) return HookResult.Continue;
        var s = S(owner);
        var text = info.GetArg(1).Trim();
        if (text.Length == 0 || text[0] is '!' or '/') return HookResult.Continue;
        if (!s.Open) return HookResult.Continue;
        var right = s.Kind == MindKind.MathRace ? LrLogic.IsAnswer(text, s.Answer) : LrLogic.TypedMatch(text, s.Word);
        if (!right)
        {
            Chat.Tell(c, "lr.mind.wrong");
            return HookResult.Handled; // a wrong answer is not shown to the other player
        }

        s.Open = false;
        LrKit.Kill(ref s.Timer);
        Chat.TellAll("lr.mind.right", x.Name, s.Kind == MindKind.MathRace ? s.Answer.ToString() : s.Word);
        var slot = owner.Slot;
        Server.NextFrame(() =>
        {
            var pl = Players.Get(slot);
            if (!S(pl).Active) return;
            if (Point(pl, x.Slot == pl.Slot ? 0 : 1, MaxQuestions)) return;
            S(pl).Pause = Round.Timer(2f, () => NextQuestion(Players.Get(slot)));
        });
        return HookResult.Handled;
    }

    // ====================================================================== Simon Says

    private void SimonPause(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active) return;
        s.Open = false;
        LrKit.HudBoth(owner, Opp(owner), "lr.mind", Lang.T("lr.simon.ready", s.Round + 1), 45, 1.6f);
        var slot = owner.Slot;
        s.Pause = Round.Timer(1.5f, () => SimonRound(Players.Get(slot)));
    }

    private void SimonRound(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active) return;
        s.Round++;
        var actions = Enum.GetValues<LrLogic.SimonAction>().Where(a => a != s.LastAction).ToArray();
        s.Action = actions[Random.Shared.Next(actions.Length)];
        s.LastAction = s.Action;
        var window = LrLogic.SimonWindow(s.Round);
        s.WindowEnd = Server.CurrentTime + window;
        var both = new[] { owner, Opp(owner) };
        for (var i = 0; i < 2; i++)
        {
            s.Jumped[i] = s.Moved[i] = false;
            var o = both[i].Controller?.PawnOrNull()?.AbsOrigin;
            s.Start[i] = o == null ? null : new CounterStrikeSharp.API.Modules.Utils.Vector(o.X, o.Y, o.Z);
        }

        s.Open = true;
        LrKit.HudBoth(owner, both[1], "lr.mind", Lang.T("lr.simon.do", Lang.T("lr.simon." + s.Action.ToString().ToLowerInvariant()), window.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)), 60, window);
        var slot = owner.Slot;
        LrKit.Kill(ref s.Timer);
        s.Timer = Round.Timer(window, () => SimonJudge(Players.Get(slot)));
    }

    private HookResult OnJump(EventPlayerJump ev, GameEventInfo info)
    {
        var c = ev.Userid;
        if (c == null || c.Jb() is not { } x) return HookResult.Continue;
        var owner = Running(x, MindKind.SimonSays);
        if (owner == null || !S(owner).Open) return HookResult.Continue;
        S(owner).Jumped[x.Slot == owner.Slot ? 0 : 1] = true;
        return HookResult.Continue;
    }

    private void OnTick()
    {
        foreach (var p in Players.Connected())
        {
            var s = S(p);
            if (!s.Active || !s.Open || s.Kind != MindKind.SimonSays || s.OpponentSlot < 0) continue;
            var both = new[] { p, Opp(p) };
            for (var i = 0; i < 2; i++)
            {
                var c = both[i].Controller;
                var o = c?.PawnOrNull()?.AbsOrigin;
                if (c == null || o == null || s.Start[i] == null) continue;
                if (!c.OnGround()) s.Jumped[i] = true;
                if (VectorUtil.Distance2D(o, s.Start[i]!) > 12f) s.Moved[i] = true;
            }
        }
    }

    private void SimonJudge(JbPlayer owner)
    {
        var s = S(owner);
        if (!s.Active || !s.Open) return;
        s.Open = false;
        var both = new[] { owner, Opp(owner) };
        var passed = new bool[2];
        for (var i = 0; i < 2; i++)
        {
            var c = both[i].Controller;
            var pawn = c?.PawnOrNull();
            if (c == null || pawn == null) continue;
            var crouching = (c.Buttons & PlayerButtons.Duck) != 0 || (pawn.Flags & (uint)PlayerFlags.FL_DUCKING) != 0;
            passed[i] = LrLogic.SimonPassed(s.Action, s.Jumped[i], crouching, pawn.EyeAngles.X, s.Moved[i]);
        }

        if (passed[0] != passed[1])
        {
            var loser = passed[0] ? both[1] : both[0];
            var winner = passed[0] ? both[0] : both[1];
            Chat.TellAll("lr.simon.failed", loser.Name, Lang.T("lr.simon." + s.Action.ToString().ToLowerInvariant()), s.Round);
            LrKit.Win(winner, loser);
            return;
        }

        if (s.Round >= MaxSimonRounds)
        {
            var w = Random.Shared.Next(2);
            Chat.TellAll("lr.simon.coin");
            LrKit.Win(both[w], both[1 - w]);
            return;
        }

        LrKit.HudBoth(owner, both[1], "lr.mind", Lang.T(passed[0] ? "lr.simon.both_ok" : "lr.simon.both_failed"), 45, 1.5f);
        SimonPause(owner);
    }
}
