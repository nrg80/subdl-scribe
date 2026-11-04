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

            Console.WriteLine($"statefile: {_checks} checks, {(_failed == 0 ? "all green" : _failed + " failed")}.");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
