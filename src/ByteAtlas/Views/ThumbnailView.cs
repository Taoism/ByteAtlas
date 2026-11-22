using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ByteAtlas.Controls;
using ByteAtlas.Model;

namespace ByteAtlas.Views;

/// <summary>Large Explorer thumbnails of the files and folders in the selected folder.</summary>
public sealed class ThumbnailView : UserControl
{
    private const int TileSize = 140;
    private const int MaxItems = 2000;
    private readonly ListBox _list;
    private readonly AppSettings _settings;

    public event EventHandler<Item>? Activated;
    public event EventHandler<Item?>? SelectionChanged;

    public sealed class Tile(Item item, string size)
    {
        public Item Item { get; } = item;
        public string Name => Item.Name;
        public string Size { get; } = size;

        public override string ToString() => Name;
    }

    public ThumbnailView(AppSettings settings)
    {
        _settings = settings;
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new FuncDataTemplate<Tile>((t, _) => MakeTile(t), false),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is Tile t)
                Activated?.Invoke(this, t.Item);
        };
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, (_list.SelectedItem as Tile)?.Item);
        Content = new Border { Classes = { "panel" }, Child = _list };
    }

    public new ContextMenu? ContextMenu
    {
        get => _list.ContextMenu;
        set => _list.ContextMenu = value;
    }

    public IReadOnlyList<Item> SelectedItems => [.. _list.SelectedItems?.OfType<Tile>().Select(t => t.Item) ?? []];

    public Control List => _list;

    public void Show(IEnumerable<Item> items) =>
        _list.ItemsSource = items.Take(MaxItems).Select(i => new Tile(i, _settings.Size(i.Size))).ToList();

    private static StackPanel MakeTile(Tile? t)
    {
        var image = new Image { Width = TileSize, Height = TileSize - 20, Stretch = Stretch.Uniform };
        var sp = new StackPanel { Width = TileSize, Spacing = 2, Margin = new Thickness(4) };
        sp.Children.Add(image);
        sp.Children.Add(new TextBlock { Text = t?.Name, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = t?.Size, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
        if (t != null)
        {
            ToolTip.SetTip(sp, t.Item.FullPath);
            _ = Load(image, t.Item.FullPath);
        }
        return sp;
    }

    private static async Task Load(Image image, string path)
    {
        var bmp = await ShellThumbnails.GetAsync(path, 256);
        if (bmp != null)
            image.Source = bmp;
    }
}
