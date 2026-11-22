using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using ByteAtlas.Controls;
using ByteAtlas.Security;

namespace ByteAtlas.Dialogs;

/// <summary>Who may do what with a file or folder: owner, inheritance, access rules, granular rights.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PermissionsDialog : DialogWindow
{
    public PermissionsDialog(string path) : base("Permissions", 860)
    {
        var info = Permissions.Read(path);
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = info.Entries, SelectionMode = DataGridSelectionMode.Single };
        grid.Columns.Add(new DataGridTextColumn { Header = "User or group", Binding = new Binding(nameof(AccessEntry.Identity)), Width = new DataGridLength(240) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(AccessEntry.Type)), Width = new DataGridLength(70) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Access", Binding = new Binding(nameof(AccessEntry.RightsText)), Width = new DataGridLength(150) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Inherited", Binding = new Binding(nameof(AccessEntry.InheritedText)), Width = new DataGridLength(95) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Applies to", Binding = new Binding(nameof(AccessEntry.AppliesTo)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Resizing.RememberColumns(grid, "permissions");

        var granular = new StackPanel { Spacing = 2 };
        var advanced = new CheckBox { Content = "Show advanced permissions of the selected entry" };
        var advBox = new Border { Classes = { "card" }, Child = granular, IsVisible = false };
        void ShowGranular()
        {
            granular.Children.Clear();
            if (grid.SelectedItem is not AccessEntry e)
            {
                granular.Children.Add(Label("Select an entry above.", true));
                return;
            }
            foreach (var (right, name) in PermissionsGranular())
            {
                var on = (e.Rights & right) == right;
                granular.Children.Add(new CheckBox { Content = name, IsChecked = on, IsHitTestVisible = false, Focusable = false });
            }
        }
        advanced.IsCheckedChanged += (_, _) =>
        {
            advBox.IsVisible = advanced.IsChecked == true;
            ShowGranular();
        };
        grid.SelectionChanged += (_, _) => ShowGranular();
        if (info.Entries.Count > 0)
            grid.SelectedIndex = 0;

        var head = Column(4,
            new SelectableTextBlock { Text = path, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            Label($"Owner: {info.Owner}"),
            Label($"Permissions: {info.InheritanceText}{(info.InheritanceProtected ? "  ·  inheritance from the parent folder is switched off" : "")}", true));
        if (info.Error != null)
            head.Children.Add(Label(info.Error, true));

        var dock = new DockPanel();
        head.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        var bottom = Column(8, advanced, advBox);
        bottom.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(grid);
        SetBody(dock, "Close", null);
        Resizing.Remember(this, "permissions", 860, 620, 520, 360);
        var props = MainWindow.ToolButton("Windows security dialog…", () => Shell.Properties(path));
        props.HorizontalAlignment = HorizontalAlignment.Left;
        bottom.Children.Add(props);
    }

    private static IEnumerable<(System.Security.AccessControl.FileSystemRights Right, string Name)> PermissionsGranular()
    {
        yield return (System.Security.AccessControl.FileSystemRights.ReadData, "List folder / read data");
        yield return (System.Security.AccessControl.FileSystemRights.WriteData, "Create files / write data");
        yield return (System.Security.AccessControl.FileSystemRights.AppendData, "Create folders / append data");
        yield return (System.Security.AccessControl.FileSystemRights.ReadExtendedAttributes, "Read extended attributes");
        yield return (System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes, "Write extended attributes");
        yield return (System.Security.AccessControl.FileSystemRights.ExecuteFile, "Traverse folder / execute file");
        yield return (System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, "Delete sub folders and files");
        yield return (System.Security.AccessControl.FileSystemRights.ReadAttributes, "Read attributes");
        yield return (System.Security.AccessControl.FileSystemRights.WriteAttributes, "Write attributes");
        yield return (System.Security.AccessControl.FileSystemRights.Delete, "Delete");
        yield return (System.Security.AccessControl.FileSystemRights.ReadPermissions, "Read permissions");
        yield return (System.Security.AccessControl.FileSystemRights.ChangePermissions, "Change permissions");
        yield return (System.Security.AccessControl.FileSystemRights.TakeOwnership, "Take ownership");
    }
}
