using Avalonia;
using Avalonia.Controls;
using Quaykeep.Core;
using Quaykeep.Core.Models;
using Quaykeep.Views;

namespace Quaykeep.Services;

/// <summary>Tray icon (the menu bar on macOS) with the quick-connect menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly Avalonia.Controls.TrayIcon _icon;
    private readonly NativeMenu _menu = new();

    public TrayIcon(AppHost host)
    {
        _host = host;
        _icon = new Avalonia.Controls.TrayIcon { ToolTipText = AppPaths.ProductTitle, Menu = _menu, IsVisible = true };
        _icon.Clicked += (_, _) => _host.ShowMainWindow();
        // the server list may have changed since the menu was built
        _menu.Opening += (_, _) => BuildMenu();
        if (Application.Current is { } app) Avalonia.Controls.TrayIcon.SetIcons(app, [_icon]);
        BuildMenu();
        UpdateState();
    }

    public void UpdateState()
    {
        var unlocked = _host.Vault.IsUnlocked;
        _icon.Icon = IconFactory.Create(unlocked, 64);
        var agent = _host.Agent.PipeName ?? L.Get("Tray.AgentNotRunning");
        _icon.ToolTipText = $"{AppPaths.ProductTitle} — {L.Get(unlocked ? "Tray.Unlocked" : "Tray.Locked")}\n{L.Get("Tray.Agent")} {agent}";
        BuildMenu();
    }

    /// <summary>A notice in the corner of the screen; a click on it opens the main window.</summary>
    public void Balloon(string title, string text, bool warning = false) => Toast.Show(title, text, warning, _host.ShowMainWindow);

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(Item(L.Get("Tray.Open"), _host.ShowMainWindow));

        var connect = new NativeMenuItem(L.Get("Tray.Connect")) { Menu = new NativeMenu() };
        if (_host.Vault.IsUnlocked)
        {
            var servers = _host.Vault.Data.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var group in servers.GroupBy(s => s.Group ?? "").OrderBy(g => g.Key))
            {
                var target = connect.Menu;
                if (!string.IsNullOrWhiteSpace(group.Key))
                {
                    // nested groups "a/b" become nested submenus
                    foreach (var part in group.Key.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var sub = target.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Menu != null && i.Header == part);
                        if (sub == null)
                        {
                            sub = new NativeMenuItem(part) { Menu = new NativeMenu() };
                            target.Items.Add(sub);
                        }
                        target = sub.Menu!;
                    }
                }
                foreach (var s in group) target.Items.Add(ServerItem(s));
            }
            if (servers.Count == 0) connect.Menu.Items.Add(new NativeMenuItem(L.Get("Tray.NoServers")) { IsEnabled = false });
        }
        else
        {
            connect.Menu.Items.Add(Item(L.Get("Tray.Unlock"), _host.ShowMainWindow));
        }
        _menu.Items.Add(connect);
        _menu.Items.Add(new NativeMenuItemSeparator());

        var language = new NativeMenuItem(L.Get("Tray.Language")) { Menu = new NativeMenu() };
        var configured = _host.SettingsStore.Settings.Language;
        language.Menu.Items.Add(Check(L.Get("Lang.System"), configured == null, () => _host.SetLanguage(null)));
        language.Menu.Items.Add(Check("Русский", configured == L.Russian, () => _host.SetLanguage(L.Russian)));
        language.Menu.Items.Add(Check("English", configured == L.English, () => _host.SetLanguage(L.English)));
        _menu.Items.Add(language);
        _menu.Items.Add(new NativeMenuItemSeparator());

        _menu.Items.Add(_host.Vault.IsUnlocked ? Item(L.Get("Tray.Lock"), _host.Lock) : Item(L.Get("Tray.Unlock"), _host.ShowMainWindow));
        _menu.Items.Add(new NativeMenuItemSeparator());
        _menu.Items.Add(Item(L.Get("Tray.Exit"), _host.Exit));
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Check(string header, bool isChecked, Action action)
    {
        var item = Item(header, action);
        item.ToggleType = MenuItemToggleType.Radio;
        item.IsChecked = isChecked;
        return item;
    }

    private NativeMenuItem ServerItem(ServerEntry s)
    {
        var item = Item($"{s.Name}    {s.Display}", () => _host.Connect(s.Id));
        item.ToolTip = L.Get(s.Auth switch { AuthMode.Key => "Tray.ByKey", AuthMode.None => "Auth.SsmShell", _ => "Tray.ByPassword" });
        return item;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
        Toast.CloseAll();
    }
}
