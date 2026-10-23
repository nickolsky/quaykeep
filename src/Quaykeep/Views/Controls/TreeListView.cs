using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quaykeep.ViewModels;

namespace Quaykeep.Views.Controls;

/// <summary>A column of <see cref="TreeListView"/>: header text, width (resizable in the header) and the cell template.</summary>
public sealed class TreeListColumn : AvaloniaObject
{
    public static readonly StyledProperty<string?> HeaderProperty = AvaloniaProperty.Register<TreeListColumn, string?>(nameof(Header));
    public static readonly StyledProperty<double> WidthProperty = AvaloniaProperty.Register<TreeListColumn, double>(nameof(Width), 120);

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Width in pixels; the last column ignores it and fills the rest.</summary>
    public double Width
    {
        get => GetValue(WidthProperty);
        set => SetValue(WidthProperty, value);
    }

    public double MinWidth { get; set; } = 30;

    /// <summary>The cell; its DataContext is the row's <see cref="TreeNode"/>.</summary>
    public IDataTemplate? CellTemplate { get; set; }

    /// <summary>The width it takes now (the last column: the width that fills the view).</summary>
    public double ActualWidth { get; internal set; }
}

/// <summary>
/// A tree laid out in resizable columns with a header row. The visible nodes (roots, and the children of expanded
/// nodes) are flattened into one virtualized list, so thousands of rows stay fast; the list follows the nodes'
/// Children collections and IsExpanded / IsSelected. The last column stretches to fill the remaining width; columns
/// wider than the view scroll horizontally together with their header. (Avalonia's TreeDataGrid would do this, but it
/// is commercially licensed.)
/// </summary>
public sealed class TreeListView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<TreeListView, IEnumerable?>(nameof(ItemsSource));

    private readonly ObservableCollection<TreeNode> _rows = [];
    private readonly ListBox _list;
    private readonly HashSet<TreeNode> _watched = [];
    private readonly HashSet<INotifyCollectionChanged> _watchedLists = [];
    private INotifyCollectionChanged? _sourceList;
    private TreeNode? _selected;
    private bool _syncing;
    private bool _rebuildQueued;

    public TreeListView()
    {
        Columns.CollectionChanged += (_, e) =>
        {
            foreach (TreeListColumn c in e.NewItems ?? Array.Empty<TreeListColumn>())
                c.PropertyChanged += (_, a) =>
                {
                    if (a.Property == TreeListColumn.WidthProperty) RaiseLayoutChanged();
                };
            RebuildHeader();
        };

        _list = new ListBox
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<TreeNode>((_, _) => new TreeColumnsPanel(this, header: false), supportsRecycling: true),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.SelectionChanged += (_, _) =>
        {
            if (!_syncing) Select(_list.SelectedItem as TreeNode, scroll: false);
        };
        _list.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        _list.DoubleTapped += OnRowDoubleTapped;
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);

        Header = new TreeColumnsPanel(this, header: true) { Margin = new Thickness(0, 0, 0, 4) };
        var dock = new DockPanel();
        DockPanel.SetDock(Header, Dock.Top);
        dock.Children.Add(Header);
        dock.Children.Add(_list);
        // columns wider than the window scroll horizontally together with their header
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = dock,
            Focusable = false,
        };
        SizeChanged += (_, _) => RaiseLayoutChanged();
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public AvaloniaList<TreeListColumn> Columns { get; } = [];

    internal TreeColumnsPanel Header { get; }

    /// <summary>The selected node (follows the nodes' IsSelected both ways).</summary>
    public TreeNode? SelectedItem => _selected;

    /// <summary>Row types whose double click means "open" (raised as <see cref="RowDoubleClicked"/>), not "expand".</summary>
    public Func<TreeNode, bool>? DoubleClickOpens { get; set; }

    public event EventHandler<TreeNode?>? SelectedItemChanged;

    /// <summary>A double click on a row (not on its expander). Set Handled to stop the default expand / collapse.</summary>
    public event EventHandler<TreeRowEventArgs>? RowDoubleClicked;

    /// <summary>Column widths or the view's width changed: rows and header lay out again.</summary>
    internal event Action? LayoutChanged;

    public void Focus() => (_list.ContainerFromItem(_selected ?? (object)_rows.FirstOrDefault()!) as Control ?? _list).Focus();

    /// <summary>Pixels available to the columns: the view minus a margin for the vertical scroll bar.</summary>
    private double Available => Bounds.Width - 16;

    /// <summary>The width a column takes: its own, or (the last one) what fills the view, at least 120.</summary>
    internal double ColumnWidth(int index)
    {
        if (index < Columns.Count - 1) return Columns[index].ActualWidth = Math.Max(Columns[index].MinWidth, Columns[index].Width);
        var others = 0.0;
        for (var i = 0; i < Columns.Count - 1; i++) others += Math.Max(Columns[i].MinWidth, Columns[i].Width);
        return Columns[index].ActualWidth = Math.Max(120, Available - others);
    }

    private void RaiseLayoutChanged()
    {
        LayoutChanged?.Invoke();
        Header.InvalidateMeasure();
    }

    private void RebuildHeader()
    {
        Header.Children.Clear();
        for (var i = 0; i < Columns.Count; i++) Header.Children.Add(HeaderCell(Columns[i], last: i == Columns.Count - 1));
    }

    private Control HeaderCell(TreeListColumn column, bool last)
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Bind(TextBlock.TextProperty, column.GetObservable(TreeListColumn.HeaderProperty));
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var border = new Border { Padding = new Thickness(8, 5, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Child = text };
        border.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        border.PointerEntered += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        border.PointerExited += (_, _) => border.Background = Brushes.Transparent;
        var cell = new Panel { ClipToBounds = false };
        cell.Children.Add(border);
        if (last) return cell;
        var line = new Rectangle { Width = 1, Margin = new Thickness(0, 7) };
        line.SetResourceReference(Shape.FillProperty, "DividerStrokeColorDefaultBrush");
        var grip = new Thumb
        {
            Width = 9,
            Margin = new Thickness(0, 0, -4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brushes.Transparent, Child = line }),
        };
        grip.DragDelta += (_, e) => column.Width = Math.Max(column.MinWidth, column.ActualWidth + e.Vector.X);
        cell.Children.Add(grip);
        return cell;
    }

    // ---------- rows ----------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ItemsSourceProperty) return;
        if (_sourceList != null) _sourceList.CollectionChanged -= OnCollectionChanged;
        _sourceList = ItemsSource as INotifyCollectionChanged;
        if (_sourceList != null) _sourceList.CollectionChanged += OnCollectionChanged;
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TreeNode node) return;
        switch (e.PropertyName)
        {
            case nameof(TreeNode.IsExpanded):
                Rebuild();
                break;
            case nameof(TreeNode.IsSelected):
                if (_syncing) break;
                if (node.IsSelected && !ReferenceEquals(node, _selected))
                {
                    // selected by the view model (a new server, "go to peer"): show it
                    for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
                    Rebuild();
                    Select(node, scroll: true);
                }
                else if (!node.IsSelected && ReferenceEquals(node, _selected)) Select(null, scroll: false);
                break;
            case "" or null:
                QueueRebuild();
                break;
        }
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.UIThread.Post(Rebuild, DispatcherPriority.Background);
    }

    /// <summary>Recomputes the visible rows and updates the list with the fewest changes (keeps scroll position and containers).</summary>
    private void Rebuild()
    {
        _rebuildQueued = false;
        var visible = new List<TreeNode>();
        var all = new HashSet<TreeNode>();
        var lists = new HashSet<INotifyCollectionChanged>();
        void Walk(IEnumerable nodes, bool shown)
        {
            foreach (var n in nodes.OfType<TreeNode>())
            {
                all.Add(n);
                if (shown) visible.Add(n);
                lists.Add(n.Children);
                Walk(n.Children, shown && n.IsExpanded);
            }
        }
        if (ItemsSource != null) Walk(ItemsSource, true);

        foreach (var n in _watched.Where(n => !all.Contains(n)).ToList())
        {
            n.PropertyChanged -= OnNodeChanged;
            _watched.Remove(n);
        }
        foreach (var n in all.Where(_watched.Add)) n.PropertyChanged += OnNodeChanged;
        foreach (var l in _watchedLists.Where(l => !lists.Contains(l)).ToList())
        {
            l.CollectionChanged -= OnCollectionChanged;
            _watchedLists.Remove(l);
        }
        foreach (var l in lists.Where(_watchedLists.Add)) l.CollectionChanged += OnCollectionChanged;

        _syncing = true;
        try
        {
            for (var i = 0; i < visible.Count; i++)
            {
                if (i < _rows.Count && ReferenceEquals(_rows[i], visible[i])) continue;
                var j = -1;
                for (var k = i + 1; k < _rows.Count; k++)
                    if (ReferenceEquals(_rows[k], visible[i]))
                    {
                        j = k;
                        break;
                    }
                if (j > 0)
                    for (var k = j - 1; k >= i; k--) _rows.RemoveAt(k);
                else _rows.Insert(i, visible[i]);
            }
            while (_rows.Count > visible.Count) _rows.RemoveAt(_rows.Count - 1);
            _list.SelectedItem = _selected != null && _rows.Contains(_selected) ? _selected : null;
        }
        finally
        {
            _syncing = false;
        }

        // a node marked selected while it was not watched yet (the view model rebuilt the tree), or the old one is gone
        var wanted = all.FirstOrDefault(n => n.IsSelected && !ReferenceEquals(n, _selected)) ?? (_selected != null && all.Contains(_selected) ? _selected : null);
        if (!ReferenceEquals(wanted, _selected)) Select(wanted, scroll: true);
    }

    private void Select(TreeNode? node, bool scroll)
    {
        if (ReferenceEquals(_selected, node))
        {
            if (node != null && !ReferenceEquals(_list.SelectedItem, node) && _rows.Contains(node)) Sync(node);
            return;
        }
        var old = _selected;
        _selected = node;
        Sync(node);
        _syncing = true;
        try
        {
            if (old != null && _watched.Contains(old)) old.IsSelected = false;
            if (node != null) node.IsSelected = true;
        }
        finally
        {
            _syncing = false;
        }
        if (scroll && node != null && _rows.Contains(node)) _list.ScrollIntoView(node);
        SelectedItemChanged?.Invoke(this, node);
    }

    private void Sync(TreeNode? node)
    {
        _syncing = true;
        try
        {
            _list.SelectedItem = node != null && _rows.Contains(node) ? node : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------- mouse and keys ----------

    private static TreeNode? RowOf(object? source) =>
        (source as Visual)?.FindSelfOrAncestor<ListBoxItem>()?.DataContext as TreeNode;

    private static bool OnExpander(object? source)
    {
        for (var v = source as Visual; v != null && v is not ListBoxItem; v = v.GetVisualParent())
            if (v is ToggleButton) return true;
        return false;
    }

    /// <summary>Right click selects the row under the mouse, so the context menu acts on it.</summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed || RowOf(e.Source) is not { } node) return;
        Select(node, scroll: false);
        (_list.ContainerFromItem(node) as Control)?.Focus();
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OnExpander(e.Source) || RowOf(e.Source) is not { } node || !ReferenceEquals(node, _selected)) return;
        var args = new TreeRowEventArgs(node);
        RowDoubleClicked?.Invoke(this, args);
        if (!args.Handled && DoubleClickOpens?.Invoke(node) != true && node.CanExpand) node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }

    /// <summary>Right expands (or steps into the first child), Left collapses (or steps up to the parent), like a tree view.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (_selected is not { } node || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Right)
        {
            if (node.CanExpand && !node.IsExpanded) node.IsExpanded = true;
            else if (node.IsExpanded && node.Children.Count > 0) Select(node.Children[0], scroll: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (node.IsExpanded) node.IsExpanded = false;
            else if (node.Parent != null) Select(node.Parent, scroll: true);
            e.Handled = true;
        }
        if (e.Handled) (_list.ContainerFromItem(_selected!) as Control)?.Focus();
    }
}

public sealed class TreeRowEventArgs(TreeNode node) : EventArgs
{
    public TreeNode Node { get; } = node;
    public bool Handled { get; set; }
}

/// <summary>Lays its children out side by side in the widths of the owner's columns: the header cells, or one row's cells.</summary>
internal sealed class TreeColumnsPanel : Panel
{
    private readonly TreeListView _owner;
    private readonly bool _header;

    public TreeColumnsPanel(TreeListView owner, bool header)
    {
        _owner = owner;
        _header = header;
        ClipToBounds = false;
        if (header) return;
        Background = Brushes.Transparent;
        // each cell clipped to its column, with the same inner margin as WPF's GridView cells
        foreach (var column in owner.Columns)
            Children.Add(new Border { ClipToBounds = true, Padding = new Thickness(6, 0), Child = column.CellTemplate?.Build(null) });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_header) _owner.LayoutChanged += InvalidateMeasure;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_header) _owner.LayoutChanged -= InvalidateMeasure;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        for (var i = 0; i < Children.Count && i < _owner.Columns.Count; i++)
        {
            var w = _owner.ColumnWidth(i);
            Children[i].Measure(new Size(w, double.PositiveInfinity));
            width += w;
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (var i = 0; i < Children.Count && i < _owner.Columns.Count; i++)
        {
            var w = _owner.ColumnWidth(i);
            Children[i].Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}
