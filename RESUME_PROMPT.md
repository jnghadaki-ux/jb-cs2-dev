# Resume prompt (paste into any Claude session or platform)

```
Continue the SWBS Ultimate Jailbreak CS2 project in GitHub repo jnghadaki-ux/jb-cs2-dev, branch
claude/swbs-jailbreak-cs2. Read CLAUDE.md (my standing rules + Master Prompt v2), then SESSION_HANDOFF.md and
PROJECT_STATUS.md, and continue with the first open item in SESSION_HANDOFF.md "Exact next actions".

Always:
- .NET 8 ONLY (net8.0, CounterStrikeSharp.API 1.0.368). Never work on a .NET 10 version.
- Credits: Gdaki = creator (all work before upload is mine); later work "enhanced with Claude Code";
  inspired by HlStriker, Swbs development/lead admins.
- Real CS2 effects only (no ASCII/text stand-ins, no invented hit points). Keep SMPlugins commands/behaviour.
- Never claim builds/tests/server behaviour you did not run. Build + test after each change.
- If the task needs a different model than the one running, pause and tell me which one and why before starting.
- Max 2 sub-agents. Commit + push near the end of the session and after ~25 min of work; update SESSION_HANDOFF.md.
- Usage right now: 5-hour window __%, weekly __%. At >=90% (5-hour) stop new work, push and hand off;
  at >=90% weekly do only the single most valuable task, then push.

Task for this session (optional): ____
```

Without repository access (e.g. a chat app with no GitHub), upload `CLAUDE.md`, `SESSION_HANDOFF.md`,
`PROJECT_STATUS.md` and the files for the task, and say "no git access: return changed files in full".
