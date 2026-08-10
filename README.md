# RimWorld MOD: Good Sleep

*[日本語版はこちら / Japanese version](README.ja.md)*

## Goal
When a colonist's schedule slot is set to "Sleep", vanilla RimWorld only actually sends them to bed once they're tired enough (`Need_Rest` low enough that `RestUtility.CanFallAsleep` returns true). Some modded races add pawns that never accumulate fatigue at all (no `Need_Rest`, or a rest need that never drops), and such pawns simply never go to sleep on their own - even with a full 8-hour "Sleep" block on their schedule.

This mod makes pawns actually go to bed at their scheduled sleep time regardless of tiredness, including such races. If a pawn currently has priority-1 work available (the highest priority column in the Work tab), that work still comes first; forced sleep only kicks in once nothing more urgent needs doing.

- Mod name / author / packageId are finalized (`Good Sleep` / `takeroot1120` / `takeroot1120.goodsleep`).

## Prerequisites
- RimWorld: `1.6.4871 rev590`
  - Default Steam install path: `C:\Program Files (x86)\Steam\steamapps\common\RimWorld`
- .NET SDK: `8.0.423` or compatible.
  Build with `dotnet build Source/GoodSleep/GoodSleep.csproj`.
  (If `dotnet` isn't on the current shell's PATH, invoke the full path to `dotnet.exe` instead, e.g. `C:\Program Files\dotnet\dotnet.exe`.)
- No mod dependencies. A copy of [Lib.Harmony](https://github.com/pardeike/Harmony) (`0Harmony.dll`, MIT license) is vendored under `Source/GoodSleep/Libs/` so the project builds standalone; it's copied into `1.6/Assemblies/` alongside the mod's own DLL at build time.

## Implementation
Actual classes/methods identified by decompiling `Assembly-CSharp.dll` with ILSpyCmd:
- `RimWorld.JobGiver_GetRest` (a `ThinkNode_JobGiver` in the pawn think tree, positioned after emergency handling like firefighting/patient rescue and before normal work)
  - `GetPriority(Pawn)` returns `0` unless the pawn is tired enough to fall asleep (`Need_Rest` missing entirely, or `RestUtility.CanFallAsleep` says "not tired yet", both return `0`). `ThinkNode_PrioritySorter` never even considers a node whose priority is `0`, so `TryGiveJob` is never called for pawns that aren't tired - regardless of their schedule.
  - `TryGiveJob(Pawn)` builds the actual `LayDown` job (via `RestUtility.FindBedFor`, falling back to a ground sleep spot found by the private `TryFindGroundSleepSpotFor`) once the node has been selected.
- `Verse.AI.Job.forceSleep` (public field) - when set, `Toils_LayDown`'s tick action treats the pawn as able to fall asleep (`RestUtility.CanFallAsleep(actor) || curJob.forceSleep`) and suppresses the automatic wake-up check (`RestUtility.ShouldWakeUp(actor) && !curJob.forceSleep`), regardless of rest level.

`Source/GoodSleep/HarmonyPatches.cs` patches both methods:
- `Patch_JobGiver_GetRest_GetPriority_ForceScheduledSleep` (postfix on `GetPriority`): if vanilla returned `0` and the pawn's current schedule slot is "Sleep" (checked via the lightweight `GoodSleepUtility.IsScheduledSleepNow`), forces the same priority value (`8`) vanilla itself uses for a sleep-scheduled, tired pawn. This is what makes the node get considered at all for pawns that are never "tired enough" by vanilla's own definition.
- `Patch_JobGiver_GetRest_TryGiveJob_ForceScheduledSleep` (postfix on `TryGiveJob`): if vanilla didn't produce a job, and `GoodSleepUtility.ShouldForceSleepNow` (schedule check plus "no priority-1 work available", see below) says the pawn should be forced to sleep, builds a `LayDown` job the same way vanilla does (reusing the private `TryFindGroundSleepSpotFor` via reflection when no bed is available) and sets `forceSleep = true` on it.

### The "priority-1 work" exception
`GoodSleepUtility.HasPendingPriority1Work` implements the "don't force sleep if urgent work is pending" rule: it walks every `WorkTypeDef` the pawn has set to priority 1 in the Work tab, and for each of its `WorkGiverDef`s checks whether a job is actually available right now (`WorkGiver.ShouldSkip`/`MissingRequiredCapacity`, then `WorkGiver_Scanner.PotentialWorkThingsGlobal`/`PotentialWorkCellsGlobal` + `HasJobOnThing`/`HasJobOnCell` for scanning givers, or `NonScanJob` otherwise). If the pawn is already mid-job on a priority-1 work type, that in-progress job isn't interrupted either. This is effectively the same scan `JobGiver_Work` performs later, run early only for priority-1 work types (typically a very small set per pawn), so the added cost is limited to the pawn's scheduled sleep hours.

### Bug found during testing: waking up exactly at 100% rest
Initial testing showed pawns would go to sleep correctly, but wake up again the moment their rest gauge hit 100% (then fall back asleep once their current task finished, repeating in a loop). The `GoodSleepUtility.IsScheduledSleepNow` eligibility check originally excluded pawns already in a `LayDown` job, on the assumption that a forced sleep job already running didn't need to be re-forced. But once rest reaches 100%, vanilla's own `GetPriority` genuinely drops to `0` (per `RestUtility.CanFallAsleep`) - at that exact moment, *our* forced override was the only thing still claiming priority `8` for the sleeping pawn, and excluding "already sleeping" pawns from that override meant the priority silently dropped to `0`, letting any other job with a positive priority pre-empt the sleep. Removing that exclusion (so the forced priority keeps being asserted for the entire duration of the scheduled sleep block, not just up to the moment the pawn falls asleep) fixed it - confirmed in-game.

## In-game verification status
Confirmed in-game, for all three of: a normal humanlike colonist, a modded race whose rest need never depletes (always shows 100%), and a modded race with no rest need at all:
- Assigning "Sleep" to the current schedule slot sends the pawn to bed even when not tired
- The pawn keeps sleeping past 100% rest instead of waking up immediately
- (Verified for the no-rest-need and always-100%-rest races specifically, which vanilla cannot put to sleep on its own at all)

The priority-1 work exception is implemented as described above but has not yet been separately regression-tested against a live priority-1 job in-game.

## Folder structure
```
rimworld-good-sleep/
  About/About.xml            Mod metadata (name/author/packageId finalized)
  LoadFolders.xml            1.6 load config
  Source/GoodSleep/          C# Harmony patch source
    Libs/0Harmony.dll        Vendored Lib.Harmony (MIT), referenced at build time
  1.6/Assemblies/            Build output DLL destination
```
