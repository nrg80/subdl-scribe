// This file is part of SubDL Scribe (https://github.com/nrg80/subdl-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// SubDL Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// SubDL Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using LiteDB;

// Usage: dbdump <db> <collection> <contains>
var path = args[0];
var collection = args[1];
var needle = args.Length > 2 ? args[2] : "";

using var db = new LiteDatabase($"Filename={path};ReadOnly=true");
var col = db.GetCollection(collection);
var n = 0;
foreach (var doc in col.FindAll())
{
    var s = doc.ToString();
    if (needle.Length > 0 && !s.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
    n++;
    Console.WriteLine("=== doc " + n + " (id=" + doc["_id"].ToString() + ") ===");
    foreach (var kv in doc)
    {
        if (kv.Key == "_id") continue;
        Console.WriteLine($"  {kv.Key} = {kv.Value}");
    }
}
Console.WriteLine($"--- {n} document(s) matched in '{collection}' ---");
