// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// F-M438: a plugin-owned field that is NEW to an existing configuration file has to be inserted at its
// declared position. Jellyfin's XML serializer matches elements in declaration order; an element that
// sits behind a later-declared member is ignored on load, so the value survives until the next restart
// and then reads as unset. Measured 04.11.2026 on prod: the postprocessing watermark was written,
// worked for the rest of the session and was gone after a restart.

using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.SubdlScribe;
using Jellyfin.Plugin.SubdlScribe.Configuration;

namespace StateFileRun;

internal static class Program
{
    private static int _checks;
    private static int _failed;

    private static void Check(bool ok, string text)
    {
        _checks++;
        if (!ok)
        {
            _failed++;
            Console.WriteLine("  [FAIL] " + text);
        }
    }

    private static string[] Order => typeof(PluginConfiguration)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => p.Name)
        .ToArray();

    private static PluginConfiguration Load(string path)
    {
        using var stream = File.OpenRead(path);
        return (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(stream)!;
    }

    private static string WriteExistingFile(string dir, PluginConfiguration cfg)
    {
        string path = Path.Combine(dir, "Jellyfin.Plugin.SubdlSync.xml");
        using (var stream = File.Create(path))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(stream, cfg);
        }

        return path;
    }

    /// <summary>Walks up from the test binary until the plugin project directory is found.</summary>
    private static string FindPluginRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "Jellyfin.Plugin.SubdlSync");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("plugin root not found");
    }

    private static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "statefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var user = new PluginConfiguration
            {
                Username = "konto@example.invalid",
                PostprocessMaxPages = 20000,
            };

            // 1) Ein Feld, das Jellyfin als xsi:nil geschrieben hat, überlebt den Neustart.
            string path = WriteExistingFile(dir, user);
            var nilElement = XDocument.Load(path).Root!.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "PostprocessWatermarkUtc");
            Check(nilElement != null && nilElement.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance")) != null,
                "F-M438: a nullable field sits in the file as xsi:nil, not missing");
            var stamp = new DateTime(2026, 11, 4, 12, 58, 7, DateTimeKind.Utc);
            Plugin.PatchPluginStateFile(path, new Dictionary<string, string?>
            {
                ["PostprocessWatermarkUtc"] = stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            }, Order);
            var reloaded = Load(path);
            Check(reloaded.PostprocessWatermarkUtc?.ToUniversalTime() == stamp,
                "F-M438: a field new to the file is read back after a restart");
            Check(reloaded.Username == user.Username,
                "F-M438: a user field next to it is untouched");
            Check(reloaded.PostprocessMaxPages == 20000,
                "F-M438: a user field after it is untouched");

            // 1b) Ein Feld, das die Datei GAR NICHT kennt (Datei einer älteren Version), wird an seiner
            //     Deklarationsposition eingefügt und überlebt ebenfalls.
            string absent = WriteExistingFile(dir, user);
            var absentDoc = XDocument.Load(absent);
            absentDoc.Root!.Elements().Where(e => e.Name.LocalName == "PostprocessWatermarkUtc").Remove();
            absentDoc.Save(absent);
            Check(!XDocument.Load(absent).Root!.Elements().Any(e => e.Name.LocalName == "PostprocessWatermarkUtc"),
                "F-M438: the fixture can start without the element at all");
            Plugin.PatchPluginStateFile(absent, new Dictionary<string, string?>
            {
                ["PostprocessWatermarkUtc"] = stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            }, Order);
            Check(Load(absent).PostprocessWatermarkUtc?.ToUniversalTime() == stamp,
                "F-M438: an element the file does not know yet is inserted at its declared position");

            // 2) Das Feld steht an seiner Deklarationsposition — nicht am Dateiende.
            var names = XDocument.Load(path).Root!.Elements().Select(e => e.Name.LocalName).ToList();
            int watermark = names.IndexOf("PostprocessWatermarkUtc");
            int runtime = names.IndexOf("PostprocessMaxRuntimeMinutes");
            Check(watermark >= 0 && (runtime < 0 || watermark < runtime),
                "F-M438: the new element lands before the members declared after it");

            // 3) Die alte Schreibweise (ans Ende anhängen) wird als Fehler erkannt — der Test kann also
            //    nicht grün werden, wenn die Positionierung wieder verloren geht.
            string appendPath = WriteExistingFile(dir, user);
            var doc = XDocument.Load(appendPath);
            doc.Root!.Add(new XElement("PostprocessWatermarkUtc", stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")));
            doc.Save(appendPath);
            Check(Load(appendPath).PostprocessWatermarkUtc == null,
                "F-M438: appending at the end of the file is exactly what used to lose the value");

            // 4) Ein bestehendes Feld wird weiter nur im Wert ersetzt; die Datei bleibt sonst gleich.
            string existing = WriteExistingFile(dir, user);
            string before = File.ReadAllText(existing);
            Plugin.PatchPluginStateFile(existing, new Dictionary<string, string?>
            {
                ["Username"] = "zweit@example.invalid",
            }, Order);
            string after = File.ReadAllText(existing);
            Check(Load(existing).Username == "zweit@example.invalid", "F-M438: an existing field is replaced");
            Check(before.Length > 0 && after.Contains("<PostprocessMaxPages>20000</PostprocessMaxPages>", StringComparison.Ordinal),
                "F-M438: the rest of the document survives");

            // 5) Null-Werte werden nicht als leeres Element geschrieben.
            string nullPath = WriteExistingFile(dir, user);
            Plugin.PatchPluginStateFile(nullPath, new Dictionary<string, string?>
            {
                ["PostprocessWatermarkUtc"] = null,
            }, Order);
            Check(Load(nullPath).PostprocessWatermarkUtc == null,
                "F-M438: a null value leaves the field unset instead of writing it");

            // 6) F-M445: der Nachlauf hat keinen festen Anker mehr — ein Termin, letzter Auftrag gewinnt.
            string root = FindPluginRoot();
            string sched = File.ReadAllText(Path.Combine(root, "ScheduledTasks", "SubdlSchedulerCoordinator.cs"));
            string upload = File.ReadAllText(Path.Combine(root, "Pipeline", "UploadPipeline.cs"));
            string cfg = File.ReadAllText(Path.Combine(root, "Configuration", "PluginConfiguration.cs"));
            Check(sched.Contains("_postprocessArmUtc = fireAtUtc;", StringComparison.Ordinal),
                "F-M445: arming replaces the moment instead of comparing against it (last order wins)");
            Check(!sched.Contains("_postprocessDeferredUntil", StringComparison.Ordinal),
                "F-M445: the old lock-busy deferral field is gone — it only delayed a due anchor");
            Check(sched.Contains("config.UploadEnabled && _postprocessArmUtc != DateTime.MinValue", StringComparison.Ordinal),
                "F-M445: the fire needs upload enabled and an armed moment, nothing else");
            Check(!sched.Contains("RandomPostprocessTime", StringComparison.Ordinal)
                  && !sched.Contains("UploadPostprocessInterval", StringComparison.Ordinal)
                  && !sched.Contains("PostprocessMinSpacingHours", StringComparison.Ordinal),
                "F-M445: no fixed anchor, no diced day+time, no minimum spacing left in the scheduler");
            Check(upload.Contains("ArmUploadPostprocessing(", StringComparison.Ordinal)
                  && upload.Contains("if (summary.StopRequested)", StringComparison.Ordinal),
                "F-M445: a normal run end arms it, a user stop goes the other way");
            Check(cfg.Contains("public int UploadPostprocessDelayMinutes { get; set; } = 30;", StringComparison.Ordinal),
                "F-M445: the delay is 30 minutes by default");
            Check(sched.Contains("if (delay <= 0)", StringComparison.Ordinal)
                  && sched.Contains("delay is 0 — the postprocessing is inactive", StringComparison.Ordinal),
                "F-M445: delay 0 is inactive — nothing is armed, and an open order is cancelled");
            Check(sched.Contains("if (delay > 1440)", StringComparison.Ordinal),
                "F-M445: the range is capped at 1440 minutes");
            Check(sched.Contains("public void CancelUploadPostprocessing(", StringComparison.Ordinal)
                  && upload.Contains("CancelUploadPostprocessing(\"upload run started\")", StringComparison.Ordinal),
                "F-M445: an open order is cancelled when the upload run starts");
            Check(upload.Contains("CancelUploadPostprocessing(\n                    $\"upload run stopped by the user", StringComparison.Ordinal),
                "F-M445: a user stop CANCELS the scheduled postprocessing run (operator order 02.11.2026)");
            Check(sched.Contains("public DateTime? GetUploadPostprocessDue()", StringComparison.Ordinal),
                "F-M445: the armed moment is readable for the status line");
            string ctrl = File.ReadAllText(Path.Combine(root, "Api", "SubdlStatusController.cs"));
            Check(ctrl.Contains("PostprocessDue = postprocessDue?", StringComparison.Ordinal)
                  && ctrl.Contains("workerFires.Remove(\"SubDLPostprocessTask\")", StringComparison.Ordinal),
                "F-M445: the moment rides on its own field — the worker row is not painted yellow forever");
            string page = File.ReadAllText(Path.Combine(root, "Configuration", "configPage.html"));
            Check(page.Contains("id=\"PostprocessDueLine\"", StringComparison.Ordinal)
                  && page.Contains("ppLine.style.display = ppWhen ? '' : 'none'", StringComparison.Ordinal),
                "F-M445: one line under the workers, gone once the run starts");
            Check(sched.Contains("_postprocessArmUtc.ToString(\"O\", CultureInfo.InvariantCulture)", StringComparison.Ordinal)
                  && sched.Contains("ParseInstant(fields, \"postprocessDefer\")", StringComparison.Ordinal),
                "F-M445: the armed moment is persisted under the old field name and read back");

            Console.WriteLine($"statefile: {_checks} checks, {(_failed == 0 ? "all green" : _failed + " failed")}.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
