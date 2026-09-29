using System.Windows;
using Quaykeep.Core.Firewall;
using Quaykeep.Core.Models;
using Quaykeep.Services;

namespace Quaykeep.Views;

/// <summary>Named firewall rule sets: create, rename, delete, and edit their rules (in <see cref="FirewallWindow"/>).</summary>
public partial class FirewallPresetsWindow : Window
{
    private sealed record Item(FirewallPreset Preset, string Name, string Usage);

    private readonly AppHost _host;

    public FirewallPresetsWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        Show(null);
    }

    private void Show(Guid? select)
    {
        var items = _host.Vault.Read(d => d.FirewallPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new Item(p.Clone(), p.Name, L.F("Fw.PresetUsage", p.Rules.Count, FirewallSets.UsingPreset(d, p.Id).Count))).ToList());
        PresetsList.ItemsSource = items;
        PresetsList.SelectedItem = items.FirstOrDefault(i => i.Preset.Id == select) ?? items.FirstOrDefault();
    }

    private FirewallPreset? Selected => (PresetsList.SelectedItem as Item)?.Preset;

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, Title, L.Get("Fw.PresetName"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var preset = new FirewallPreset { Name = name };
        _host.Vault.Update(d => d.FirewallPresets.Add(preset));
        Show(preset.Id);
        new FirewallWindow(_host, preset.Clone()) { Owner = this }.ShowDialog();
        Show(preset.Id);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var name = InputDialog.Ask(this, Title, L.Get("Fw.PresetName"), p.Name)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _host.Vault.Update(d =>
        {
            if (d.FirewallPresets.FirstOrDefault(x => x.Id == p.Id) is { } x) x.Name = name;
        });
        Show(p.Id);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        var used = _host.Vault.Read(d => FirewallSets.UsingPreset(d, p.Id).Count);
        if (MessageBox.Show(this, L.F("Fw.ConfirmDeletePreset", p.Name, used), Title, MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _host.Vault.Update(d =>
        {
            d.FirewallPresets.RemoveAll(x => x.Id == p.Id);
            foreach (var s in d.Servers) s.Firewall?.Presets.Remove(p.Id);
        });
        Show(null);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } p) return;
        new FirewallWindow(_host, p) { Owner = this }.ShowDialog();
        Show(p.Id);
    }
}
