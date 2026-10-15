using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LTOG.Core;

namespace LTOG;

/// <summary>One indexed tape of the catalog.</summary>
public sealed class CatalogTape
{
    public SchemaSnapshot Snapshot { get; init; } = null!;
    /// <summary>Volume name, else the recorded barcode, else the cartridge serial, else "Index #n".</summary>
    public string Label { get; init; } = "";
    public TapeRecord? Record => TapeDb.Get(Snapshot.VolumeUuid);
}

/// <summary>A file or folder of every index merged into one tree: a path found on several tapes is one node.</summary>
public sealed class CatalogNode
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "/";
    public bool IsDirectory { get; init; }
    public CatalogNode? Parent { get; init; }
    public Dictionary<string, CatalogNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every copy, first-written first.</summary>
    public List<(CatalogTape Tape, SchemaNode Node)> Copies { get; } = new();
    public long Size { get; private set; }            // folders: every file inside
    public DateTime? Modified { get; private set; }   // local time, of the first-written copy

    public string Location => Copies.Count > 0 ? Copies[0].Tape.Label : "";
    public string MoreLocations => Copies.Count > 1 ? $" +{Copies.Count - 1}" : "";
    public string AllLocations => string.Join("\n", Copies.Select(c => c.Tape.Label));

    public IEnumerable<CatalogNode> Descendants() =>
        Children.Values.SelectMany(c => c.Descendants().Prepend(c));

    /// <summary>Parse every snapshot, oldest first (that order numbers unlabelled ones).</summary>
    public static List<CatalogTape> Load(IEnumerable<FileInfo> files, IReadOnlyDictionary<string, TapeRecord> records)
    {
        var tapes = new List<CatalogTape>();
        int i = 0;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            i++;
            SchemaSnapshot s;
            try { s = SchemaParser.Parse(f); }
            catch { continue; }   // unreadable: the Tapes page flags it
            string name = s.Root.Name is "/" or "(empty index)" ? "" : s.Root.Name;   // LTFS root name = volume name
            var rec = records.GetValueOrDefault(s.VolumeUuid);
            string label = new[] { name, rec?.Barcode, rec?.Serial }.FirstOrDefault(l => !string.IsNullOrEmpty(l))
                           ?? $"Index #{i}";
            tapes.Add(new CatalogTape { Snapshot = s, Label = label });
        }
        return tapes;
    }

    /// <summary>Merge the tapes into one tree.</summary>
    public static CatalogNode Build(IEnumerable<CatalogTape> tapes)
    {
        var root = new CatalogNode { IsDirectory = true };
        foreach (var t in tapes) root.Merge(t, t.Snapshot.Root);
        root.Finish();
        return root;
    }

    private void Merge(CatalogTape tape, SchemaNode src)
    {
        Copies.Add((tape, src));
        foreach (var c in src.Children)
        {
            string key = (c.IsDirectory ? "d:" : "f:") + c.Name;   // a file and folder may share a name across tapes
            if (!Children.TryGetValue(key, out var n))
                Children[key] = n = new CatalogNode
                {
                    Name = c.Name, Path = Path.TrimEnd('/') + "/" + c.Name, IsDirectory = c.IsDirectory, Parent = this,
                };
            n.Merge(tape, c);
        }
    }

    private void Finish()
    {
        Copies.Sort((a, b) => (a.Node.CreatedUtc ?? DateTime.MaxValue).CompareTo(b.Node.CreatedUtc ?? DateTime.MaxValue));
        foreach (var c in Children.Values) c.Finish();
        Size = IsDirectory ? Children.Values.Sum(c => c.Size) : Copies[0].Node.Length ?? 0;
        Modified = Copies.Count > 0 ? Copies[0].Node.ModifiedUtc?.ToLocalTime() : null;   // empty root
    }

    public CatalogNode? Resolve(string path)
    {
        var n = this;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if ((n = n.Children.GetValueOrDefault("d:" + part)) == null) return null;
        return n;
    }
}

/// <summary>Search filter window state; <see cref="Matcher"/> turns it plus the query into a test.</summary>
public sealed class SearchOptions
{
    public bool Wildcards, AnyWord, CaseSensitive, UseRegex, IgnoreDiacritics, Partial = true, Fuzzy;
    public string Type = "";            // "", "files", "folders", or an extension like ".7z"
    public int DateOp, TimeOp, SizeOp;  // 0 = ignore; the rest index the filter window's lists
    public DateTime DateA = DateTime.Today, DateB = DateTime.Today;
    public double WithinDays = 7, WithinHours = 1;
    public TimeSpan TimeA = new(12, 0, 0), TimeB = new(13, 0, 0);
    public double SizeA = 1, SizeB = 10;
    public int SizeUnit = 2;            // power of 1024: 0 B .. 4 TiB

    public bool IsFiltering => Type != "" || DateOp + TimeOp + SizeOp > 0;
    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>Null when nothing filters, i.e. every node shows.</summary>
    public Func<CatalogNode, bool>? Matcher(string query)
    {
        var tests = new List<Func<CatalogNode, bool>>();
        if (query.Trim().Length > 0)
        {
            var name = NameTest(query);
            tests.Add(n => name(n.Name));
        }
        string type = Type;
        if (type != "")
            tests.Add(type switch
            {
                "files" => n => !n.IsDirectory,
                "folders" => n => n.IsDirectory,
                _ => n => !n.IsDirectory && System.IO.Path.GetExtension(n.Name).Equals(type, StringComparison.OrdinalIgnoreCase),
            });

        var (da, db) = DateA <= DateB ? (DateA.Date, DateB.Date) : (DateB.Date, DateA.Date);
        int dateOp = DateOp;
        double days = WithinDays;
        if (dateOp > 0)
            tests.Add(n => n.Modified is { } m && dateOp switch
            {
                1 => m.Date == da,
                2 => m.Date < da,
                3 => m.Date > da,
                4 => m.Date >= da && m.Date <= db,
                5 => m.Date < da || m.Date > db,
                _ => m >= DateTime.Now.AddDays(-days),
            });

        var (ta, tb) = TimeA <= TimeB ? (TimeA, TimeB) : (TimeB, TimeA);
        int timeOp = TimeOp;
        double hours = WithinHours;
        static bool Near(TimeSpan t, TimeSpan a, double h) { var d = Math.Abs((t - a).TotalHours); return Math.Min(d, 24 - d) <= h; }
        if (timeOp > 0)
            tests.Add(n => n.Modified is { } m && m.TimeOfDay is var t && timeOp switch
            {
                1 => t.Hours == ta.Hours && t.Minutes == ta.Minutes,
                2 => t < ta,
                3 => t > ta,
                4 => t >= ta && t <= tb,
                5 => t < ta || t > tb,
                6 => Near(t, ta, 1),
                7 => Near(t, ta, 6),
                _ => m >= DateTime.Now.AddHours(-hours),
            });

        double unit = Math.Pow(1024, SizeUnit), sa = Math.Min(SizeA, SizeB) * unit, sb = Math.Max(SizeA, SizeB) * unit;
        double eq = SizeA;
        int sizeOp = SizeOp;
        if (sizeOp > 0)
            tests.Add(n => (double)n.Size is var s && sizeOp switch
            {
                1 => Math.Round(s / unit, 2) == Math.Round(eq, 2),   // as displayed
                2 => s < eq * unit,
                3 => s > eq * unit,
                4 => s >= sa && s <= sb,
                5 => s < sa || s > sb,
                6 => Math.Abs(s - eq * unit) <= eq * unit * 0.25,
                _ => Math.Abs(s - eq * unit) <= eq * unit * 0.5,
            });

        return tests.Count == 0 ? null : n => tests.All(t => t(n));
    }

    private Func<string, bool> NameTest(string query)
    {
        bool ci = !CaseSensitive, strip = IgnoreDiacritics, regex = UseRegex, wild = Wildcards, partial = Partial, fuzzy = Fuzzy;
        var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Norm(string s) => strip ? new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()) : s;
        var terms = (AnyWord ? query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [query.Trim()])
            .Select(t => Term(Norm(t))).ToList();
        return name => { name = Norm(name); return terms.Any(t => t(name)); };

        Func<string, bool> Term(string t)
        {
            if (regex || wild)
            {
                string pattern = regex ? t : Regex.Escape(t).Replace(@"\*", ".*").Replace(@"\?", ".");
                if (!regex && !partial) pattern = $"^{pattern}$";
                try
                {
                    var r = new Regex(pattern, (ci ? RegexOptions.IgnoreCase : 0) | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(50));
                    return s => { try { return r.IsMatch(s); } catch (RegexMatchTimeoutException) { return false; } };
                }
                catch (ArgumentException) { return _ => false; }   // half-typed pattern
            }
            if (fuzzy)   // every character, in order
                return s =>
                {
                    int i = 0;
                    foreach (var ch in s)
                        if (i < t.Length && (ci ? char.ToUpperInvariant(ch) == char.ToUpperInvariant(t[i]) : ch == t[i])) i++;
                    return i == t.Length;
                };
            return partial ? s => s.Contains(t, cmp) : s => s.Equals(t, cmp);
        }
    }

#if DEBUG
    static SearchOptions()
    {
        static bool M(SearchOptions o, string q, string name) => o.NameTest(q)(name);
        Debug.Assert(M(new(), "ab", "xABy"));
        Debug.Assert(!M(new() { Partial = false }, "ab", "xaby"));
        Debug.Assert(!M(new() { CaseSensitive = true }, "ab", "xABy"));
        Debug.Assert(M(new() { Wildcards = true, Partial = false }, "*.7z", "a.7z"));
        Debug.Assert(!M(new() { Wildcards = true, Partial = false }, "*.7z", "a.7z.bak"));
        Debug.Assert(M(new() { UseRegex = true }, @"\d{3}", "file123"));
        Debug.Assert(!M(new() { UseRegex = true }, "(", "("));
        Debug.Assert(M(new() { Fuzzy = true }, "fl7", "file lists.7z"));
        Debug.Assert(M(new() { IgnoreDiacritics = true }, "cafe", "Café"));
        Debug.Assert(M(new() { AnyWord = true }, "zzz lists", "file lists.7z"));
        Debug.Assert(!M(new(), "zzz lists", "file lists.7z"));
    }
#endif
}
