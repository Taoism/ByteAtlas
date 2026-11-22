using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace ByteAtlas.Controls;

/// <summary>Drag files and folders out of a list into Explorer (copy, move or link, as Explorer decides).</summary>
internal static class DragOut
{
    /// <summary>True while a drag started here is running (our own window then ignores the drop).</summary>
    public static bool Active { get; private set; }

    public static void Attach(Control control, Func<IReadOnlyList<string>> paths)
    {
        PointerPressedEventArgs? pressed = null;
        Point start = default;
        var dragging = false;
        control.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            // not from column headers, scroll bars or splitters (those drag something else)
            var onChrome = e.Source is Visual v && (v.FindAncestorOfType<DataGridColumnHeader>(true) != null || v.FindAncestorOfType<ScrollBar>(true) != null || v.FindAncestorOfType<Thumb>(true) != null);
            if (!onChrome && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                pressed = e;
                start = e.GetPosition(control);
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerReleasedEvent, (_, _) => pressed = null, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        control.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (pressed == null || dragging || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
                return;
            var d = e.GetPosition(control) - start;
            if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8)
                return;
            var list = paths().Where(p => !Shell.IsRemote(p)).ToList();
            if (list.Count == 0 || TopLevel.GetTopLevel(control)?.StorageProvider is not { } sp)
                return;
            dragging = true;
            Active = true;
            var args = pressed;
            pressed = null;
            try
            {
                var data = new DataTransfer();
                foreach (var p in list.Take(2000))
                {
                    IStorageItem? item = Directory.Exists(p) ? await sp.TryGetFolderFromPathAsync(p) : await sp.TryGetFileFromPathAsync(p);
                    if (item != null)
                        data.Add(DataTransferItem.CreateFile(item));
                }
                if (data.Items.Count > 0)
                    await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            finally
            {
                dragging = false;
                Active = false;
            }
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
