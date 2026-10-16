using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ribbit.Geometry.Clash;
using Ribbit.Geometry.Grids;

namespace Ribbit.Native.Settings;

/// <summary>
/// Which grids belong to each scope box, and the settings saved against it.
///
/// Deliberately the SAME file and shape the Grid_v2.dyn Dynamo script wrote -
/// %APPDATA%\GridAdjuster\scope_sets.json, keyed by model title then by the
/// scope box's element id as text - so sets already set up in Dynamo are
/// picked up by this tool, and the other way round.
///
/// Kept outside the model on purpose. A grid's own Scope Box parameter would
/// be the real fix, but it is not view-specific: setting it changes the grid
/// in every view, for everyone. That is a modelling decision, not a side
/// effect of running this.
/// </summary>
internal static class GridScopeStore
{
    /// <summary>
    /// Set by the in-Revit tests to a scratch file, so they exercise the real
    /// read / write / merge code without touching the user's saved sets.
    /// </summary>
    internal static string? PathOverride { get; set; }

    public static string StorePath
    {
        get
        {
            if (!string.IsNullOrEmpty(PathOverride))
                return PathOverride!;
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(baseDir, "GridAdjuster", "scope_sets.json");
        }
    }

    /// <summary>
    /// (everything in the file, was it readable). No file is a perfectly good
    /// empty store; a file that is there and will not parse holds other
    /// models' sets that must not be written over.
    /// </summary>
    private static (JsonObject Data, bool Readable) ReadAll()
    {
        string path = StorePath;
        if (!File.Exists(path))
            return (new JsonObject(), true);

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o
                ? (o, true)
                : (new JsonObject(), false);
        }
        catch (Exception)
        {
            return (new JsonObject(), false);
        }
    }

    /// <summary>
    /// The store key for a model: its title with Revit's open-method suffixes
    /// ("_username", "_detached") removed, so one model's sets follow it
    /// whether it is opened as a local, detached, or the central itself - the
    /// same fix the clash ledgers needed (<see cref="ModelTitle"/>).
    /// </summary>
    public static string KeyFor(string title, string? username)
    {
        string key = ModelTitle.Normalize(title, username);
        return key.Length > 0 ? key : title;
    }

    /// <summary>
    /// {scope box id as text: preset} for this model. An unreadable store is
    /// an empty one - the sets are a convenience, never worth stopping over.
    ///
    /// Read under the normalized key first, then under every variant of it -
    /// the raw local / detached title the Dynamo graph wrote under - each only
    /// filling in scope boxes not already found. So sets made in Dynamo, or
    /// under one way of opening the model, are not lost to another.
    /// </summary>
    public static Dictionary<string, GridPreset> Load(string title, string? username)
    {
        Dictionary<string, GridPreset> sets = [];
        string key = KeyFor(title, username);
        JsonObject data = ReadAll().Data;

        List<string> keys = [key];
        keys.AddRange(data.Select(kv => kv.Key)
            .Where(k => k != key && ModelTitle.Normalize(k, username) == key)
            .OrderBy(k => k == title ? 0 : 1)
            .ThenBy(k => k, StringComparer.Ordinal));

        foreach (string k in keys)
        {
            if (data[k] is not JsonObject mine)
                continue;
            foreach (KeyValuePair<string, JsonNode?> kv in mine)
            {
                if (kv.Value is JsonObject entry && !sets.ContainsKey(kv.Key))
                    sets[kv.Key] = FromJson(entry);
            }
        }

        return sets;
    }

    /// <summary>
    /// True when it reached disk, with every other model's sets intact.
    /// Written under the normalized key; variants are left as they are.
    /// </summary>
    public static bool Save(string title, string? username, IReadOnlyDictionary<string, GridPreset> sets)
    {
        string modelKey = KeyFor(title, username);
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            (JsonObject data, bool readable) = ReadAll();
            if (!readable)
            {
                // Moved aside and kept, never destroyed.
                File.Copy(path, path + ".unreadable", overwrite: true);
                data = new JsonObject();
            }

            JsonObject mine = [];
            foreach (KeyValuePair<string, GridPreset> kv in sets)
                mine[kv.Key] = ToJson(kv.Value);
            data[modelKey] = mine;

            // Written alongside under a UNIQUE temp name and swapped in, so a
            // crash cannot leave a half-written store, and two Revit sessions
            // saving at once cannot write into the same temp file.
            string temporary = AtomicFile.TempPathFor(path);
            File.WriteAllText(temporary,
                data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AtomicFile.Commit(temporary, path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static GridPreset FromJson(JsonObject o)
    {
        GridPreset p = new()
        {
            Name = Str(o["name"]) ?? "",
            Grids = [.. (o["grids"] as JsonArray ?? []).Select(Str).OfType<string>().Where(s => s.Length > 0)],
        };

        // Fewer than three steps is not a usable list (GridSteps.With ignores
        // it), so it must not make the entry read as "its saved settings".
        if (o["steps"] is JsonArray { Count: >= 3 } steps)
            p.Steps = [.. steps.Select(Bool)];
        if (o["offsets"] is JsonArray { Count: 4 } offsets)
            p.Offsets = [.. offsets.Select(n => Num(n) ?? 0.0)];
        if (o["bubbles"] is JsonArray { Count: 2 } bubbles)
            p.Bubbles = [.. bubbles.Select(Bool)];

        // Any saved gap, 0 included: the engine uses it as it is (a 0 turns
        // STEP 4 off for that scope box, with a warning). Only putting it
        // back on SCREEN skips a 0 - see GridAdjustWindow.ApplyPreset.
        if (o["gap"] is not null)
            p.GapMm = Num(o["gap"]);

        return p;
    }

    private static JsonObject ToJson(GridPreset p)
    {
        JsonObject o = new()
        {
            ["name"] = p.Name,
            ["grids"] = new JsonArray([.. p.Grids.Select(g => (JsonNode?)JsonValue.Create(g))]),
        };
        if (p.Steps is not null)
            o["steps"] = new JsonArray([.. p.Steps.Select(b => (JsonNode?)JsonValue.Create(b))]);
        if (p.Offsets is not null)
            o["offsets"] = new JsonArray([.. p.Offsets.Select(d => (JsonNode?)JsonValue.Create(d))]);
        if (p.Bubbles is not null)
            o["bubbles"] = new JsonArray([.. p.Bubbles.Select(b => (JsonNode?)JsonValue.Create(b))]);
        if (p.GapMm is double gap)
            o["gap"] = gap;
        return o;
    }

    private static string? Str(JsonNode? n)
    {
        try { return n?.GetValue<string>(); }
        catch (Exception) { return n?.ToString(); }
    }

    private static bool Bool(JsonNode? n)
    {
        if (n is null)
            return false;
        try { return n.GetValue<bool>(); }
        catch (Exception) { }
        double? d = Num(n);
        if (d is not null)
            return d.Value != 0;
        string s = (n.ToString() ?? "").Trim().ToLowerInvariant();
        return s is "true" or "1" or "yes";
    }

    private static double? Num(JsonNode? n)
    {
        if (n is null)
            return null;
        try { return n.GetValue<double>(); }
        catch (Exception) { }
        return GridMath.ParseMm(n.ToString());
    }
}
