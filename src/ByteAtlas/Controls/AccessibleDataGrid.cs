using System.Collections;
using System.Globalization;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Automation.Peers;

namespace ByteAtlas.Controls;

/// <summary>
/// A DataGrid whose rows can be read and selected through UI automation. Avalonia's grid rows offer no
/// selection pattern, so the grid itself offers the Value pattern: reading it gives the selected rows
/// ("2 of 15 selected: 3, 5" plus the first row's text), setting it selects rows: "3,5" (1-based, as shown),
/// "3-7", "all" or "none". Screen readers announce the selection; tests select rows without mouse or keys.
/// </summary>
public sealed class AccessibleDataGrid : DataGrid
{
    protected override Type StyleKeyOverride => typeof(DataGrid);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Rows in shown order (sorting and grouping included).</summary>
    private List<object> Rows()
    {
        var list = new List<object>();
        if (CollectionView is IEnumerable view)
        {
            foreach (var x in view)
                list.Add(x);
        }
        else if (ItemsSource is IEnumerable items)
        {
            foreach (var x in items)
                list.Add(x);
        }
        return list;
    }

    internal string Describe()
    {
        var rows = Rows();
        var index = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++)
            index.TryAdd(rows[i], i + 1);
        var sel = SelectedItems.Cast<object>().Select(x => index.TryGetValue(x, out var n) ? n : 0).Where(n => n > 0).Order().ToList();
        var text = $"{sel.Count} of {rows.Count} selected" + (sel.Count > 0 ? ": " + string.Join(", ", sel.Take(50)) : "");
        if (SelectedItem is { } first)
            text += " | " + first;
        return text;
    }

    internal void Select(string value)
    {
        var rows = Rows();
        SelectedItems.Clear();
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            return;
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in rows)
                SelectedItems.Add(r);
            return;
        }
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from))
                throw new ArgumentException("rows are given as numbers: 3,5 or 3-7, all, none");
            var to = range.Length > 1 && int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : from;
            for (var i = Math.Max(1, from); i <= Math.Min(rows.Count, to); i++)
                SelectedItems.Add(rows[i - 1]);
        }
        if (SelectedItem != null)
            ScrollIntoView(SelectedItem, null);
    }

    private sealed class Peer(AccessibleDataGrid owner) : DataGridAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => false;
        public string Value => owner.Describe();
        public void SetValue(string? value) => owner.Select(value ?? "");
    }
}
