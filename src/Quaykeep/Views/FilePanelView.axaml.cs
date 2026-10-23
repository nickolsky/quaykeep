using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Quaykeep.Core.Files;
using Quaykeep.ViewModels;

namespace Quaykeep.Views;

/// <summary>One panel of the file manager: folder listing, keyboard (F2…F8), context menu, drag and drop.</summary>
public partial class FilePanelView : UserControl
{
    private static readonly DataFormat<DragPayload> DragFormat = DataFormat.CreateInProcessFormat<DragPayload>("Quaykeep.FileRows");
    private const double DragDistance = 4;
    private readonly Dictionary<DataGridRow, (FileRow Row, PropertyChangedEventHandler Handler)> _watched = [];
    private DataGridCollectionView? _view;
    private (string Member, bool Descending)? _sort;
    private PointerPressedEventArgs? _dragPress;
    private Point _dragStart;
    private bool _dragArmed;
    private string _typed = "";
    private DateTime _typedAt;

    public FilePanelView()
    {
        InitializeComponent();
        Grid.AddHandler(KeyDownEvent, OnGridKey, RoutingStrategies.Tunnel);
        Grid.AddHandler(TextInputEvent, OnTypeToFind, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel);
        Grid.DoubleTapped += OnDoubleClick;
        Grid.GotFocus += (_, _) => Activated?.Invoke(this);
        Grid.Sorting += OnSorting;
        Grid.LoadingRow += (_, e) => WatchMarks(e.Row);
        Grid.UnloadingRow += (_, e) => Unwatch(e.Row);
        DragDrop.AddDragOverHandler(Grid, OnDragOver);
        DragDrop.AddDropHandler(Grid, OnDrop);
        Menu.Opening += OnContextMenuOpening;
    }

    public FilePanelModel Model { get; private set; } = null!;

    /// <summary>Copy / move the items to the other panel (target folder: null = the other panel's folder).</summary>
    public event Action<FilePanelView, IReadOnlyList<FileItem>, bool, string?>? CopyRequested;
    /// <summary>Files of this computer dropped on this panel (from the system's file manager): upload them into the folder.</summary>
    public event Action<IReadOnlyList<string>, string>? FilesDropped;
    public event Action<FileItem>? EditRequested;
    public event Action<string>? NewFileCreated;
    public event Action<string>? TerminalRequested;
    public event Action? SwitchRequested;
    public event Action<FilePanelView>? Activated;

    public void Attach(FilePanelModel model)
    {
        Model = model;
        DataContext = model;
        _view = new DataGridCollectionView(model.Rows);
        Grid.ItemsSource = _view;
        model.SelectRequested += Select;
        if (!model.IsRemote)
        {
            Grid.Columns[3].IsVisible = false; // permissions
            Grid.Columns[4].IsVisible = false; // owner
            MiChmod.IsVisible = false;
            MiTerminal.IsVisible = false;
        }
        else MiExplorer.IsVisible = false;
        MiCopy.Header = L.Get(model.IsRemote ? "Files.Download" : "Files.Upload");
        MiMove.Header = L.Get(model.IsRemote ? "Files.MoveDownload" : "Files.MoveUpload");
    }

    public void SetActive(bool active)
    {
        _isActive = active;
        Frame.SetResourceReference(Border.BorderBrushProperty, active ? "AccentFillColorDefaultBrush" : "ControlFillColorTransparentBrush");
    }

    private bool _isActive;

    /// <summary>
    /// After the list was rebuilt (a folder opened, a rename…) the focus may be gone: the active panel takes it back,
    /// unless the user moved on to a text box (the path) or elsewhere.
    /// </summary>
    private bool ShouldTakeFocus()
    {
        if (Grid.IsKeyboardFocusWithin) return true;
        if (!_isActive || !IsEffectivelyVisible) return false;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is null or Window or TabItem or TabControl or DataGrid ||
               focused is Visual v && v is not TextBox && v.FindSelfOrAncestor<FilePanelView>() == this;
    }

    /// <summary>Keyboard focus on the list with the cursor on a row (the arrows are handled here, so they never leave it).</summary>
    public void FocusGrid()
    {
        if (Model.Rows.Count == 0)
        {
            Grid.Focus();
            return;
        }
        var row = Current ?? Model.Rows.FirstOrDefault(r => !r.IsParent) ?? Model.Rows[0];
        FocusRow(row);
    }

    private void FocusRow(FileRow row)
    {
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row, Grid.Columns[0]);
        Grid.Focus();
    }

    /// <summary>What an operation works on (Far rules): the marked files, otherwise the one under the cursor.</summary>
    public List<FileItem> SelectedItems()
    {
        var marked = Model.Marked;
        if (marked.Count > 0) return marked.Select(r => r.Item).ToList();
        return Current is { IsParent: false } c ? [c.Item] : [];
    }

    private void ClearMarks()
    {
        if (Model.Marked.Count > 0) Model.MarkAll(false);
    }

    /// <summary>The rows in the order shown (sorted).</summary>
    private List<FileRow> Shown => _view?.Cast<FileRow>().ToList() ?? [.. Model.Rows];

    private void MoveCursor(int delta)
    {
        var rows = Shown;
        if (rows.Count == 0) return;
        var i = Math.Clamp((Current == null ? 0 : rows.IndexOf(Current)) + delta, 0, rows.Count - 1);
        FocusRow(rows[i]);
    }

    /// <summary>Rows that fit in the panel (for PageUp / PageDown).</summary>
    private int PageRows => Math.Max(1, (int)((Grid.Bounds.Height - 30) / Math.Max(1, Grid.RowHeight)) - 1);

    private void ToggleMark(FileRow? row)
    {
        if (row == null || row.IsParent) return;
        row.IsMarked = !row.IsMarked;
        Model.UpdateStatus();
    }

    private void MarkByMask(bool on)
    {
        var mask = InputDialog.Ask(Owner, L.Get(on ? "Files.MarkMask" : "Files.UnmarkMask"), L.Get("Files.MaskPrompt"), _lastMask);
        if (string.IsNullOrWhiteSpace(mask)) return;
        _lastMask = mask.Trim();
        Model.MarkByMask(_lastMask, on);
    }

    private static string _lastMask = "*.*";

    private FileRow? Current => Grid.SelectedItem as FileRow;

    private void Select(string? name)
    {
        if (Model.Rows.Count == 0) return;
        _view?.Refresh();
        var row = name == null ? null : Model.Rows.FirstOrDefault(r => r.Name == name);
        // a new folder starts on ".." (Far), going up lands on the folder we came from
        row ??= Shown.FirstOrDefault() ?? Model.Rows[0];
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row, Grid.Columns[0]);
        Dispatcher.UIThread.Post(() =>
        {
            if (ShouldTakeFocus()) FocusRow(row);
        }, DispatcherPriority.Background);
    }

    // ---------- marked rows: the caution colour ----------

    private void WatchMarks(DataGridRow row)
    {
        Unwatch(row);
        if (row.DataContext is not FileRow file) return;
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(FileRow.IsMarked)) row.Classes.Set("marked", file.IsMarked);
        };
        file.PropertyChanged += handler;
        _watched[row] = (file, handler);
        row.Classes.Set("marked", file.IsMarked);
    }

    private void Unwatch(DataGridRow row)
    {
        if (_watched.Remove(row, out var w)) w.Row.PropertyChanged -= w.Handler;
        row.Classes.Set("marked", false);
    }

    // ---------- navigation ----------

    private async void Open(FileRow? row)
    {
        if (row == null) return;
        if (row.IsParent)
        {
            await Model.UpAsync();
            return;
        }
        if (row.IsDirectory)
        {
            await Model.OpenAsync(row.Item.Path);
            return;
        }
        if (Model.IsRemote) EditRequested?.Invoke(row.Item);
        else
            try
            {
                UiExtensions.OpenWithShell(row.Item.Path);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(Owner, ex.Message, L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
    }

    private void OnDoubleClick(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source) is not { } row) return;
        Open(row);
        e.Handled = true;
    }

    private async void OnPathKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await Model.OpenAsync(Model.PathEdit);
            FocusGrid();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Model.PathEdit = Model.Path;
            FocusGrid();
        }
    }

    private async void OnUp(object? sender, RoutedEventArgs e) => await Model.UpAsync();
    private async void OnHome(object? sender, RoutedEventArgs e) => await Model.OpenAsync(null);
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await Model.RefreshAsync(Current?.Name);
    private async void OnRetry(object? sender, RoutedEventArgs e) => await Model.RefreshAsync();

    private async void OnGridKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta;
        var shift = e.KeyModifiers == KeyModifiers.Shift;
        var none = e.KeyModifiers == KeyModifiers.None;
        var handled = true;
        switch (e.Key)
        {
            case Key.Enter when none: Open(Current); break;
            case Key.Back when none:
                e.Handled = true;
                await Model.UpAsync();
                return;
            case Key.Tab when none || shift: SwitchRequested?.Invoke(); break;
            case Key.F2 when none: Rename(); break;
            case Key.F4 when none: Edit(); break;
            case Key.F4 when shift: NewFile(); break;
            case Key.F5 when none: Copy(false); break;
            case Key.F6 when none: Copy(true); break;
            case Key.F7 when none: NewFolder(); break;
            case Key.F8 or Key.Delete when none: Delete(); break;
            // Far: Insert marks and moves down, Shift+arrows mark while moving, gray + - * work with masks
            case Key.Insert when none:
                ToggleMark(Current);
                MoveCursor(1);
                break;
            case Key.Down when shift:
                ToggleMark(Current);
                MoveCursor(1);
                break;
            case Key.Up when shift:
                ToggleMark(Current);
                MoveCursor(-1);
                break;
            // the cursor moves here, not by the grid: it never leaves the list
            case Key.Down when none: MoveCursor(1); break;
            case Key.Up when none: MoveCursor(-1); break;
            case Key.PageDown when none: MoveCursor(PageRows); break;
            case Key.PageUp when none: MoveCursor(-PageRows); break;
            case Key.Home when none || ctrl: MoveCursor(-Model.Rows.Count); break;
            case Key.End when none || ctrl: MoveCursor(Model.Rows.Count); break;
            case Key.Left or Key.Right when none: break;
            case Key.Add when none: MarkByMask(true); break;
            case Key.Subtract when none: MarkByMask(false); break;
            case Key.Multiply when none: Model.InvertMarks(); break;
            case Key.A when ctrl: Model.MarkAll(Model.Marked.Count == 0); break;
            case Key.Escape when none && Model.Marked.Count > 0: ClearMarks(); break;
            case Key.R when ctrl:
                e.Handled = true;
                await Model.RefreshAsync(Current?.Name);
                return;
            case Key.H when ctrl: Model.ShowHidden = !Model.ShowHidden; break;
            case Key.L when ctrl:
                PathBox.Focus();
                PathBox.SelectAll();
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    /// <summary>Typing letters jumps to the first name that starts with them.</summary>
    private void OnTypeToFind(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        _typed = DateTime.Now - _typedAt > TimeSpan.FromSeconds(1) ? e.Text : _typed + e.Text;
        _typedAt = DateTime.Now;
        var row = Shown.FirstOrDefault(r => r.Name.StartsWith(_typed, StringComparison.OrdinalIgnoreCase));
        if (row == null) return;
        FocusRow(row);
        e.Handled = true;
    }

    // ---------- sorting: ".." first, folders before files ----------

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (_view == null || e.Column.SortMemberPath is not { Length: > 0 } member) return;
        var descending = _sort is { } s && s.Member == member && !s.Descending;
        _sort = (member, descending);
        _view.SortDescriptions.Clear();
        // the comparer knows the direction itself, so ".." and the folders stay on top either way
        _view.SortDescriptions.Add(DataGridSortDescription.FromComparer(new RowComparer(member, descending), ListSortDirection.Ascending));
    }

    private sealed class RowComparer(string member, bool descending) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not FileRow a || y is not FileRow b) return 0;
            if (a.IsParent != b.IsParent) return a.IsParent ? -1 : 1;
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var r = member switch
            {
                "Item.Size" => a.Item.Size.CompareTo(b.Item.Size),
                "Item.Modified" => Nullable.Compare(a.Item.Modified, b.Item.Modified),
                _ => 0,
            };
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return descending ? -r : r;
        }
    }

    // ---------- operations ----------

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void Copy(bool move)
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        CopyRequested?.Invoke(this, items, move, null);
        ClearMarks();
    }

    public void Edit()
    {
        if (Current is { IsDirectory: false } row)
        {
            if (Model.IsRemote) EditRequested?.Invoke(row.Item);
            else Open(row);
        }
    }

    private async void Rename()
    {
        if (Current is not { IsParent: false } row || Model.Fs is not { } fs) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.Rename"), L.Get("Files.NewName"), row.Name);
        if (name == null || name == row.Name) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dir = fs.Parent(row.Item.Path) ?? Model.Path;
        await Model.RunAsync(f => f.Rename(row.Item.Path, f.Combine(dir, name)), name);
    }

    public async void NewFolder()
    {
        if (Model.Fs is not { } fs || Model.Path.Length == 0) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.NewFolder"), L.Get("Files.FolderName"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Model.RunAsync(f => f.CreateDirectory(f.Combine(Model.Path, name)), name);
    }

    private async void NewFile()
    {
        if (Model.Fs is not { } fs || Model.Path.Length == 0) return;
        var name = InputDialog.Ask(Owner, L.Get("Files.NewFile"), L.Get("Files.FileName"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!FileSystems.IsValidName(name, fs.IsRemote))
        {
            MessageBox.Show(Owner, L.Get("Files.BadName"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var path = fs.Combine(Model.Path, name);
        var ok = await Model.RunAsync(f =>
        {
            if (f.Stat(path) != null) throw new InvalidOperationException(L.F("Files.Exists", name));
            f.Create(path).Dispose();
        }, name);
        if (ok) NewFileCreated?.Invoke(path);
    }

    public async void Delete()
    {
        var items = SelectedItems();
        if (items.Count == 0 || Model.Fs == null) return;
        var what = items.Count == 1 ? items[0].Name : L.F("Files.ItemsCount", items.Count);
        if (MessageBox.Show(Owner, L.F("Files.DeleteConfirm", what), L.Get("Files.Title"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var index = Current == null ? 0 : Shown.IndexOf(Current);
        await Model.RunAsync(f =>
        {
            foreach (var i in items) f.Delete(i);
        });
        var rows = Shown;
        if (rows.Count > 0) FocusRow(rows[Math.Clamp(index, 0, rows.Count - 1)]);
    }

    private async void Chmod()
    {
        var items = SelectedItems();
        if (items.Count == 0 || Model.Fs is not SftpFileSystem) return;
        var current = items[0].Mode is { } m ? Convert.ToString(m, 8).PadLeft(3, '0') : "644";
        var text = InputDialog.Ask(Owner, L.Get("Files.Chmod"), L.F("Files.ChmodPrompt", items.Count == 1 ? items[0].Name : L.F("Files.ItemsCount", items.Count)), current);
        if (text == null) return;
        if (FileSystems.ParseMode(text) is not { } mode)
        {
            MessageBox.Show(Owner, L.Get("Files.BadMode"), L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Model.RunAsync(f =>
        {
            foreach (var i in items) ((SftpFileSystem)f).ChangeMode(i.Path, mode);
        }, items[0].Name);
    }

    // ---------- mouse: Ctrl / Shift + click marks, Shift + right button marks (Far style), drag and drop ----------

    private bool? _rightMarking;
    private bool _suppressMenu;

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Grid);
        var row = RowAt(e.Source);
        var mods = e.KeyModifiers;
        if (point.Properties.IsRightButtonPressed)
        {
            if (row == null) return;
            if (mods == KeyModifiers.Shift && !row.IsParent)
            {
                // dragging marks every row the mouse passes with the same mark as the first one
                _rightMarking = !row.IsMarked;
                row.IsMarked = _rightMarking.Value;
                Model.UpdateStatus();
                Grid.SelectedItem = row;
                e.Pointer.Capture(Grid);
                _suppressMenu = true;
                e.Handled = true;
                return;
            }
            // right click moves the cursor, the marks stay
            Grid.SelectedItem = row;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragStart = point.Position;
        if (row != null && (mods == KeyModifiers.Control || mods == KeyModifiers.Meta))
        {
            ToggleMark(row);
            Grid.SelectedItem = row;
            e.Handled = true;
            return;
        }
        if (row != null && mods == KeyModifiers.Shift && Current is { } from)
        {
            var rows = Shown;
            var (a, b) = (rows.IndexOf(from), rows.IndexOf(row));
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                if (i >= 0) rows[i].IsMarked = true;
            Model.UpdateStatus();
            Grid.SelectedItem = row;
            e.Handled = true;
            return;
        }
        _dragArmed = row != null && e.ClickCount == 1;
        _dragPress = _dragArmed ? e : null;
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragArmed = false;
        _dragPress = null;
        if (_rightMarking == null) return;
        _rightMarking = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(Grid);
        if (_rightMarking is { } on)
        {
            if (!point.Properties.IsRightButtonPressed)
            {
                _rightMarking = null;
                e.Pointer.Capture(null);
                return;
            }
            if (RowAt(Grid.InputHitTest(point.Position)) is not { IsParent: false } marked || marked.IsMarked == on) return;
            marked.IsMarked = on;
            Grid.SelectedItem = marked;
            Model.UpdateStatus();
            return;
        }
        if (!_dragArmed || _dragPress is not { } press || !point.Properties.IsLeftButtonPressed) return;
        var d = point.Position - _dragStart;
        if (Math.Abs(d.X) < DragDistance && Math.Abs(d.Y) < DragDistance) return;
        _dragArmed = false;
        _dragPress = null;
        // the marked files when the drag starts on one of them, otherwise the file under the mouse
        var under = Current;
        var items = under is { IsMarked: true } || under == null ? SelectedItems() : under.IsParent ? [] : [under.Item];
        if (items.Count == 0) return;
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(DragFormat, new DragPayload(this, items));
        data.Add(item);
        // local files can also go to the system's file manager or other apps
        if (!Model.IsRemote && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            foreach (var i in items)
            {
                IStorageItem? s = i.IsDirectory ? await storage.TryGetFolderFromPathAsync(i.Path) : await storage.TryGetFileFromPathAsync(i.Path);
                if (s != null) data.Add(DataTransferItem.CreateFile(s));
            }
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private sealed record DragPayload(FilePanelView Source, List<FileItem> Items);

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_suppressMenu)
        {
            _suppressMenu = false;
            e.Cancel = true;
            return;
        }
        var hasItem = SelectedItems().Count > 0;
        var file = Current is { IsDirectory: false };
        MiOpen.IsEnabled = Current != null;
        MiEdit.IsEnabled = file;
        MiCopy.IsEnabled = MiMove.IsEnabled = MiDelete.IsEnabled = MiChmod.IsEnabled = MiCopyPath.IsEnabled = hasItem;
        MiRename.IsEnabled = Current is { IsParent: false };
        MiNewFile.IsEnabled = Model.Path.Length > 0;
    }

    private void OnMenuOpen(object? sender, RoutedEventArgs e) => Open(Current);
    private void OnMenuMarkMask(object? sender, RoutedEventArgs e) => MarkByMask(true);
    private void OnMenuUnmarkMask(object? sender, RoutedEventArgs e) => MarkByMask(false);
    private void OnMenuInvert(object? sender, RoutedEventArgs e) => Model.InvertMarks();
    private void OnMenuEdit(object? sender, RoutedEventArgs e) => Edit();
    private void OnMenuCopy(object? sender, RoutedEventArgs e) => Copy(false);
    private void OnMenuMove(object? sender, RoutedEventArgs e) => Copy(true);
    private void OnMenuRename(object? sender, RoutedEventArgs e) => Rename();
    private void OnMenuChmod(object? sender, RoutedEventArgs e) => Chmod();
    private void OnMenuDelete(object? sender, RoutedEventArgs e) => Delete();
    private void OnMenuNewFolder(object? sender, RoutedEventArgs e) => NewFolder();
    private void OnMenuNewFile(object? sender, RoutedEventArgs e) => NewFile();
    private void OnMenuTerminal(object? sender, RoutedEventArgs e) => TerminalRequested?.Invoke(Current is { IsDirectory: true, IsParent: false } d ? d.Item.Path : Model.Path);

    private void OnMenuCopyPath(object? sender, RoutedEventArgs e)
    {
        var paths = SelectedItems().Select(i => i.Path).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    /// <summary>Shows the item in the system's file manager: selected in Explorer / Finder, its folder elsewhere.</summary>
    private void OnMenuExplorer(object? sender, RoutedEventArgs e)
    {
        var selected = Current is { IsParent: false } row ? row.Item.Path : null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var args = selected != null ? $"/select,\"{selected}\"" : $"\"{Model.Path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
            }
            else if (OperatingSystem.IsMacOS() && selected != null)
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(selected);
                Process.Start(psi)?.Dispose();
            }
            else UiExtensions.OpenWithShell(Model.Path.Length > 0 ? Model.Path : "/");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(Owner, ex.Message, L.Get("Files.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- drop ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (e.DataTransfer.TryGetValue(DragFormat) is { } p && p.Source != this)
            e.DragEffects = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move : DragDropEffects.Copy;
        else if (Model.IsRemote && e.DataTransfer.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        // onto a folder row: into that folder
        var target = RowAt(e.Source) is { IsDirectory: true, IsParent: false } r ? r.Item.Path : Model.Path;
        if (e.DataTransfer.TryGetValue(DragFormat) is { } p)
        {
            if (p.Source != this) p.Source.CopyRequested?.Invoke(p.Source, p.Items, e.KeyModifiers.HasFlag(KeyModifiers.Shift), target);
        }
        else if (Model.IsRemote && e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) FilesDropped?.Invoke(paths, target);
        }
        e.Handled = true;
    }

    /// <summary>The row under an element (null on the header, a scroll bar or empty space).</summary>
    private static FileRow? RowAt(object? source)
    {
        var visual = source as Visual;
        if (visual.FindSelfOrAncestor<DataGridColumnHeader>() != null || visual.FindSelfOrAncestor<Avalonia.Controls.Primitives.ScrollBar>() != null)
            return null;
        return visual.FindSelfOrAncestor<DataGridRow>()?.DataContext as FileRow;
    }
}
