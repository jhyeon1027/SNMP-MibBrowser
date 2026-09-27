using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace SnmpMibBrowser;

public partial class UpdateWindow : Window
{
    private readonly UserSettings _settings;
    private readonly string _language;
    private readonly UpdateService _updates = new();
    private GitHubRelease? _release;

    public UpdateWindow(UserSettings settings, string language)
    {
        InitializeComponent(); _settings = settings; _language = language;
        PrereleaseBox.IsChecked = settings.IncludePrereleases;
        Localize(); Closing += (_, _) => SaveChoices();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        SaveChoices(); SetBusy(true); ReleaseText.Text = T("Suche nach Updates…", "Checking for updates…");
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            _release = await _updates.FindUpdateAsync(PrereleaseBox.IsChecked == true, new Version(current.Major, current.Minor, current.Build), CancellationToken.None);
            if (_release == null) ReleaseText.Text = T("Keine neuere passende Version gefunden.", "No newer matching version was found.");
            else ReleaseText.Text = $"{_release.Name} ({_release.Tag}){(_release.Prerelease ? "  [PRE-RELEASE]" : "")}\r\n\r\n{_release.Notes}";
            InstallButton.IsEnabled = ReleasePageButton.IsEnabled = _release != null;
        }
        catch (Exception ex) { ReleaseText.Text = T("Updateprüfung fehlgeschlagen: ", "Update check failed: ") + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        if (MessageBox.Show(T("Das Update wird heruntergeladen. Danach startet die Anwendung neu. Fortfahren?", "The update will be downloaded and the application will restart. Continue?"), T("Update installieren", "Install update"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true); DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<int>(x => DownloadProgress.Value = x);
            var file = await _updates.DownloadAsync(_release, progress, CancellationToken.None);
            UpdateService.InstallAfterExit(file); Application.Current.Shutdown();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, T("Update fehlgeschlagen", "Update failed"), MessageBoxButton.OK, MessageBoxImage.Error); SetBusy(false); }
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e) { if (_release != null) Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true }); }
    private void SaveChoices() { _settings.IncludePrereleases = PrereleaseBox.IsChecked == true; }
    private void SetBusy(bool busy) { CheckButton.IsEnabled = !busy; InstallButton.IsEnabled = !busy && _release != null; PrereleaseBox.IsEnabled = !busy; }
    private string T(string de, string en) => _language == "de" ? de : en;
    private void Localize() { var de = _language == "de"; Title = "Updates"; TitleText.Text = de ? "Anwendungsupdates" : "Application updates"; PrereleaseBox.Content = de ? "Vorabversionen einbeziehen" : "Include prereleases"; CheckButton.Content = de ? "Jetzt prüfen" : "Check now"; InstallButton.Content = de ? "Herunterladen und installieren" : "Download and install"; ReleasePageButton.Content = de ? "Release-Seite öffnen" : "Open release page"; CloseButton.Content = de ? "Schließen" : "Close"; ReleaseText.Text = de ? "Die Updateprüfung verwendet das offizielle SNMP-MibBrowser-Repository." : "Update checks use the official SNMP MibBrowser repository."; }
}
