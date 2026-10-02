using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace SnmpMibBrowser;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    static App()
    {
        // Show the entered auth/privacy key as a tooltip while hovering any PasswordBox.
        EventManager.RegisterClassHandler(typeof(PasswordBox), UIElement.MouseEnterEvent, new System.Windows.Input.MouseEventHandler((s, _) =>
        {
            var box = (PasswordBox)s;
            box.ToolTip = string.IsNullOrEmpty(box.Password) ? null : box.Password;
        }));
    }
}

