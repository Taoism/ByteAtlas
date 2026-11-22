using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using ByteAtlas.Analysis;
using ByteAtlas.Model;
using ByteAtlas.Views;

namespace ByteAtlas;

/// <summary>Extra Details columns: the size of one file type, file group, owner or age interval inside each folder.</summary>
public sealed partial class MainWindow
{
    private int _extraVersion;

    private static string ExtraKey(AppSettings.ExtraColumn c) => c.Kind + ":" + c.Key;

    private void AddExtraColumn(string kind, string key, string label)
    {
        if (_settings.DetailExtraColumns.Exists(c => c.Kind == kind && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
            return;
        _settings.DetailExtraColumns.Add(new AppSettings.ExtraColumn { Kind = kind, Key = key, Label = label });
        RebuildDetailColumns();
        SetStatus($"Column \"{label}\" added to the Details list.");
    }

    private void AddExtraColumns()
    {
        foreach (var c in _settings.DetailExtraColumns)
        {
            var key = ExtraKey(c);
            _details.Columns.Add(new DataGridTemplateColumn
            {
                Header = c.Label,
                Width = new DataGridLength(110),
                CellTemplate = new FuncDataTemplate<ItemRow>((r, _) => new TextBlock
                {
                    Text = r != null && r.Extra.TryGetValue(key, out var v) ? (v > 0 ? _settings.Size(v) : "") : "…",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Avalonia.Thickness(0, 0, 8, 0),
                }),
                CustomSortComparer = Comparer<object>.Create((a, b) =>
                    (a is ItemRow x ? x.Extra.GetValueOrDefault(key) : 0).CompareTo(b is ItemRow y ? y.Extra.GetValueOrDefault(key) : 0)),
            });
        }
    }

    /// <summary>Computes the extra column values in the background, then shows them.</summary>
    private void FillExtraColumns(List<ItemRow> rows)
    {
        var cols = _settings.DetailExtraColumns.ToList();
        if (cols.Count == 0 || rows.Count == 0)
            return;
        var version = ++_extraVersion;
        var groups = _settings.FileGroupSet;
        var ages = _settings.AgeIntervalSet;
        var basis = _settings.AgeBasis;
        var now = DateTime.Now;
        _ = Task.Run(() =>
        {
            var tests = cols.Select(c => (Key: ExtraKey(c), Test: Matcher(c, groups, ages, basis, now))).ToList();
            foreach (var row in rows)
            {
                if (version != _extraVersion)
                    return;
                var sums = new long[tests.Count];
                IEnumerable<FileItem> files = row.Item is DirNode d ? d.AllFiles() : [(FileItem)row.Item];
                foreach (var f in files)
                    for (var k = 0; k < tests.Count; k++)
                        if (tests[k].Test(f, row.Scan))
                            sums[k] += f.Size;
                for (var k = 0; k < tests.Count; k++)
                    row.Extra[tests[k].Key] = sums[k];
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version == _extraVersion && ReferenceEquals(_details.ItemsSource, rows))
                    _details.ItemsSource = rows.ToList(); // new list: cells are rebuilt with the values
            });
        }, CancellationToken.None);
    }

    private static Func<FileItem, Scanning.ScanSession?, bool> Matcher(AppSettings.ExtraColumn c, FileGroups groups, AgeIntervals ages, AgeBasis basis, DateTime now)
    {
        switch (c.Kind)
        {
            case "ext":
                return (f, _) => string.Equals(f.Extension, c.Key, StringComparison.OrdinalIgnoreCase);
            case "group":
                return (f, _) => groups.GroupOf(f.Extension) == c.Key;
            case "owner":
                return (f, s) => s != null && string.Equals(f.OwnerId < 0 ? "(unknown)" : s.Owners.Name(f.OwnerId), c.Key, StringComparison.OrdinalIgnoreCase);
            default:
                var index = ages.Intervals.FindIndex(i => i.Label == c.Key);
                return (f, _) =>
                {
                    var age = now - (basis switch { AgeBasis.Accessed => f.Accessed, AgeBasis.Created => f.Created, _ => f.Modified });
                    var i = 0;
                    while (i < ages.Intervals.Count - 1 && age > ages.Intervals[i].MaxAge)
                        i++;
                    return i == index;
                };
        }
    }
}
