using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace ByteAtlas.Controls;

/// <summary>
/// A ComboBox whose choice can be set through UI automation: Avalonia's own peer offers a read-only Value pattern
/// and its items are not in the automation tree while closed. Setting the value selects the item with that text
/// (case-insensitive), or "#3" the third item.
/// </summary>
public sealed class AccessibleComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    internal void SelectByText(string value)
    {
        var items = Items.Cast<object?>().ToList();
        value = value.Trim();
        if (value.StartsWith('#') && int.TryParse(value[1..], out var n) && n >= 1 && n <= items.Count)
        {
            SelectedIndex = n - 1;
            return;
        }
        var i = items.FindIndex(x => string.Equals(x?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            i = items.FindIndex(x => x?.ToString()?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true);
        if (i < 0)
            throw new ArgumentException($"no item \"{value}\"; items: {string.Join(", ", items)}");
        SelectedIndex = i;
    }

    private sealed class Peer(AccessibleComboBox owner) : ComboBoxAutomationPeer(owner), IValueProvider
    {
        bool IValueProvider.IsReadOnly => false;
        string? IValueProvider.Value => owner.SelectedItem?.ToString();
        void IValueProvider.SetValue(string? value) => owner.SelectByText(value ?? "");
    }
}
