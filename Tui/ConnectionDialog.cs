#pragma warning disable CS0618 // TextView (multi-line community list)
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SnmpMibBrowser.Tui;

/// <summary>
/// All SNMP settings of the session: profile, version, port, ordered community list, v3 credentials, timeout and retries.
/// "Save to profile" also stores them in the selected profile. The target host is never part of a profile.
/// </summary>
public sealed class ConnectionDialog : Dialog
{
    public static readonly string[] Versions = ["v1", "v2c", "v3"];
    public static readonly string[] AuthTypes = ["None", "MD5", "SHA-1", "SHA-256", "SHA-384", "SHA-512"];
    public static readonly string[] PrivacyTypes = ["None", "DES", "AES-128", "AES-192", "AES-256"];

    private readonly UserSettings _settings;
    private readonly DropDownList _profile, _version, _auth, _privacy;
    private readonly TextField _port, _user, _authKey, _privKey, _context, _timeout, _retries;
    private readonly TextView _communities;
    private readonly Label _error;

    /// <summary>Button indexes: 0 Cancel, 1 Save to profile, 2 Apply.</summary>
    public bool SaveToProfile => Result == 1;
    public SnmpProfile Connection { get; private set; } = new();
    public string ProfileName => _profile.Text;

    public ConnectionDialog(UserSettings settings, string profileName, SnmpProfile current)
    {
        _settings = settings;
        Title = "Connection settings (F8)";
        const int x = 16;
        var y = 0;
        Label L(string text, int row) => new() { Text = text, X = 0, Y = row };

        _profile = Dialogs.Choice(settings.Profiles.Select(p => p.Name), profileName, 24);
        _profile.X = x; _profile.Y = y;
        var newProfile = new Button { Text = "New…", X = Pos.Right(_profile) + 1, Y = y };
        var deleteProfile = new Button { Text = "Delete", X = Pos.Right(newProfile) + 1, Y = y };
        Add(L("Profile", y), _profile, newProfile, deleteProfile);

        y += 2;
        _version = Dialogs.Choice(Versions, current.Version, 8); _version.X = x; _version.Y = y;
        _port = new TextField { X = x + 20, Y = y, Width = 7 };
        Add(L("Version", y), _version, new Label { Text = "Port", X = x + 14, Y = y }, _port);

        y += 2;
        _communities = new TextView { X = x, Y = y, Width = 40, Height = 4 };
        Add(L("Communities", y), L("(one per line,", y + 1), L(" tried in order)", y + 2), _communities);

        y += 5;
        _user = new TextField { X = x, Y = y, Width = 24 };
        _context = new TextField { X = x + 35, Y = y, Width = 14 };
        Add(L("v3 user", y), _user, new Label { Text = "Context", X = x + 26, Y = y }, _context);
        y += 1;
        _auth = Dialogs.Choice(AuthTypes, current.AuthType, 10); _auth.X = x; _auth.Y = y;
        _authKey = new TextField { X = x + 20, Y = y, Width = 29, Secret = true };
        Add(L("v3 auth", y), _auth, new Label { Text = "Key", X = x + 15, Y = y }, _authKey);
        y += 1;
        _privacy = Dialogs.Choice(PrivacyTypes, current.PrivacyType, 10); _privacy.X = x; _privacy.Y = y;
        _privKey = new TextField { X = x + 20, Y = y, Width = 29, Secret = true };
        Add(L("v3 privacy", y), _privacy, new Label { Text = "Key", X = x + 15, Y = y }, _privKey);

        y += 2;
        _timeout = new TextField { X = x, Y = y, Width = 7 };
        _retries = new TextField { X = x + 20, Y = y, Width = 4 };
        Add(L("Timeout (ms)", y), _timeout, new Label { Text = "Retries", X = x + 11, Y = y }, _retries);

        y += 2;
        _error = new Label { X = 0, Y = y, Width = Dim.Fill(), Text = "" };
        Add(_error);

        Load(current);
        _profile.TextChanged += (_, _) => { if (FindProfile(_profile.Text) is { } p) Load(p); };
        newProfile.Accepting += (_, e) => { e.Handled = true; AddProfile(); };
        deleteProfile.Accepting += (_, e) => { e.Handled = true; DeleteProfile(); };

        AddButton(new Button { Text = "Cancel" });
        AddButton(new Button { Text = "_Save to profile" });
        AddButton(new Button { Text = "_Apply" });
    }

    private SnmpProfile? FindProfile(string name) => _settings.Profiles.FirstOrDefault(p => p.Name == name);

    private void Load(SnmpProfile p)
    {
        _version.Text = p.Version; _port.Text = p.Port.ToString();
        _communities.Text = string.Join("\n", p.CommunityList().Where(c => c.Length > 0));
        _user.Text = p.User; _auth.Text = p.AuthType; _authKey.Text = p.AuthPassword;
        _privacy.Text = p.PrivacyType; _privKey.Text = p.PrivacyPassword; _context.Text = p.Context;
        _timeout.Text = p.Timeout.ToString(); _retries.Text = p.Retries.ToString();
    }

    /// <summary>Validates the fields into <see cref="Connection"/>; the caller reopens the dialog on an error.</summary>
    public bool TryRead(out string error)
    {
        error = "";
        if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535) { error = "Invalid port (1-65535)."; return false; }
        if (!int.TryParse(_timeout.Text, out var timeout) || timeout is < 100 or > 120000) { error = "Invalid timeout (100-120000 ms)."; return false; }
        if (!int.TryParse(_retries.Text, out var retries) || retries is < 0 or > 10) { error = "Invalid retries (0-10)."; return false; }
        var communities = _communities.Text.Split('\n').Select(c => c.Trim('\r')).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (_version.Text != "v3" && communities.Count == 0) { error = "Enter at least one community."; return false; }
        Connection = new SnmpProfile
        {
            Name = _profile.Text, Port = port, Version = _version.Text, Communities = communities, Community = communities.FirstOrDefault() ?? "",
            User = _user.Text, AuthType = _auth.Text, AuthPassword = _authKey.Text, PrivacyType = _privacy.Text, PrivacyPassword = _privKey.Text,
            Context = _context.Text, Timeout = timeout, Retries = retries
        };
        return true;
    }

    private void AddProfile()
    {
        var name = Dialogs.Input(App!, "New profile", "Name of the new profile (starts with the values shown):");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (FindProfile(name) != null) { _error.Text = $"Profile \"{name}\" already exists."; return; }
        if (!TryRead(out var error)) { _error.Text = error; return; }
        var profile = Connection; profile.Name = name;
        _settings.Profiles.Add(profile);
        _profile.Source = new ListWrapper<string>(new(_settings.Profiles.Select(p => p.Name)));
        _profile.Text = name;
        _error.Text = $"Profile \"{name}\" created.";
    }

    private void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1) { _error.Text = "The last profile cannot be deleted."; return; }
        var name = _profile.Text;
        if (!Dialogs.Confirm(App!, "Delete profile", $"Delete profile \"{name}\"?")) return;
        _settings.Profiles.RemoveAll(p => p.Name == name);
        _profile.Source = new ListWrapper<string>(new(_settings.Profiles.Select(p => p.Name)));
        _profile.Text = _settings.Profiles[0].Name;
        _error.Text = $"Profile \"{name}\" deleted.";
    }
}
