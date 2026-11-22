using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ByteAtlas.Model;

namespace ByteAtlas.Controls;

/// <summary>
/// A small drawn icon for a file or folder in the item lists. Folders are filled shapes in theme colours (dark and
/// light themes each have their own, no light outlines on dark); files are a page with a band in the colour of
/// their type (the same colour as in the charts).
/// </summary>
public sealed class ItemIcon : Control
{
    public static readonly StyledProperty<Item?> ItemProperty = AvaloniaProperty.Register<ItemIcon, Item?>(nameof(Item));

    static ItemIcon() => AffectsRender<ItemIcon>(ItemProperty);

    public ItemIcon()
    {
        Width = 16;
        Height = 16;
        // the theme can change while shown
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public Item? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>File groups for the type colours (the app's settings).</summary>
    public static Analysis.FileGroups Groups { get; set; } = new();

    public override void Render(DrawingContext ctx)
    {
        var item = Item;
        if (item == null)
            return;
        var cy = Bounds.Height / 2;
        if (item is DirNode)
            DrawFolder(ctx, this, 0, cy);
        else
            DrawFile(ctx, this, 1, cy, Palette.ForExtension(item.Extension, Groups));
    }

    /// <summary>A folder 16 px wide, centred on <paramref name="cy"/>: back with its tab, then the front.</summary>
    internal static void DrawFolder(DrawingContext ctx, Control theme, double x, double cy)
    {
        var back = new StreamGeometry();
        using (var c = back.Open())
        {
            c.BeginFigure(new Point(x + 1, cy - 6), true);
            c.LineTo(new Point(x + 6, cy - 6));
            c.LineTo(new Point(x + 8, cy - 4));
            c.LineTo(new Point(x + 15, cy - 4));
            c.LineTo(new Point(x + 15, cy + 6));
            c.LineTo(new Point(x + 1, cy + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(theme.ThemeBrush("FolderIconBackBrush", Brushes.DarkGoldenrod), null, back);
        ctx.FillRectangle(theme.ThemeBrush("FolderIconBrush", Brushes.Goldenrod), new Rect(x + 1, cy - 2, 14, 8), 1);
    }

    /// <summary>A page with a folded corner and a band in the type colour.</summary>
    internal static void DrawFile(DrawingContext ctx, Control theme, double x, double cy, Color type)
    {
        var page = new StreamGeometry();
        using (var c = page.Open())
        {
            c.BeginFigure(new Point(x + 2, cy - 7), true);
            c.LineTo(new Point(x + 9, cy - 7));
            c.LineTo(new Point(x + 13, cy - 3));
            c.LineTo(new Point(x + 13, cy + 7));
            c.LineTo(new Point(x + 2, cy + 7));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(theme.ThemeBrush("FileIconBrush", Brushes.DimGray), new Pen(theme.ThemeBrush("GlyphBrush", Brushes.Gray), 1), page);
        ctx.FillRectangle(new SolidColorBrush(type), new Rect(x + 3.5, cy + 1, 8, 4.5), 1);
    }
}
