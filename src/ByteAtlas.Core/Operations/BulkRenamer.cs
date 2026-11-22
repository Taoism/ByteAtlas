using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ByteAtlas.Operations;

public enum RenameRuleType
{
    Replace,
    Insert,
    Serialize,
    Delete,
    Case,
}

public enum RenameScope
{
    NameOnly,
    ExtensionOnly,
    FullName,
}

public enum InsertWhere
{
    Prefix,
    Suffix,
    Position,
}

public enum CaseMode
{
    Upper,
    Lower,
    FirstLetterUpper,
}

/// <summary>One renaming rule; rules apply top to bottom.</summary>
public sealed class RenameRule
{
    public RenameRuleType Type { get; set; }
    public bool Active { get; set; } = true;
    public RenameScope Scope { get; set; } = RenameScope.NameOnly;

    // Replace
    public string Search { get; set; } = "";
    public string ReplaceWith { get; set; } = "";
    public bool UseRegex { get; set; }

    // Insert / Serialize
    public string Text { get; set; } = "";
    public InsertWhere Where { get; set; } = InsertWhere.Prefix;
    public int Position { get; set; }
    public bool FromRight { get; set; }
    public int StartValue { get; set; } = 1;
    public int PadLength { get; set; }

    // Delete: Text, or Count characters from Position
    public int Count { get; set; }

    public CaseMode Case { get; set; }

    [JsonIgnore]
    public string Summary => Type switch
    {
        RenameRuleType.Replace => $"Replace \"{Search}\" with \"{ReplaceWith}\"{(UseRegex ? " (regex)" : "")}",
        RenameRuleType.Insert => $"Insert \"{Text}\" {WhereText}",
        RenameRuleType.Serialize => $"Numbers from {StartValue}{(PadLength > 0 ? $", {PadLength} digits" : "")} {WhereText}",
        RenameRuleType.Delete => Text.Length > 0 ? $"Delete \"{Text}\"" : $"Delete {Count} characters from position {Position}{(FromRight ? " (from the right)" : "")}",
        _ => Case switch
        {
            CaseMode.Upper => "To upper case",
            CaseMode.Lower => "To lower case",
            _ => "First letter upper case",
        },
    } + Scope switch
    {
        RenameScope.ExtensionOnly => " — extension",
        RenameScope.FullName => " — full name",
        _ => "",
    };

    private string WhereText => Where switch
    {
        InsertWhere.Prefix => "at the start",
        InsertWhere.Suffix => "at the end",
        _ => $"at position {Position}{(FromRight ? " from the right" : "")}",
    };
}

public sealed record RenamePreview(string OriginalPath, string NewName)
{
    public string OriginalName => Path.GetFileName(OriginalPath);
    public string NewPath => Path.Combine(Path.GetDirectoryName(OriginalPath) ?? "", NewName);
    public bool Changed => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);
    public string? Problem { get; init; }
}

/// <summary>Bulk renaming with an ordered rule set, preview and undo.</summary>
public sealed class BulkRenamer
{
    public List<RenameRule> Rules { get; set; } = [];

    private List<(string From, string To)> _lastRun = [];

    public bool CanUndo => _lastRun.Count > 0;

    public List<RenamePreview> Preview(IReadOnlyList<string> paths)
    {
        var list = new List<RenamePreview>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < paths.Count; i++)
        {
            var name = Path.GetFileName(paths[i]);
            var isDir = Directory.Exists(paths[i]);
            var n = Apply(name, i, isDir);
            string? problem = null;
            if (n.Length == 0 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problem = "Invalid name";
            var newPath = Path.Combine(Path.GetDirectoryName(paths[i]) ?? "", n);
            if (problem == null && !seen.Add(newPath))
                problem = "Name used twice";
            else if (problem == null && !string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && (File.Exists(newPath) || Directory.Exists(newPath)))
                problem = "Exists already";
            list.Add(new RenamePreview(paths[i], n) { Problem = problem });
        }
        return list;
    }

    public string Apply(string fileName, int index, bool isFolder = false)
    {
        var name = fileName;
        foreach (var r in Rules)
        {
            if (!r.Active)
                continue;
            var ext = isFolder ? "" : Path.GetExtension(name);
            var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
            switch (r.Scope)
            {
                case RenameScope.FullName:
                    name = ApplyRule(r, name, index);
                    break;
                case RenameScope.ExtensionOnly:
                    if (ext.Length > 0)
                        name = stem + "." + ApplyRule(r, ext[1..], index);
                    break;
                default:
                    name = ApplyRule(r, stem, index) + ext;
                    break;
            }
        }
        return name;
    }

    private static string ApplyRule(RenameRule r, string s, int index)
    {
        switch (r.Type)
        {
            case RenameRuleType.Replace:
                if (r.Search.Length == 0)
                    return s;
                if (r.UseRegex)
                {
                    try
                    {
                        return Regex.Replace(s, r.Search, r.ReplaceWith, RegexOptions.None, TimeSpan.FromMilliseconds(200));
                    }
                    catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
                    {
                        return s;
                    }
                }
                return s.Replace(r.Search, r.ReplaceWith, StringComparison.OrdinalIgnoreCase);
            case RenameRuleType.Insert:
                return Insert(s, r.Text, r);
            case RenameRuleType.Serialize:
                var num = (r.StartValue + index).ToString(CultureInfo.InvariantCulture);
                if (r.PadLength > 0)
                    num = num.PadLeft(r.PadLength, '0');
                return Insert(s, num, r);
            case RenameRuleType.Delete:
                if (r.Text.Length > 0)
                    return s.Replace(r.Text, "", StringComparison.OrdinalIgnoreCase);
                var start = r.FromRight ? s.Length - r.Position - r.Count : r.Position;
                start = Math.Clamp(start, 0, s.Length);
                var count = Math.Clamp(r.Count, 0, s.Length - start);
                return s.Remove(start, count);
            default:
                return r.Case switch
                {
                    CaseMode.Upper => s.ToUpperInvariant(),
                    CaseMode.Lower => s.ToLowerInvariant(),
                    _ => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant(),
                };
        }
    }

    private static string Insert(string s, string text, RenameRule r) => r.Where switch
    {
        InsertWhere.Prefix => text + s,
        InsertWhere.Suffix => s + text,
        _ => s.Insert(Math.Clamp(r.FromRight ? s.Length - r.Position : r.Position, 0, s.Length), text),
    };

    /// <summary>Renames everything in the preview that changed and has no problem.</summary>
    public OperationResult Execute(IEnumerable<RenamePreview> preview)
    {
        var errors = new List<string>();
        var run = new List<(string, string)>();
        foreach (var p in preview)
        {
            if (!p.Changed || p.Problem != null)
                continue;
            try
            {
                if (Directory.Exists(p.OriginalPath))
                    Directory.Move(p.OriginalPath, p.NewPath);
                else
                    File.Move(p.OriginalPath, p.NewPath);
                run.Add((p.OriginalPath, p.NewPath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{p.OriginalPath}: {e.Message}");
            }
        }
        _lastRun = run;
        return new OperationResult(run.Count, errors.Count, errors);
    }

    public OperationResult Undo()
    {
        var errors = new List<string>();
        var done = 0;
        for (var i = _lastRun.Count - 1; i >= 0; i--)
        {
            var (from, to) = _lastRun[i];
            try
            {
                if (Directory.Exists(to))
                    Directory.Move(to, from);
                else
                    File.Move(to, from);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{to}: {e.Message}");
            }
        }
        _lastRun = [];
        return new OperationResult(done, errors.Count, errors);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public void SaveRules(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Rules, Json));

    public void LoadRules(string path) => Rules = JsonSerializer.Deserialize<List<RenameRule>>(File.ReadAllText(path), Json) ?? [];
}
