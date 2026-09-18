using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SnmpMibBrowser;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<SnmpProfile> _profiles;
    private readonly string _language;
    public UserSettings ResultSettings { get; }

    public SettingsWindow(UserSettings settings, string language)
    {
        InitializeComponent(); _language = language;
        ResultSettings = new UserSettings { Language=settings.Language, Dark=settings.Dark, MibFolder=settings.MibFolder, SelectedProfile=settings.SelectedProfile, GitHubRepository=settings.GitHubRepository, IncludePrereleases=settings.IncludePrereleases,
            Profiles=settings.Profiles.Select(Clone).ToList() };
        _profiles = new(ResultSettings.Profiles); ProfilesList.ItemsSource = _profiles; MibFolderBox.Text = settings.MibFolder;
        Localize(); if (_profiles.Count > 0) ProfilesList.SelectedIndex = 0;
    }

    private static SnmpProfile Clone(SnmpProfile p) => new() { Name=p.Name, Port=p.Port, Version=p.Version, Community=p.Community, User=p.User, AuthType=p.AuthType, AuthPassword=p.AuthPassword, PrivacyType=p.PrivacyType, PrivacyPassword=p.PrivacyPassword, Context=p.Context };
    private static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Keine";
    private static void Select(ComboBox box, string value) { foreach (ComboBoxItem i in box.Items) if (i.Content?.ToString() == value) { box.SelectedItem=i; return; } box.SelectedIndex=0; }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is not SnmpProfile p) return;
        NameBox.Text=p.Name; PortSettingsBox.Text=p.Port.ToString(); Select(VersionSettingsBox,p.Version); CommunitySettingsBox.Text=p.Community; UserSettingsBox.Text=p.User; ContextSettingsBox.Text=p.Context; Select(AuthSettingsBox,p.AuthType); AuthPassBox.Password=p.AuthPassword; Select(PrivacySettingsBox,p.PrivacyType); PrivacyPassBox.Password=p.PrivacyPassword;
    }
    private void New_Click(object sender, RoutedEventArgs e) { ProfilesList.SelectedItem=null; NameBox.Text=""; PortSettingsBox.Text="161"; VersionSettingsBox.SelectedIndex=1; CommunitySettingsBox.Text="public"; UserSettingsBox.Text=""; ContextSettingsBox.Text=""; AuthSettingsBox.SelectedIndex=0; AuthPassBox.Password=""; PrivacySettingsBox.SelectedIndex=0; PrivacyPassBox.Password=""; NameBox.Focus(); }
    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || !int.TryParse(PortSettingsBox.Text,out var port) || port is < 1 or > 65535) { MessageBox.Show(_language=="de"?"Name und ein gültiger SNMP-Port sind erforderlich.":"Name and a valid SNMP port are required."); return; }
        var p=ProfilesList.SelectedItem as SnmpProfile ?? new SnmpProfile();
        p.Name=NameBox.Text.Trim();p.Port=port;p.Version=Selected(VersionSettingsBox);p.Community=CommunitySettingsBox.Text;p.User=UserSettingsBox.Text;p.Context=ContextSettingsBox.Text;p.AuthType=Selected(AuthSettingsBox);p.AuthPassword=AuthPassBox.Password;p.PrivacyType=Selected(PrivacySettingsBox);p.PrivacyPassword=PrivacyPassBox.Password;
        if (!_profiles.Contains(p)) _profiles.Add(p); ProfilesList.Items.Refresh(); ProfilesList.SelectedItem=p;
    }
    private void Delete_Click(object sender, RoutedEventArgs e) { if (ProfilesList.SelectedItem is SnmpProfile p && MessageBox.Show((_language=="de"?"Profil löschen: ":"Delete profile: ")+p.Name,"SNMP MibBrowser",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes) _profiles.Remove(p); }
    private void Browse_Click(object sender, RoutedEventArgs e) { var d=new OpenFolderDialog{Title=_language=="de"?"Standard-MIB-Ordner":"Default MIB folder"}; if(d.ShowDialog()==true)MibFolderBox.Text=d.FolderName; }
    private void Ok_Click(object sender, RoutedEventArgs e) { ResultSettings.MibFolder=MibFolderBox.Text.Trim();ResultSettings.Profiles=_profiles.ToList();DialogResult=true; }
    private void Localize() { var de=_language=="de";Title=de?"Optionen":"Options";MibFolderGroup.Header=de?"Standard-MIB-Ordner":"Default MIB folder";BrowseButton.Content=de?"Durchsuchen…":"Browse…";ProfilesGroup.Header=de?"SNMP-Profile":"SNMP profiles";EditorGroup.Header=de?"Profil bearbeiten":"Profile editor";NewButton.Content=de?"Neu":"New";DeleteButton.Content=de?"Löschen":"Delete";SaveProfileButton.Content=de?"Profil speichern":"Save profile";CancelSettingsButton.Content=de?"Abbrechen":"Cancel";UserSettingsLabel.Text=de?"v3-Benutzer":"v3 user";AuthSettingsLabel.Text=de?"Authentifizierung":"Authentication";PrivacySettingsLabel.Text=de?"Verschlüsselung":"Encryption"; }
}
