using System.Windows;
using System.Windows.Input;

namespace WingetInstaller;

public partial class AppDialog : Window
{
    public AppDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public static void Show(Window owner, string message)
    {
        var dialog = new AppDialog(message) { Owner = owner };
        dialog.ShowDialog();
    }

    void Ok_Click(object sender, RoutedEventArgs e) => Close();

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}