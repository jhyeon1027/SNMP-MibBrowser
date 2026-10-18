using System.Windows;
using System.Windows.Controls;

namespace SnmpMibBrowser;

// Edits an ordered SNMP v1/v2c community list as plain text, one entry per line,
// so long lists can be pasted in one go.
public partial class CommunityListWindow : Window
{
    private readonly bool _de;
    public List<string> Result { get; private set; } = [];

    public CommunityListWindow(IEnumerable<string> communities, string language)
    {
        InitializeComponent(); _de = language == "de";
        Title = _de ? "Community-Liste" : "Community list";
        HintText.Text = _de ? "Eine Community pro Zeile. Sie werden von oben nach unten versucht, bis das Gerät antwortet." : "One community per line. They are tried from top to bottom until the device answers.";
        CancelListButton.Content = _de ? "Abbrechen" : "Cancel";
        CommunitiesText.Text = string.Join(Environment.NewLine, communities);
        CommunitiesText.Focus(); CommunitiesText.CaretIndex = CommunitiesText.Text.Length;
    }

    public static List<string> Parse(string text) =>
        text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private void CommunitiesText_TextChanged(object sender, TextChangedEventArgs e)
    {
        var count = Parse(CommunitiesText.Text).Count;
        CountText.Text = _de ? $"{count} Communities" : $"{count} communities";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { Result = Parse(CommunitiesText.Text); DialogResult = true; }
}
