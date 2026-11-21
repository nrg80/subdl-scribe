// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without
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
using Jellyfin.Plugin.SubScribe.ScheduledTasks;

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
            root, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/ScribeSchedulerCoordinator.cs"));
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

        // F-M368, second half: the refetch slot PRINTED a retry time and stored nothing, so it re-probed
        // every 30-s tick, burnt its 16x budget in ~8 minutes and then went silent. The spacing is a
        // stored UTC deadline now — gated, cleared on fire, persisted.
        Check(src.Contains("_refetchDeferredUntil = nowUtc.AddMinutes", StringComparison.Ordinal),
            "the refetch deferral arms a real UTC deadline");
        Check(src.Contains("AnchorJobGate.DeferralElapsed(nowUtc, _refetchDeferredUntil)", StringComparison.Ordinal),
            "and the slot is gated by it instead of re-probing every tick");
        Check(src.Contains("_refetchDeferredUntil = DateTime.MinValue; // F-M368: fired", StringComparison.Ordinal),
            "a real refetch fire clears it");
        Check(src.Contains("refetchDefer", StringComparison.Ordinal)
              && src.Contains("ParseInstant(fields, \"refetchDefer\")", StringComparison.Ordinal),
            "it survives a restart (written into the state payload and parsed back)");
        Check(!src.Contains("var spacedRef = now.AddMinutes", StringComparison.Ordinal),
            "no retry time is merely printed any more");

        // F-M368, third part: the display. A deadline that has already passed was still reported as a
        // pending fire, so the row read "defer - next attempt Wed 4:40 PM" hours after 16:40 while the run
        // had finished green. Every one of the five keys must be gated by a deadline still in the future.
        var pend = src.IndexOf("public System.Collections.Generic.Dictionary<string, DateTime> GetPendingFires()", StringComparison.Ordinal);
        var body = pend >= 0 ? src.Substring(pend, Math.Min(2500, src.Length - pend)) : string.Empty;
        Check(body.Length > 0, "GetPendingFires is present");
        Check((body.Split("nowUtc < ").Length - 1) >= 5,
            "all five pending-fire keys are gated by a future deadline");
        Check(body.Contains("if (prune != DateTime.MinValue && nowUtc < prune)", StringComparison.Ordinal),
            "the database deadline specifically is gated");

        // ── 5. The upload postprocessing holds the refetch (F-M666) ──────────────
        // Operator order 21.11.2026, „Retetch zählt auch den Postprocessing mit": while an upload
        // postprocessing is armed or running, the refetch fire is deferred — like a held run lock, with a
        // reason a reader can act on. The decision is a pure function so it can be checked here without a
        // server: `ScribeSchedulerCoordinator.CheckAndFire` returns at once when `Plugin.Instance` is null,
        // and `Plugin.Instance` has a private setter, so the tick itself is not driveable from a test.
        Console.WriteLine("5. Upload postprocessing holds the refetch (F-M666)");

        var ppNow = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var ppDue = ppNow.AddMinutes(20);

        var heldPending = AnchorJobGate.UploadPostprocessHold(ppNow, ppDue, false, true, 30);
        Check(heldPending is not null, "an armed postprocessing moment ⇒ the refetch is HELD");
        Check(heldPending is not null && heldPending.StartsWith("upload postprocessing pending until ", StringComparison.Ordinal),
            "the reason names the postprocessing and the moment — " + (heldPending ?? "null"));
        Check(heldPending is not null && heldPending.EndsWith(
                ppDue.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal),
            "the moment in the reason is LOCAL time, the clock the operator reads");
        Check(heldPending is not null && !heldPending.Contains("F-M", StringComparison.Ordinal)
              && !heldPending.Contains('_'),
            "the reason carries no requirement number and no code token (it is read without the code)");

        Check(AnchorJobGate.UploadPostprocessHold(ppNow, ppNow, false, true, 30) is null,
            "the armed moment has ARRIVED ⇒ no hold — the refetch fires (no waiting on a spent order)");
        Check(AnchorJobGate.UploadPostprocessHold(ppNow, ppNow.AddMinutes(-30), false, true, 30) is null,
            "an overdue armed moment ⇒ no hold either (the postprocessing block above had its turn)");

        Check(AnchorJobGate.UploadPostprocessHold(ppNow, ppDue, false, true, 0) is null,
            "UploadPostprocessDelayMinutes = 0 ⇒ no anchor and no hold (F-M445, refetch runs as today)");

        var heldRunning = AnchorJobGate.UploadPostprocessHold(ppNow, null, true, true, 30);
        Check(heldRunning == "upload postprocessing is running",
            "a RUNNING postprocessing holds the refetch too — " + (heldRunning ?? "null"));
        Check(AnchorJobGate.UploadPostprocessHold(ppNow, ppDue, true, true, 30) == "upload postprocessing is running",
            "running outranks pending — the immediate truth wins the line");
        Check(AnchorJobGate.UploadPostprocessHold(ppNow, null, true, true, 0) == "upload postprocessing is running",
            "a manual postprocessing run holds it even with the delay at 0 — the run is real, not an anchor");

        Check(AnchorJobGate.UploadPostprocessHold(ppNow, ppDue, false, false, 30) is null,
            "upload switch OFF with a stale armed moment ⇒ NO hold (an order that can never fire must not block)");

        // The wiring: the coordinator must not grow a second mechanism. It asks this gate, inside the
        // refetch block, and does exactly what the lock test does — deadline, reason, budget, return.
        Console.WriteLine("6. Refetch wiring (F-M666)");
        var refetchStart = src.IndexOf("var effective = config.RefetchInterval;", StringComparison.Ordinal);
        Check(refetchStart > 0, "the refetch block is present");
        var refetchBlock = refetchStart > 0 ? src.Substring(refetchStart, Math.Min(12000, src.Length - refetchStart)) : string.Empty;

        Check(refetchBlock.Contains("AnchorJobGate.UploadPostprocessHold(", StringComparison.Ordinal),
            "the refetch asks the postprocessing gate");
        Check(refetchBlock.Contains("Pipeline.UploadPipeline.IsPostprocessingRunning", StringComparison.Ordinal),
            "the running state is read from the live flag, not guessed");
        Check(refetchBlock.Contains("config.UploadPostprocessDelayMinutes", StringComparison.Ordinal),
            "the configured delay is read there (0 = no anchor)");
        Check(refetchBlock.Contains("_refetchFireWhy = postprocessHold;", StringComparison.Ordinal),
            "the reason travels into _refetchFireWhy — the field GetCycleDeferral prints");
        Check(src.Contains("return (when, _refetchFireWhy ?? \"retry scheduled\", versuche);", StringComparison.Ordinal),
            "and GetCycleDeferral hands it to the page as the line's reason");

        var holdAt = refetchBlock.IndexOf("AnchorJobGate.UploadPostprocessHold(", StringComparison.Ordinal);
        var lockAt = refetchBlock.IndexOf("Pipeline.PipelineRunLock.IsFree(_logger)", StringComparison.Ordinal);
        var markerAt = refetchBlock.IndexOf("_lastFiredMarker = fire.Marker;", StringComparison.Ordinal);
        var spacingAt = refetchBlock.IndexOf("_refetchDeferredUntil = nowUtc.AddMinutes(JobSpacing(config));", StringComparison.Ordinal);
        Check(holdAt > 0 && lockAt > holdAt,
            "the postprocessing gate sits BEFORE the lock test — the running postprocessing IS the lock holder");
        Check(holdAt > 0 && markerAt > holdAt,
            "the hold returns BEFORE the marker is consumed — the slot stays claimable");
        Check(spacingAt > holdAt && spacingAt < markerAt,
            "the deferral uses the same UTC deadline as the lock-busy deferral (no second clock)");
        Check(refetchBlock.Contains("TryCountReschedule(\"refetch\", \"postprocessing pending\")", StringComparison.Ordinal),
            "it counts on the same 16× budget as the other refetch deferrals");
        Check(refetchBlock.Contains("refetch", StringComparison.Ordinal)
              && src.Contains("Refetch fire ({Anchor}) deferred — {Why}; retrying at {Retry:yyyy-MM-dd HH:mm} local (F-M666).", StringComparison.Ordinal),
            "the log line names the cause in the operator's words and the retry moment in local time");

        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0
            ? $"anchorgate: {_checks} checks, all green."
            : $"anchorgate: {_checks} checks, {Failures.Count} FAILED.");
        return Failures.Count == 0 ? 0 : 1;
    }
}
