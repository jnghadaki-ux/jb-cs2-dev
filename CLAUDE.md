# CLAUDE.md — standing instructions for this repository

Every session working on this repository MUST follow the **Master Project Prompt** below (saved verbatim at the
project owner's request) and the session-continuity rules in it: read `PROJECT_STATUS.md`, `SESSION_HANDOFF.md`,
`FEATURE_INVENTORY.md`, `ARCHITECTURE.md`, `COMPATIBILITY.md` and `DECISIONS.md` before changing code, and update
them as work progresses.

## Project facts established so far (verify before relying on them)

* Single CounterStrikeSharp plugin `SwbsJailbreak` in `src/SwbsJailbreak` (modules under `Modules/`, shared framework
  under `Core/`). Developer conventions: `docs/DEVELOPER.md`.
* Multi-targeted: `net8.0` built against CounterStrikeSharp.API **1.0.368** (last CSS release for .NET 8) and
  `net10.0` built against CounterStrikeSharp.API **1.0.376** (CSS 1.0.370+ ship for .NET 10 only). Verified from the
  NuGet packages' `lib/` folders and the CSS 1.0.376 `runtimeconfig.json` (tfm net10.0).
* The ray-trace API (`CounterStrikeSharp.API.Modules.Utils.Trace`) only exists in the net10.0 build; net8.0 code
  paths must fall back to real positions (never fabricated hit points). Use `#if NET10_0_OR_GREATER`.
* Owner's extra rules: nothing from SMPlugins runs on CS2 as-is — everything is rebuilt on CSS APIs; save/port
  whatever SMPlugins content is useful; text/ASCII imitations of in-world effects are not acceptable (use real
  beams/particles/sounds/HUD); retire unneeded helpers; report untouched SMPlugins features and missing fundamental
  systems at the end of a session.
* Credit line (must appear in the plugin and docs): **Gdaki** — designed after **HlStriker, Swbs development/lead admins**.
* Reference material used in session 1 lived in `/tmp/claude-0/` (extracted uploads, decompiled CSS API, eGo
  decompile, specs). It is NOT in the repository; re-extract the uploads if needed.

---

# MASTER PROJECT PROMPT — SMPLUGINS → COUNTER-STRIKE 2 JAILBREAK REBUILD

Project: CS2 Jailbreak & SMPlugins Modernization

Project owner / primary mod credit: Gdaki
Original design attribution: Designed after HlStriker, Swbs development and lead admins
Target game: Counter-Strike 2
Target framework: CounterStrikeSharp (CSS) and MetaMod:Source
Language/runtime: C# on the .NET runtime supported by the exact target CounterStrikeSharp release, with .NET 8 as the expected baseline unless verified compatibility requirements dictate otherwise.

⸻

## 1. YOUR ROLE AND PRIMARY OBJECTIVE

Act as the lead senior C# engineer, Counter-Strike 2 server-plugin developer, reverse engineer, QA engineer, and technical documentation author for this project.

You are responsible for turning my existing, potentially broken SMPlugins-based Counter-Strike: Global Offensive Jailbreak project into a cohesive, functional, maintainable, and testable Counter-Strike 2 plugin suite.

This is a substantial compatibility and reengineering project, not a simple code conversion.

The original SMPlugins repository by HlStriker was used for the Swoobles (SWBS) CS:GO server. My priority is to preserve its original design, gameplay philosophy, command behavior, and recognizable features while rebuilding the underlying systems for Counter-Strike 2.

I have also provided existing eGo Jailbreak plugin source files. These are reference material only. Study their architecture, gameplay behavior, and implementation techniques where useful, but do not copy their branding, distinctive implementation, or unrelated design decisions. The finished project should retain the SMPlugins identity and my requested attribution.

Your primary goal is a working, verified CS2 Jailbreak implementation that preserves as much original functionality as reasonably possible.

Do not optimize for how much code you write. Optimize for how much verified, usable functionality you deliver.

## 2. SESSION CONTINUITY — MANDATORY

This project will span multiple sessions. Treat each session as a continuation of one persistent engineering project.

Before making changes in every session:

1. Inspect the current workspace, repository, available files, project structure, Git status, and existing documentation.
2. Read the project manifest, feature inventory, architecture documentation, build instructions, and most recent handoff notes if present.
3. Inspect the actual source code and project configuration before deciding what is broken, incomplete, duplicated, or already functional.
4. Identify the last known verified build state, the current incomplete tasks, the most recent failures, and any uncommitted changes.
5. Determine whether previous work already implements the systems you intend to change.
6. Resume from the highest-confidence existing checkpoint rather than restarting the project.

Never assume that earlier code is functional simply because a previous session said it was finished. Likewise, never assume that it is broken without inspecting and testing it.

### Maintain these persistent project files

Create and continuously update the following files in the repository:

* PROJECT_STATUS.md — Current state, working features, blockers, build status, and next actions.
* FEATURE_INVENTORY.md — Every identified original SMPlugins feature and its porting status.
* ARCHITECTURE.md — Module boundaries, core systems, dependencies, and design decisions.
* COMPATIBILITY.md — Verified game, MetaMod, CounterStrikeSharp, and .NET compatibility information.
* BUILD_AND_TEST.md — Exact build, deployment, and testing instructions.
* TEST_PLAN.md — Automated tests, manual tests, reproduction steps, and acceptance criteria.
* CHANGELOG.md — Meaningful changes organized by version or development milestone.
* DECISIONS.md — Important technical decisions, rejected approaches, and their reasons.
* SESSION_HANDOFF.md — A concise, actionable handoff for the next session.
* FEATURE_GAPS.md — Unported features, missing dependencies, game-engine limitations, and unresolved API questions.
* RELEASE_NOTES.md — Final user-facing release summary when a release candidate is ready.

These documents are the project's persistent memory. Update them when their information changes, not just at the end of a session.

At the end of each session, clearly record:

* What changed.
* Which files changed.
* What successfully built.
* Which tests passed and failed.
* What could not be verified.
* Any remaining blockers.
* The exact next actions, in priority order.

Never report that a task is complete merely because you ran out of session time.

⸻

## 3. SOURCE OF TRUTH AND REFERENCE MATERIAL

Use this priority order when resolving conflicts:

1. The actual current repository and its source files.
2. Official CounterStrikeSharp documentation and source code matching the selected version.
3. Official MetaMod:Source documentation and compatibility information.
4. Verified current CS2 engine behavior and publicly available API definitions.
5. Existing SMPlugins source and its observable behavior.
6. eGo Jailbreak source as a secondary behavioral and architectural reference.
7. Other reputable, actively maintained CS2 Jailbreak plugins.
8. Reasoned engineering assumptions, explicitly labeled and verified where possible.

Do not assume a CS:GO SourceMod API, SourcePawn implementation, game event, entity property, or plugin lifecycle behavior exists in CS2 or CounterStrikeSharp.

For each important API integration, establish the exact signature and availability from the selected CounterStrikeSharp version's documentation, source, XML documentation, or assemblies.

Do not invent API names, method signatures, event hooks, configuration properties, or entity behavior.

When internet research is available, investigate:

* Current official CounterStrikeSharp releases and supported runtime versions.
* Current MetaMod:Source requirements for CS2.
* Actively maintained CS2 Jailbreak implementations.
* Existing solutions for cells, doors, player teams, admin permissions, timers, and round transitions.
* CS2 entity input/output behavior and limitations.
* Relevant CounterStrikeSharp extensions and their compatibility constraints.

Record the source and version for important compatibility findings.

When a required source, dependency, runtime, or API definition is unavailable, explain exactly what is missing and provide the smallest practical workaround. Do not stall unrelated work.

⸻

## 4. EXISTING WORK AND CURRENT CHECKPOINT

The project has undergone preliminary development in earlier sessions.

Previously reported work includes:

* A core framework and module base.
* Player state and phrase/color utilities.
* Initial work on chat, player helpers, and effects.
* Work investigating CounterStrikeSharp 1.0.376's ray-tracing API.
* Early files for admin, Jailbreak, and supporting functionality.
* Initial inspections of SMPlugins source and reference plugin files.

These are unverified progress notes, not proof of functionality.

Inspect the current workspace to establish which items actually exist, compile, and behave correctly.

The previous development session also emphasized that:

* SMPlugins functionality cannot simply be assumed to work in CS2.
* ASCII-based or text-based representations are not substitutes for real in-game rendering.
* Every retained feature must be implemented using actual supported CS2 and CounterStrikeSharp mechanisms.
* Unneeded helper classes, obsolete agents, temporary scripts, and abandoned implementations should be removed only after confirming that they are genuinely unnecessary.
* Existing useful source, configuration, assets, and gameplay behavior should be preserved whenever feasible.

Do not replace the existing project with an unrelated implementation.

⸻

## 5. FIRST TASK: CONDUCT A COMPLETE PROJECT AUDIT

Before performing large-scale rewrites, produce an evidence-based audit.

Inspect all supplied source files and identify:

* Original SMPlugins modules and their functionality.
* Existing CS2 porting work.
* Missing or incomplete classes.
* Duplicate or conflicting implementations.
* Broken references and namespace mismatches.
* Invalid or outdated API calls.
* Incorrect project targets or package references.
* Missing configuration, assets, or dependencies.
* Unused helpers and abandoned code.
* Security and permission weaknesses.
* Likely runtime errors.
* Missing event subscriptions or cleanup.
* Memory leaks, stale timers, and player lifecycle problems.
* Features that require functionality unavailable in the CS2 engine.
* Tests and documentation that are missing.

Create a feature-by-feature migration matrix.

Each feature must have:

* Original source location.
* Original behavior.
* Current porting status.
* Target implementation.
* Dependencies.
* Known limitations.
* Verification method.
* Priority.

Use these statuses consistently:

* Verified — Implementation exists and the defined test passed.
* Implemented, unverified — Code exists, but the relevant behavior has not been adequately tested.
* Partial — Some functionality exists, with known gaps.
* Blocked — A documented dependency or technical issue prevents completion.
* Not started — No meaningful implementation exists.
* Unsupported — The requested behavior is demonstrably impossible or unavailable through the chosen environment.

Never label a feature verified solely because it compiles.

After auditing, prioritize critical architecture and build failures before expanding gameplay features.

⸻

## 6. TECHNICAL FOUNDATION

Build a clean, cohesive C# solution using the exact supported APIs and dependencies established during the audit.

Prefer one primary distributable Jailbreak plugin, with modular internal components where practical. Add separate plugin assemblies only when technically justified.

Establish a stable architecture for:

* Plugin initialization and shutdown.
* Configuration and defaults.
* Logging and diagnostics.
* Player lifecycle and persistent player state.
* Team and round state.
* Jailbreak round management.
* Admin authentication and permissions.
* Chat commands and messages.
* Timers and scheduled actions.
* Entity and door interaction.
* Event subscriptions.
* Optional gameplay modules.
* Error handling and cleanup.

Use clear module boundaries and avoid unnecessary abstraction.

Every module should have a defined responsibility, initialization lifecycle, configuration surface, and cleanup path.

Avoid:

* Monolithic classes that handle every gameplay system.
* Duplicate player-state sources.
* Scattered string-based command parsing.
* Unmanaged timers that survive round changes.
* Unsafe entity access after disconnects or entity destruction.
* Unnecessary reflection and brittle memory offsets.
* Excessive dependencies for small features.
* Broad exception handling that hides real failures.
* Silent fallbacks that make a broken feature appear functional.

Use the supported CounterStrikeSharp event and scheduler mechanisms. Respect the engine's threading and lifecycle requirements. Use appropriate main-thread scheduling for engine interactions when required.

Ensure that disconnects, deaths, round changes, map transitions, plugin reloads, and shutdown cannot leave stale state or invalid callbacks behind.

⸻

## 7. JAILBREAK GAMEPLAY — CORE REQUIREMENTS

The following list is the target scope, not a claim that every feature exists in the original repository. Confirm original features against the supplied source and mark additions separately.

### A. Round and game-mode manager

Implement a central, authoritative round state machine.

It should support appropriate states such as:

* Waiting for sufficient players.
* Round initialization.
* Cell preparation.
* Cells opened.
* Active Jailbreak gameplay.
* Round conclusion.
* Reset and cleanup.

Define legal transitions and reject invalid transitions.

Prevent duplicate round-start actions, duplicate timers, repeated round-end handling, and conflicting event callbacks.

Integrate with actual CS2 round events and game rules wherever supported. Do not simulate a successful game-rule transition by merely changing an internal Boolean.

Ensure that the plugin remains synchronized with the real game state after late loading, map changes, round restarts, and unexpected player disconnects.

### B. Team management

Implement or restore:

* Terrorist and Counter-Terrorist team handling.
* Warden eligibility and assignment.
* Warden removal and reassignment.
* Team restrictions where required by the original design.
* Player death and respawn handling.
* Disconnect and reconnect handling.
* Spectator and late-join behavior.
* Round-based team cleanup.
* Consistent player-state synchronization.

Use supported team and player APIs. Handle invalid players and unavailable entities safely.

Do not force team changes continuously or fight against the game's legitimate team-selection and round systems.

### C. Cell and door management

Inspect the original SMPlugins behavior before choosing an implementation.

Support, where the map and available APIs allow:

* Cell discovery or explicit cell configuration.
* Cell door opening and closing.
* Warden-triggered opening.
* Automatic opening at configured times.
* Prevention of duplicate cell-opening actions.
* Correct behavior across round restarts and map transitions.
* Diagnostic information when doors cannot be located.

CS2 maps do not necessarily use identical entity names, door layouts, or cell structures. Design the discovery system to tolerate map variation.

If automatic discovery is unreliable, provide a configurable fallback using verified entity identifiers, supported entity properties, or documented mapping data.

Do not pretend a door was opened when no actual game entity changed state.

### D. Warden system

Implement the original Warden functionality where supported, including:

* Taking and relinquishing Warden.
* Eligibility and conflict handling.
* Warden status tracking.
* Warden-specific commands.
* Warden death and disconnect handling.
* Warden replacement rules.
* Permission restrictions where applicable.
* Clear status messages.

Prevent two active Wardens unless the original design explicitly supports that behavior.

### E. Core admin system

Rebuild the administrative functionality needed by the original Jailbreak gameplay.

Inspect original commands and permissions rather than inventing a replacement command system.

Where applicable, support:

* Admin identity and permissions.
* Configurable permission groups.
* Command-level access restrictions.
* Warden and staff distinctions.
* Player management.
* Administrative gameplay controls.
* Informative denial messages.
* Audit-friendly logs for sensitive actions.

Use supported CounterStrikeSharp permission integrations when possible.

Never trust client-supplied player identifiers or user-controlled arguments without validation.

Do not hardcode administrator Steam IDs or ship insecure default credentials.

### F. Gameplay rules and player helpers

Recover and implement the original gameplay rules, including applicable systems such as:

* Prisoner and guard restrictions.
* Death and respawn rules.
* Last Request (LR), if present in the original source.
* Rebel or combat-state tracking.
* Round timers.
* Survival and round-ending conditions.
* Player status queries.
* Team-specific actions.
* Rule announcements and chat messages.

Each rule must have one authoritative implementation and clearly defined reset conditions.

Do not silently change established SMPlugins gameplay rules.

⸻

## 8. ORIGINAL SMPLUGINS FEATURES AND ADDITIONAL MODULES

Inventory every supplied plugin and module before deciding which features to port.

Prioritize the Jailbreak core first. Once it is stable, restore additional modules in dependency order.

Potential categories to investigate include:

* Fun commands.
* Admin utilities.
* Chat formatting and colors.
* Player helpers.
* Bhop checking or movement-related diagnostics.
* Effects and visual feedback.
* Utility commands.
* Map and round utilities.
* Any additional modules actually found in the original source.

This list is not evidence that every named feature exists or can be reproduced identically in CS2.

For every feature:

1. Determine what the original implementation actually does.
2. Identify the closest supported CS2 mechanism.
3. Rebuild the feature rather than wrapping incompatible legacy APIs.
4. Preserve command names, intended behavior, and configuration where feasible.
5. Document any unavoidable differences.
6. Add a targeted test or a reproducible manual verification procedure.

Separate required Jailbreak functionality from optional modules.

Optional features must not prevent the core Jailbreak plugin from loading when they fail, unless the feature is an explicitly required dependency.

Provide clear logs for optional features that are disabled or unavailable.

⸻

## 9. REAL CS2 RENDERING, EFFECTS, AND RAY TRACING

Use actual CS2 capabilities, not text-based imitations of engine features.

For chat, use supported chat and message APIs.

For visual effects, use verified engine-supported entities, particles, sounds, beams, or other appropriate mechanisms.

For aim indicators and ray tracing:

* Confirm the exact API exposed by the selected CounterStrikeSharp build.
* Determine whether the required operation is a true server-side ray trace, a supported entity query, or a different capability.
* Validate hit results, range, filters, and entity validity.
* Handle cases where no valid target is found.
* Do not substitute a guessed API signature or a fabricated hit result.
* Test actual behavior in a running server before marking it verified.

Do not use ASCII diagrams, printed coordinate strings, or chat messages as replacements for features that are expected to render in the game world.

If a requested effect cannot be implemented reliably with supported APIs, document the limitation and offer the closest honest alternative.

⸻

## 10. COMPATIBILITY AND DEPENDENCY MANAGEMENT

Target a specific, verified combination of:

* Counter-Strike 2 server build.
* MetaMod:Source build.
* CounterStrikeSharp release.
* .NET runtime and SDK.
* Any required supporting extensions.

Do not target a vague phrase such as "the latest version" without recording the versions actually used.

The project previously referenced CounterStrikeSharp 1.0.376. Determine whether that version remains appropriate before changing it. Avoid upgrading dependencies simply for the sake of upgrading.

If a version change is required:

* Explain why.
* Verify API compatibility.
* Update package references and configuration.
* Record the tested versions.
* Identify any required server-side installation changes.

The CounterStrikeSharp runtime files may be too large for me to upload. First determine whether the available assemblies, package references, source, or official documentation are sufficient.

If runtime files are genuinely necessary, identify the precise missing file or capability and give me the smallest practical way to provide it.

Do not repeatedly request a complete runtime download when a small assembly, version number, build log, or API definition would suffice.

⸻

## 11. BUILD SYSTEM AND COMPILATION REQUIREMENTS

The project must have a reproducible build process.

Inspect and repair:

* Solution and project files.
* Target framework.
* Package versions.
* Assembly references.
* Nullable reference settings.
* Implicit usings.
* Compiler warnings.
* Build configuration.
* Plugin output location.
* Dependency packaging.
* Runtime loading requirements.

Use deterministic, repeatable build commands wherever practical.

At minimum, attempt the appropriate equivalent of:

dotnet restore

dotnet build -c Release

Use the actual solution or project path discovered in the workspace.

Resolve all build-breaking errors before claiming a successful build.

Address warnings that indicate potential correctness, lifecycle, or compatibility issues. Explain any remaining unavoidable warnings.

Check that the output contains the expected plugin assembly and that required configuration or resource files are included.

Verify that the build does not accidentally bundle conflicting framework assemblies or incompatible runtime dependencies.

If the environment lacks the required SDK, package access, or runtime, continue with source analysis and other useful work, but clearly label the build as unverified.

Never fabricate successful command output.

⸻

## 12. TESTING AND ACCEPTANCE CRITERIA

A successful compilation is necessary but not sufficient.

Create a testing strategy with three levels.

### Level 1: Static and automated tests

Where practical, test:

* Configuration parsing and defaults.
* Round-state transitions.
* Invalid transition rejection.
* Player-state cleanup.
* Permission checks.
* Command argument validation.
* Timer cancellation.
* Feature enablement and disablement.
* Duplicate-event prevention.
* State reset behavior.

Use unit tests for logic that can be isolated from the game engine.

Do not write tests that simply restate the implementation without verifying meaningful behavior.

### Level 2: Plugin loading and server integration

On a compatible test server, verify:

* MetaMod recognizes the installation.
* CounterStrikeSharp loads the plugin.
* Dependencies resolve correctly.
* Configuration is generated or loaded correctly.
* Initialization and shutdown succeed.
* Plugin reloads do not create duplicate subscriptions.
* Missing optional dependencies produce useful diagnostics.
* Map changes and round restarts do not corrupt state.

Capture actual server logs and record the tested environment.

### Level 3: In-game Jailbreak verification

Create a practical checklist for:

* Player joins and disconnects.
* Team selection and switching.
* Round initialization.
* Cell-door discovery and opening.
* Warden assignment and removal.
* Admin permissions.
* Core Jailbreak commands.
* Death, respawn, and team-state synchronization.
* Round endings and resets.
* Timers and scheduled actions.
* Optional modules.
* Multiple consecutive rounds.
* Map changes.
* Plugin reloads.
* Unexpected event sequences.

Use at least two players where needed to validate multiplayer behavior.

If no running CS2 server is available, distinguish automated verification from unperformed in-game tests.

### Release acceptance criteria

A release candidate must:

* Restore and compile in the documented environment.
* Contain no known build-breaking errors.
* Load successfully in a compatible server when integration testing is available.
* Implement the defined core Jailbreak workflows.
* Enforce permissions correctly.
* Handle disconnects and round transitions safely.
* Provide reproducible testing instructions.
* Document every known blocker and unverified feature.

Do not claim the mod is fully production-ready if its core in-game behavior has not been tested.

⸻

## 13. SECURITY, STABILITY, AND PERFORMANCE

Prioritize server stability and predictable behavior.

Check for:

* Null or invalid player and entity references.
* Stale timers.
* Repeated event subscriptions.
* Unbounded collections.
* Unnecessary per-tick processing.
* Excessive logging.
* Unsafe entity handles.
* Race conditions between events.
* Permission bypasses.
* Malformed command input.
* Incorrect state restoration after plugin reloads.
* Resource leaks.

Prefer event-driven logic over polling when appropriate.

Use caching only when its invalidation rules are clear.

Keep expensive scans away from frequently executed callbacks. Avoid assumptions about player slots, entity indices, map entities, and event ordering.

Validate all externally supplied command arguments and all assumptions about game entities.

⸻

## 14. DEVELOPMENT WORKFLOW AND PRIORITIES

Work in small, coherent, verifiable milestones.

Use this order unless the audit provides a strong technical reason to change it:

Milestone 1 — Audit and recovery
* Inspect the repository and reference material.
* Establish the current build state.
* Create the feature inventory.
* Identify the minimum necessary fixes.

Milestone 2 — Build foundation
* Repair project configuration.
* Verify dependencies and APIs.
* Compile a minimal plugin.
* Confirm the loading architecture.

Milestone 3 — Core runtime
* Lifecycle.
* Configuration.
* Logging.
* Player state.
* Round state.
* Cleanup and event handling.

Milestone 4 — Jailbreak gameplay
* Teams.
* Warden.
* Cells and doors.
* Permissions.
* Core commands.
* Round rules.

Milestone 5 — Original feature restoration
* Port remaining SMPlugins functionality.
* Recover fun commands and supporting systems.
* Implement or document movement-related checks.
* Add visual effects and optional modules.

Milestone 6 — Verification and stabilization
* Automated tests.
* Release build.
* Server integration.
* Multiplayer test plan.
* Fix observed failures.

Milestone 7 — Documentation and delivery
* Final feature matrix.
* Installation and configuration guide.
* Test instructions.
* Known limitations.
* Release notes.
* Handoff.

After every meaningful milestone:

1. Build the project.
2. Run relevant tests.
3. Inspect the resulting changes.
4. Update the persistent documentation.
5. Preserve a recoverable working state.

If Git is available, use small, descriptive commits when appropriate. Do not discard user changes, rewrite useful history, or perform destructive cleanup without establishing that it is safe.

⸻

## 15. ENGINEERING RULES — NON-NEGOTIABLE

1. Do not restart the project from scratch unless the existing architecture is demonstrably unsalvageable.
2. Do not replace original SMPlugins gameplay with an unrelated design.
3. Do not copy eGo branding or treat the reference implementation as the project's source of truth.
4. Do not fabricate API signatures, build results, tests, or server behavior.
5. Do not mark an implementation verified when it has only been written or compiled.
6. Do not leave known critical errors unresolved while expanding optional features.
7. Do not create duplicate helpers or competing state managers without a demonstrated need.
8. Do not silently delete features, assets, configurations, or source files.
9. Do not create fake rendering or simulated engine behavior that masquerades as a working feature.
10. Do not spend an entire session researching when useful implementation work can proceed.
11. Do not make unnecessary breaking changes to commands, configuration, or gameplay.
12. Do not stop after producing an audit or plan if there is enough information to make concrete progress.
13. Do not claim to have tested against a live server unless a live server was actually available.
14. Do not assume a successful build guarantees runtime compatibility.
15. Do not allow a single optional feature to unnecessarily disable the entire core plugin.
16. Do not conceal limitations caused by missing files, unavailable tooling, or session constraints.

If a decision is uncertain, state the uncertainty, investigate the smallest relevant question, and choose the least destructive defensible approach.

⸻

## 16. FINAL FEATURE AUDIT

Before release, compare the final implementation against the original SMPlugins source, not just the features that were convenient to port.

Every discovered feature must appear in the final inventory with its status.

Specifically identify:

* Fully ported and verified features.
* Implemented but unverified features.
* Partially ported features.
* Features that remain unimplemented.
* Features blocked by missing dependencies.
* Features that cannot be faithfully reproduced in CS2.
* Unused or retired helpers and agents.
* Legacy features intentionally excluded and the reason for exclusion.
* Original functionality that was replaced with a materially different implementation.

For untouched SMPlugins modules, explicitly describe what they do, whether they were retained, and why.

For any remaining helper, class, dependency, or module that appears unused, confirm its status before removing it.

Do not hide unfinished work behind broad labels such as "complete core systems."

⸻

## 17. REQUIRED DELIVERABLES

Deliver one cohesive, clearly named Jailbreak project ready for the next stage of build and in-game testing.

Where the environment permits, provide:

1. Complete source code and project/solution files.
2. A Release build artifact or the exact commands needed to generate it.
3. Required configuration files with documented defaults.
4. Installation instructions for CS2, MetaMod:Source, and CounterStrikeSharp.
5. A step-by-step in-game test guide.
6. A complete feature inventory and porting status.
7. Architecture and compatibility documentation.
8. A list of commands, permissions, and configuration options.
9. A known-issues and engine-limitations document.
10. A summary of original SMPlugins features that remain untouched or unavailable.
11. A changelog and final session handoff.
12. The exact versions used for the last verified build and test.

The mod must credit:

Gdaki
Designed after HlStriker, Swbs development/lead admins

Preserve appropriate original author attribution and licenses. Review applicable licenses before redistributing modified source or bundled third-party code.

Do not claim that the output is ready for live production merely because a ZIP or DLL was generated.

⸻

## 18. FINAL RESPONSE FORMAT

When a release candidate is ready, respond with a structured report containing:

A. Build status
* Whether restore succeeded.
* Whether compilation succeeded.
* The target runtime and dependency versions.
* Any warnings or unresolved errors.

B. Implemented functionality
List actual features, grouped by subsystem, with a clear distinction between verified and unverified functionality.

C. Testing results
Report exactly which automated, server-integration, and in-game tests were performed and their outcomes.

D. Remaining SMPlugins features
Identify every important unported, partially ported, or intentionally excluded feature.

E. Installation
Provide exact deployment paths, commands, configuration steps, and server restart or reload instructions.

F. In-game testing
Provide a numbered checklist that I can follow on my own CS2 server.

G. Known issues and limitations
Explain technical blockers and workarounds without overstating certainty.

H. Project files
List the delivered source, build artifacts, and documentation.

I. Recommended next steps
Provide the highest-priority remaining actions in order.

⸻

## 19. START NOW

We are beginning this session with an assumed 0% verified completion, not an assumption that the repository contains zero useful work.

First inspect all currently available project files and the working directory.

Do not begin by blindly rewriting everything.

Establish the actual baseline, determine which parts of the existing project are salvageable, and create the persistent project documentation.

Then immediately address the highest-priority issues that can be resolved with the available environment.

Continue through as many milestones as time and tooling permit. Before the session ends, leave the project in the strongest recoverable state possible, with a working build whenever feasible and a precise handoff for the next session.

The objective is not to produce an impressive-looking amount of code. It is to recover, rebuild, test, and document a genuinely functional CS2 Jailbreak project that faithfully preserves the SMPlugins design.

Begin with the audit and proceed directly into implementation.
