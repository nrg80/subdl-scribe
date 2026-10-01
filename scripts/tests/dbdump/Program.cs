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
