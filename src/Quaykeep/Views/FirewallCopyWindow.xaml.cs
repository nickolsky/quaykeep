using System.Windows;
using Quaykeep.Core.Models;

namespace Quaykeep.Views;

/// <summary>Picks the servers that get a copy of a server's firewall rules, and whether they add to or replace theirs.</summary>
public partial class FirewallCopyWindow : Window
{
    private sealed class Choice(ServerEntry server)
    {
        public ServerEntry Server { get; } = server;
        public bool Checked { get; set; }
        public string Label => (string.IsNullOrWhiteSpace(Server.Group) ? "" : Server.Group + " / ") + $"{Server.Name}  ({Server.Host})";
    }

    private readonly List<Choice> _choices;

    public FirewallCopyWindow(string sourceName, IReadOnlyList<ServerEntry> servers)
    {
        InitializeComponent();
        HintText.Text = L.F("Fw.CopyHint", sourceName);
        _choices = servers.Select(s => new Choice(s)).ToList();
        ServersList.ItemsSource = _choices;
    }

    public List<ServerEntry> Chosen { get; private set; } = [];
    public bool Replace => ReplaceRadio.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Chosen = _choices.Where(c => c.Checked).Select(c => c.Server).ToList();
        DialogResult = true;
    }
}
