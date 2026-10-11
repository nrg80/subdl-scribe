#nullable enable
// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Test harness for F-M346 to F-M349. Run from the repo root:
//     dotnet run -c Release  (project: scripts/tests/namefallback)
// Exit code 0 = all checks green.

using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

int failures = 0, checks = 0;
void Check(string name, bool ok, string detail = null)
{
    checks++;
    Console.WriteLine(ok ? $"  OK   {name}" : $"  FAIL {name}{(detail is null ? "" : "  — " + detail)}");
    if (!ok) failures++;
}

const string PluginDll = "Jellyfin.Plugin.SubdlSync/bin/Release/net10.0/Jellyfin.Plugin.SubdlSync.dll";
string repo = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(repo, PluginDll)) && Path.GetDirectoryName(repo) is { Length: > 3 } up)
{
    repo = up;
}

var asm = Assembly.LoadFrom(Path.Combine(repo, PluginDll));
var parser = asm.GetType("Jellyfin.Plugin.SubdlScribe.Pipeline.MediaNameParser");
Check("the parser is reachable", parser != null);
var parse = parser!.GetMethod("Parse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
var extract = parser.GetMethod("ExtractImdbId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
Check("it parses and it can state an id", parse != null && extract != null);

object Parsed(string name) => parse!.Invoke(null, new object[] { name });
string Field(string name, string prop) => Parsed(name)!.GetType().GetProperty(prop)!.GetValue(Parsed(name))?.ToString();
string? Id(string name) => (string?)extract!.Invoke(null, new object[] { name });

Console.WriteLine("\nF-M346 — an id in the name");
Check("the canonical id is read", Id("The Counterfeiters 2007 tt0813547 sqi") == "tt0813547", Id("The Counterfeiters 2007 tt0813547 sqi"));
Check("also with dots and a container suffix", Id("Zombies.2018.tt6878820.sqi.1080p.WEB-DL.mkv") == "tt6878820");
Check("case-insensitive", Id("Zombies 2018 TT6878820 sqi") == "tt6878820");
Check("no id → null", Id("MobLand S02E04 Blank Curtain 1080p") is null);
Check("a six-digit id is not one", Id("Film 2020 tt123456 sqi") is null);
Check("the id never ends up in the title", Field("Zombies 2018 tt6878820 sqi", "Title") == "Zombies", Field("Zombies 2018 tt6878820 sqi", "Title"));

Console.WriteLine("\nF-M348 — leading bracket labels");
Check("a leading label is stripped", Field("[NETFLIX] Ranma ½ (2024) 3rd Season", "Title") == "Ranma ½", Field("[NETFLIX] Ranma ½ (2024) 3rd Season", "Title"));
Check("two of them in a row too", Field("[Crunchyroll] [Sub] Some Show S01E02", "Title") == "Some Show", Field("[Crunchyroll] [Sub] Some Show S01E02", "Title"));
Check("a bracket inside the title survives", Field("Rambo (2008) [Extended] 1080p", "Title") == "Rambo", Field("Rambo (2008) [Extended] 1080p", "Title"));

Console.WriteLine("\nF-M349 — dressed-up letters");
// Whether normalization does anything depends on the runtime: it is a globalization feature, and a
// runtime without ICU (System.Globalization.Invariant) returns the string unchanged. Jellyfin ships
// with ICU, so the plugin folds; this harness may not be able to. It says so instead of failing.
bool globalization = "\uFB01".Normalize(System.Text.NormalizationForm.FormKD) == "fi"; // U+FB01 is the ﬁ ligature — it only folds where ICU is present
string dressed = "[\U0001D41C\U0001D405\U0001D429\U0001D42D] \U0001D403\U0001D400\U0001D405\U0001D413 \U0001D412\U0001D413\U0001D400\U0001D413\U0001D404 (\U0001D7DA\U0001D7D8\U0001D7DA\U0001D7DC) \U0001D40F\U0001D411\U0001D408\U0001D40C\U0001D404 - \U0001D413\U0001D41B\U0001D415";
if (globalization)
{
    Check("bold letters fold to a readable title", Field(dressed, "Title") == "DAFT STATE", Field(dressed, "Title"));
    Check("and the bold year becomes a year", Field(dressed, "Year") == "2024", Field(dressed, "Year"));
}
else
{
    Console.WriteLine("  SKIP fold checks — this runtime has no ICU, normalization is a no-op here");
}
Check("the gate stays on dressed-up input only", Regex.Replace(File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "MediaNameParser.cs")), @"\s+", " ").Contains("if (NeedsUnicodeFold(fileName))"));
Check("an ordinary name is untouched", Field("Ein ganz normaler Name 2019", "Title") == "Ein ganz normaler Name");
Check("its year too", Field("Ein ganz normaler Name 2019", "Year") == "2019");

Console.WriteLine("\nWiring — the fallback confirms instead of guessing");
string dl = File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs"));
string rs = File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Api", "TmdbImdbResolver.cs"));
string pl = File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "MediaNameParser.cs"));
string flat = Regex.Replace(dl, @"\s+", " ");
Check("the series route asks the episode", flat.Contains("ResolveSeriesByEpisodeAsync"));
Check("a film with a year goes the strict way", flat.Contains("ResolveByTitleYearAsync(fnTitle!"));
Check("the id from the name is confirmed", flat.Contains("ResolveTmdbByImdbAsync(fnImdbFromName"));
Check("a contradiction is refused and logged", flat.Contains("neither is trusted"));
Check("the loose first-hit route is gone from the fallback", !flat.Contains("ResolveImdbByTitleAsync(fnTitle"));
Check("an unusable title is refused", flat.Contains("fnTitleUsable"));
Check("the episode method tests the episode itself", Regex.Replace(rs, @"\s+", " ").Contains("tv/{candidateShow}/season/{season}/episode/{episode}"));
Check("the fold is gated on dressed-up characters", Regex.Replace(pl, @"\s+", " ").Contains("if (NeedsUnicodeFold(fileName))"));

Console.WriteLine($"\n{checks} checks, {failures} failed");
Console.WriteLine(failures == 0 ? ">>> NAME-FALLBACK OK" : ">>> NAME-FALLBACK FAILED");
return failures == 0 ? 0 : 1;
