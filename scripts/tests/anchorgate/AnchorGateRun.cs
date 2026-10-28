// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. SubDL Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T160 / F-M368: a run that deferred itself must come back, and a deadline must
// be read in the clock it was written in.
//
// THE MEASURED CASE, replayed as three assertions (28.10.2026, prod):
//
//   16:10  Database refresh fired and finished (marker "20261028" consumed).
//   16:25  A second attempt found the global run lock held by "upload" and deferred:
//          "[SubDL] Database refresh re-fire scheduled at 2026-10-28 15:40 UTC"
//          (15:40 UTC = 16:40 local, CET).
//   after  Not one refresh line for the rest of the day, the row stayed yellow at 16:24.
//
// Two defects, both asserted here:
//   (1) EXCLUSION. `due = … && (marker != today || now < until)` with `if (due && now >= until)`
//       cannot ever be true for a consumed marker: while the deadline is ahead the fire gate blocks,
//       and once it arrives the due-term falls to false. The deferral therefore never re-armed.
//   (2) MIXED CLOCK. The anchor blocks wrote `DateTime.Now`, the tasks `DateTime.UtcNow`, every
//       comparison used `DateTime.Now`. A 15:40 UTC deadline read against 16:25 local looks expired
//       an hour early; the pending window closed before it opened.
//
// Usage: dotnet publish Jellyfin.Plugin.SubdlSync -c Release -o out
//        dotnet run --project scripts/tests/anchorgate/anchorgate.csproj -- [repo-root]

using System;
using System.IO;
using Jellyfin.Plugin.SubdlScribe.ScheduledTasks;

namespace AnchorGateRun;

internal static class Program
{
    private static int _checks;
    private static readonly System.Collections.Generic.List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what);
        }
    }

    // The measured frame: CET = UTC+1, so 16:25 local is 15:25 UTC.
    private static readonly DateTime NowLocal = new(2026, 10, 28, 16, 25, 0, DateTimeKind.Unspecified);
    private static readonly DateTime NowUtc = new(2026, 10, 28, 15, 25, 0, DateTimeKind.Utc);
    private static readonly DateTime Anchor = new(2026, 10, 28, 16, 19, 0, DateTimeKind.Unspecified);
    private const string Marker = "20261028";
    private const string Consumed = "20261028";

    private static int Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : "/opt/data/subdl-scribe";

        // ── 1. The gate: marker, anchor, cadence ────────────────────────────────
        Console.WriteLine("1. Anchor gate");
        Check(AnchorJobGate.IsDue(NowLocal, Anchor, true, null, Marker, false),
            "unconsumed marker, anchor reached, cadence due → due");
        Check(!AnchorJobGate.IsDue(NowLocal, Anchor, true, Consumed, Marker, false),
            "consumed marker, no deferral → NOT due (a fired day stays fired)");
        Check(AnchorJobGate.IsDue(NowLocal, Anchor, true, Consumed, Marker, true),
            "consumed marker + PENDING DEFERRAL → due again (the fix; today's code says false)");
        Check(!AnchorJobGate.IsDue(NowLocal, Anchor, false, null, Marker, false),
            "cadence not due → not due");
        Check(!AnchorJobGate.IsDue(NowLocal, Anchor.AddHours(1), true, null, Marker, false),
            "anchor in the future → not due");
        Check(!AnchorJobGate.IsDue(NowLocal, null, true, null, Marker, false),
            "no anchor today → not due");

        // ── 2. The deadline, in UTC ─────────────────────────────────────────────
        Console.WriteLine("2. Deadline gate");
        var deadline = new DateTime(2026, 10, 28, 15, 40, 0, DateTimeKind.Utc);   // 16:40 local
        Check(!AnchorJobGate.DeferralElapsed(NowUtc, deadline),
            "16:25 local (15:25 UTC) against a 15:40 UTC deadline → still waiting");
        Check(AnchorJobGate.DeferralElapsed(NowUtc.AddMinutes(15), deadline),
            "16:40 local (15:40 UTC) → deadline reached, fire");
        Check(AnchorJobGate.DeferralElapsed(NowUtc, DateTime.MinValue),
            "no deferral armed → gate open");

        // The exclusion itself, spelled out: due must not be evaluated with the deadline inside it.
        var due = AnchorJobGate.IsDue(NowLocal, Anchor, true, Consumed, Marker, true);
        var elapsed = AnchorJobGate.DeferralElapsed(NowUtc.AddMinutes(15), deadline);
        Check(due && elapsed,
            "the measured 16:40 tick: due AND deadline reached → the run happens (both were false before)");

        // A local-stamped deadline compared as if it were UTC is the second defect. The old comparison
        // fed the LOCAL clock value into a UTC deadline: 16:25 against 15:40 says "already expired".
        var oldStyleNowLocal = new DateTime(2026, 10, 28, 16, 25, 0, DateTimeKind.Unspecified);
        Check(AnchorJobGate.DeferralElapsed(oldStyleNowLocal, deadline),
            "OLD: 16:25 read raw against a 15:40 UTC deadline → 'expired' an hour early (the defect)");
        Check(!AnchorJobGate.DeferralElapsed(NowUtc, deadline),
            "NEW: 15:25 UTC against the same deadline → still waiting, as intended");

        // ── 3. Normalising a state file written by the old code ──────────────────
        Console.WriteLine("3. Deadline normalisation");
        var local = new DateTime(2026, 10, 28, 16, 40, 0, DateTimeKind.Local);
        var normalised = AnchorJobGate.NormalizeDeadline(local);
        Check(normalised.Kind == DateTimeKind.Utc, "a LOCAL deadline comes back as UTC");
        Check(normalised == local.ToUniversalTime(), "and it names the same instant");
        Check(AnchorJobGate.NormalizeDeadline(deadline) == deadline, "a UTC deadline is untouched");
        Check(AnchorJobGate.NormalizeDeadline(DateTime.MinValue) == DateTime.MinValue, "MinValue stays MinValue");
        Check(AnchorJobGate.NormalizeDeadline(
                new DateTime(2026, 10, 28, 15, 40, 0, DateTimeKind.Unspecified)).Kind == DateTimeKind.Utc,
            "an unspecified value is read as UTC, not as local");

        // ── 4. Wiring on the source ────────────────────────────────────────────
        Console.WriteLine("4. Source contract");
        var src = File.ReadAllText(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/SubdlSchedulerCoordinator.cs"));
        Check(src.Contains("AnchorJobGate.IsDue(", StringComparison.Ordinal), "the gate is used");
        Check(src.Contains("AnchorJobGate.DeferralElapsed(", StringComparison.Ordinal), "the deadline gate is used");
        Check(!src.Contains("|| now < _pruneDeferredUntil)", StringComparison.Ordinal),
            "the exclusion `|| now < _pruneDeferredUntil` is gone (refresh)");
        Check(!src.Contains("|| now < _oshashDeferredUntil)", StringComparison.Ordinal),
            "the exclusion is gone (oshash)");
        Check(!src.Contains("|| now < _postprocessDeferredUntil)", StringComparison.Ordinal),
            "the exclusion is gone (postprocess)");
        Check(!src.Contains("_pruneDeferredUntil = now.AddMinutes", StringComparison.Ordinal),
            "no local-clock deadline is written any more (refresh)");
        Check(!src.Contains("_oshashDeferredUntil = now.AddMinutes", StringComparison.Ordinal),
            "no local-clock deadline is written any more (oshash)");
        Check(src.Contains("AnchorJobGate.NormalizeDeadline(when)", StringComparison.Ordinal),
            "the state parser normalises deadlines on load");

        // F-M368: one counter per job. "dbrefresh" was a SECOND budget for the refresh — counted by
        // ScheduleRefreshFire, never reset by the fire — so after 16 task-side deferrals the arming call
        // refused silently and left no deadline at all.
        Check(src.Contains("TryCountReschedule(\"refresh\",", StringComparison.Ordinal),
            "the task-side deferral counts on the same slot the fire resets (refresh)");
        Check(!src.Contains("TryCountReschedule(\"dbrefresh\",", StringComparison.Ordinal),
            "no second, never-reset budget under the name dbrefresh");
        Check(src.Contains("ResetReschedule(\"refresh\")", StringComparison.Ordinal),
            "a real refresh fire clears that counter");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"anchorgate: {_checks} checks, all green."
            : $"anchorgate: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
