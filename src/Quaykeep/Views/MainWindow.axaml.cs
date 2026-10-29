using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Quaykeep.Core;
using Quaykeep.Core.Models;
using Quaykeep.Core.Ssh;
using Quaykeep.Services;
using Quaykeep.ViewModels;
using Quaykeep.Views.Controls;

namespace Quaykeep.Views;

public partial class MainWindow : Window
{
    /// <summary>Dynamic menu entries: a check mark only on the ones that can be checked.</summary>
    public static readonly IValueConverter CheckToggle =
        new FuncValueConverter<bool, MenuItemToggleType>(c => c ? MenuItemToggleType.CheckBox : MenuItemToggleType.None);

    public static readonly IValueConverter BoldWeight = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private bool _forceClose;

    public MainWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _vm = new MainViewModel(host) { Owner = this };
        DataContext = _vm;
        Icon = IconFactory.Create(true);
        Title = $"{AppPaths.ProductTitle} {Core.Updates.UpdateService.Current.ToString(3)}";

        var s = host.SettingsStore.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);

        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, KeyModifiers.Control), Command = new Mvvm.RelayCommand(() => SearchBox.Focus()) });
        ServersTree.DoubleClickOpens = node => node is ServerNode;
        ServersTree.SelectedItemChanged += (_, node) => _vm.SelectedNode = node;
        ServersTree.RowDoubleClicked += OnTreeDoubleClick;
        ServersTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        if (ServersTree.ContextMenu is { } menu) menu.Opening += OnTreeContextMenuOpening;
        RestoreColumns(ServersTree);
        RestoreColumns(KeysGrid);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            foreach (var t in ContentTabs.ToList())
                if (Page(t) is IDisposable d) d.Dispose();
            _vm.Dispose();
        };
        Opened += (_, _) => ServersTree.Focus();
        // the pages of the fixed tabs go to the page host too (see MainWindow.axaml)
        foreach (var tab in Tabs.Items.OfType<TabItem>().ToList())
        {
            if (tab.Content is not Control page) continue;
            tab.Content = null;
            tab.Tag = page;
            PageHost.Children.Add(page);
        }
        Tabs.SelectionChanged += OnTabChanged;
        ShowSelectedPage();
        SetUpPlatformSettings();
    }

    /// <summary>The page of a tab: kept in its Tag, shown in <c>PageHost</c>.</summary>
    private static Control? Page(TabItem tab) => tab.Tag as Control;

    /// <summary>Shows the selected tab's page; the others stay attached, only hidden (native web views survive).</summary>
    private void ShowSelectedPage()
    {
        var selected = (Tabs.SelectedItem as TabItem)?.Tag;
        foreach (var page in PageHost.Children) page.IsVisible = ReferenceEquals(page, selected);
    }

    /// <summary>Settings that only exist on some systems: the global shortcut (Windows), Windows Terminal.</summary>
    private void SetUpPlatformSettings()
    {
        if (GlobalHotkey.IsSupported)
        {
            HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);
            HotkeyBox.GotFocus += (_, _) => _host.SuspendHotkey();
            HotkeyBox.LostFocus += (_, _) =>
            {
                _host.ApplyHotkey();
                _vm.Settings.RefreshHotkeyStatus();
            };
        }
        else
        {
            HotkeyRow.IsVisible = false;
            HotkeyUnsupported.Text = L.Get("Hotkey.WindowsOnly");
            HotkeyUnsupported.IsVisible = true;
        }
        if (OperatingSystem.IsWindows()) return;
        // Windows Terminal exists only on Windows; "console" is the system terminal there
        TermWtTab.IsVisible = false;
        TermWtWindow.IsVisible = false;
        WtProfileRow.IsVisible = false;
        var name = SessionLauncher.ExternalTerminalName;
        TermConsole.Content = name.Length > 0 ? L.F("Settings.SystemTerminal", name) : L.Get("Settings.SystemTerminalMissing");
    }

    /// <summary>Close for real (used on lock), bypassing hide-to-tray.</summary>
    public new void Close()
    {
        _forceClose = true;
        base.Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var s = _host.SettingsStore.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ClientSize.Width;
            s.WindowHeight = ClientSize.Height;
        }
        SaveColumns(ServersTree);
        SaveColumns(KeysGrid);
        _host.SettingsStore.Save();
        // the app is exiting (Exit, an update, the OS logging off): nothing to ask, nothing to hide
        var shutdown = e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown;
        if (!_forceClose && !shutdown && s.CloseToTray)
        {
            e.Cancel = true;
            Hide(); // terminals keep running in the hidden window
            return;
        }
        var open = TerminalTabs.Count(t => ((TerminalView)Page(t)!).State != TerminalState.Closed) +
                   ContentTabs.Count(t => Page(t) is FileManagerView { HasRunningTransfers: true } or EditorView { IsDirty: true });
        if (!_forceClose && !shutdown && open > 0 &&
            MessageBox.Show(this, L.F("Term.CloseAllConfirm", open), AppPaths.ProductTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _host.OnMainWindowClosed();
        if (!_forceClose && !shutdown) Dispatcher.UIThread.Post(_host.Exit);
    }

    // ---------- tabs: built-in terminals, file managers, editors ----------

    private IEnumerable<TabItem> TerminalTabs => Tabs.Items.OfType<TabItem>().Where(t => Page(t) is TerminalView);
    private IEnumerable<TabItem> ContentTabs => Tabs.Items.OfType<TabItem>().Where(t => Page(t) is IDisposable);

    /// <summary>A closable tab (× button, middle click) with a status dot; returns the tab and the dot painter.</summary>
    private (TabItem Tab, Action<string> Paint, TextBlock Title) AddTab(Control content, string title, Symbol? icon, string tooltip)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button
        {
            Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 10 },
            Margin = new Thickness(10, 0, -8, 0),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Classes.Add("flat");
        ToolTip.SetTip(close, L.Get("Term.CloseTab"));
        var header = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        header.Children.Add(dot);
        if (icon is { } symbol)
            header.Children.Add(new SymbolIcon { Symbol = symbol, FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(text);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Tag = content };
        ToolTip.SetTip(tab, tooltip);
        content.IsVisible = false;
        PageHost.Children.Add(content);
        close.Click += (_, _) => CloseTab(tab);
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) CloseTab(tab);
        };
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return (tab, brush => dot.SetResourceReference(Shape.FillProperty, brush), text);
    }

    /// <summary>The list of every server's tunnels (from the tray).</summary>
    public void OpenAllTunnels() => _vm.OpenAllTunnels();

    /// <summary>Opens a terminal tab for the server and switches to it.</summary>
    /// <param name="cd">Start in this remote folder (an interactive shell, not a command).</param>
    public void OpenTerminal(ServerEntry server, string? command, string? title, string? cd = null)
    {
        var entry = server.Clone();
        // leading space: most shells keep it out of the history; clear hides the typed command line
        var typed = command != null ? " clear; " + command : cd != null ? " cd " + RemoteShell.Quote(cd) + " && clear" : null;
        var view = new TerminalView(() => _host.CreateTerminalSession(entry), typed, _host.SettingsStore.Settings.TerminalAutoSuggest);
        var (tab, paint, _) = AddTab(view, title ?? entry.Name, null, L.F("Term.TabTip", entry.Name, entry.Display, ""));

        void Paint(TerminalState state) => paint(state switch
        {
            TerminalState.Connected => "SystemFillColorSuccessBrush",
            TerminalState.Connecting => "SystemFillColorAttentionBrush",
            _ => "ControlStrongFillColorDisabledBrush",
        });
        Paint(TerminalState.Connecting);
        view.StateChanged += state =>
        {
            Paint(state);
            // "exit" in an interactive session closes the tab; commands keep their output on screen
            if (state == TerminalState.Closed && view.EndedNormally && command == null) CloseTab(tab);
        };
        view.TitleChanged += t => ToolTip.SetTip(tab, L.F("Term.TabTip", entry.Name, entry.Display, t));
        view.EditRequested += path => OpenEditor(entry, path);
        view.KeyCommand += key =>
        {
            switch (key)
            {
                case "close": CloseTab(tab); break;
                case "next": Cycle(+1); break;
                case "prev": Cycle(-1); break;
                case "duplicate": OpenTerminal(entry, null, null, view.Cwd); break;
                case "files": OpenFiles(entry, view.Cwd); break;
            }
        };
        tab.ContextMenu = TabMenu(tab, entry, () => view.Cwd, () => OpenTerminal(entry, null, null, view.Cwd));
    }

    /// <summary>The server's file manager tab (one per server): opens it or shows <paramref name="dir"/> in it.</summary>
    public void OpenFiles(ServerEntry server, string? dir)
    {
        var existing = ContentTabs.FirstOrDefault(t => Page(t) is FileManagerView f && f.Server.Id == server.Id);
        if (existing is { Tag: FileManagerView open })
        {
            Tabs.SelectedItem = existing;
            if (dir != null) open.Navigate(dir);
            if (!IsVisible) Show();
            Activate();
            return;
        }
        var entry = server.Clone();
        var view = new FileManagerView(_host, entry, dir);
        var (tab, paint, _) = AddTab(view, entry.Name, Symbol.Folder, L.F("Files.TabTip", entry.Name, entry.Display));
        void Paint() => paint(view.IsConnected ? "SystemFillColorSuccessBrush" : view.HasError ? "SystemFillColorCriticalBrush" : "SystemFillColorAttentionBrush");
        Paint();
        view.StateChanged += Paint;
        view.EditRequested += path => OpenEditor(entry, path);
        view.TerminalRequested += path => OpenTerminal(entry, null, null, path);
        view.CloseRequested += () => CloseTab(tab);
        tab.ContextMenu = TabMenu(tab, entry, () => view.RemotePath, () => OpenTerminal(entry, null, null, view.RemotePath));
    }

    /// <summary>A remote text file in an editor tab (the same file of the same server opens once).</summary>
    public void OpenEditor(ServerEntry server, string path)
    {
        var existing = ContentTabs.FirstOrDefault(t => Page(t) is EditorView e && e.Server.Id == server.Id && e.Path == path);
        if (existing != null)
        {
            Tabs.SelectedItem = existing;
            if (!IsVisible) Show();
            Activate();
            return;
        }
        var entry = server.Clone();
        var view = new EditorView(_host, entry, path);
        var (tab, paint, title) = AddTab(view, view.FileName, Symbol.Edit, L.F("TextEd.TabTip", path, entry.Name));
        void Paint()
        {
            paint(view.HasError ? "SystemFillColorCriticalBrush" : view.IsDirty ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
            title.Text = (view.IsDirty ? "● " : "") + view.FileName;
        }
        Paint();
        view.StateChanged += Paint;
        view.SavedAndClose += () => CloseTab(tab);
        view.KeyCommand += key =>
        {
            switch (key)
            {
                case "close": CloseTab(tab); break;
                case "next": Cycle(+1); break;
                case "prev": Cycle(-1); break;
            }
        };
        var dir = path[..Math.Max(1, path.LastIndexOf('/'))];
        tab.ContextMenu = TabMenu(tab, entry, () => dir, () => OpenTerminal(entry, null, null, dir));
    }

    /// <summary>Right click on a tab header.</summary>
    private ContextMenu TabMenu(TabItem tab, ServerEntry entry, Func<string?> dir, Action terminal)
    {
        var menu = new ContextMenu();
        void Item(string key, Action a, string? gesture = null)
        {
            var mi = new MenuItem { Header = L.Get(key), InputGesture = gesture == null ? null : KeyGesture.Parse(gesture) };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Item("Tab.NewTerminal", terminal, "Ctrl+Shift+T");
        Item("Tab.Files", () => OpenFiles(entry, dir()), "Ctrl+Shift+F");
        menu.Items.Add(new Separator());
        Item("Tab.Close", () => CloseTab(tab), "Ctrl+Shift+W");
        Item("Tab.CloseOthers", () =>
        {
            foreach (var t in ContentTabs.Where(t => t != tab).ToList()) CloseTab(t);
        });
        return menu;
    }

    /// <summary>Asks before closing a tab that would lose work (unsaved text, a running transfer).</summary>
    private void CloseTab(TabItem tab)
    {
        var page = Page(tab);
        if (page is FileManagerView { HasRunningTransfers: true } &&
            MessageBox.Show(this, L.Get("Files.CloseWithTransfers"), AppPaths.ProductTitle, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (page is EditorView editor && !editor.ConfirmClose()) return;
        if (page is IDisposable d) d.Dispose();
        var index = Tabs.Items.IndexOf(tab);
        if (index < 0) return;
        var wasSelected = Tabs.SelectedItem == tab;
        Tabs.Items.Remove(tab);
        if (page != null) PageHost.Children.Remove(page);
        // the next tab (or the previous one), the server list when none are left
        if (wasSelected) Tabs.SelectedIndex = ContentTabs.Any() ? Math.Min(index, Tabs.Items.Count - 1) : 0;
    }

    private void Cycle(int step)
    {
        var n = Tabs.Items.Count;
        if (n > 1) Tabs.SelectedIndex = ((Tabs.SelectedIndex + step) % n + n) % n;
    }

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // the event bubbles up from lists and combo boxes inside the tabs
        if (!ReferenceEquals(e.Source, Tabs)) return;
        ShowSelectedPage();
        Action? focus = Tabs.SelectedItem switch
        {
            TabItem { Tag: TerminalView t } => () => t.Focus(),
            TabItem { Tag: FileManagerView f } => () => f.Focus(),
            TabItem { Tag: EditorView ed } => () => ed.Focus(),
            _ => null,
        };
        if (focus != null) Dispatcher.UIThread.Post(focus, DispatcherPriority.Input);
    }

    // ---------- global shortcut box (Settings, Windows only) ----------

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab) return;
        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None)
        {
            _vm.Settings.HotkeyText = "";
            _host.SuspendHotkey();
            return;
        }
        if (GlobalHotkey.FromKeyPress(e.KeyModifiers, e.Key) is not { } combo) return;
        _vm.Settings.HotkeyText = combo;
        // stays off while the box has the focus; the new one works once it loses it
        _host.SuspendHotkey();
        ServersTree.Focus();
    }

    private void OnHotkeyDefault(object? sender, RoutedEventArgs e) => _vm.Settings.HotkeyText = GlobalHotkey.Default;
    private void OnHotkeyClear(object? sender, RoutedEventArgs e) => _vm.Settings.HotkeyText = "";

    // ---------- column widths (all but the last column, which fills) ----------

    private void RestoreColumns(DataGrid grid)
    {
        if (grid.Name is not { } name || !_host.SettingsStore.Settings.ColumnWidths.TryGetValue(name, out var widths) ||
            widths.Length != grid.Columns.Count - 1)
            return;
        for (int i = 0; i < widths.Length; i++)
        {
            var col = grid.Columns[i];
            if (widths[i] >= col.MinWidth) col.Width = new DataGridLength(widths[i]);
        }
    }

    private void SaveColumns(DataGrid grid)
    {
        // A tab that was never shown has no measured columns; keep what was saved before.
        if (grid.Name is not { } name || grid.Columns.Take(grid.Columns.Count - 1).Any(c => c.ActualWidth <= 0)) return;
        _host.SettingsStore.Settings.ColumnWidths[name] =
            grid.Columns.Take(grid.Columns.Count - 1).Select(c => Math.Round(c.ActualWidth)).ToArray();
    }

    private void RestoreColumns(TreeListView tree)
    {
        if (tree.Name is not { } name || !_host.SettingsStore.Settings.ColumnWidths.TryGetValue(name, out var widths) ||
            widths.Length != tree.Columns.Count - 1)
            return;
        for (int i = 0; i < widths.Length; i++)
            if (widths[i] >= 30) tree.Columns[i].Width = widths[i];
    }

    private void SaveColumns(TreeListView tree)
    {
        var cols = tree.Columns.Take(tree.Columns.Count - 1).ToList();
        if (tree.Name is not { } name || cols.Any(c => c.ActualWidth <= 0)) return;
        _host.SettingsStore.Settings.ColumnWidths[name] = cols.Select(c => Math.Round(c.ActualWidth)).ToArray();
    }

    // ---------- tree ----------

    private void OnTreeContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (_vm.SelectedNode is InfoNode or SectionNode)
        {
            e.Cancel = true;
            return;
        }
        _vm.PrepareContextMenu();
    }

    private void OnTreeDoubleClick(object? sender, TreeRowEventArgs e)
    {
        switch (e.Node)
        {
            case ForwardNode { Peer: not null }:
                _vm.GoToForwardPeer(); // like following a link
                e.Handled = true;
                break;
            case ServerNode:
                _vm.Connect();
                e.Handled = true;
                break;
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (e.Key == Key.Enter)
        {
            if (_vm.SelectedNode is ServerNode) _vm.Connect();
            else if (_vm.SelectedNode is { } n) n.IsExpanded = !n.IsExpanded;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.CanDelete)
        {
            _vm.DeleteServerCommand.Execute(null);
            e.Handled = true;
        }
    }
}
