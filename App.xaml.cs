using System.Runtime.InteropServices;
using System.Windows;

namespace WingetInstaller;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID("PHVR.AppInstall");
        }
        catch
        {
            // Não impede a inicialização caso o Shell não aceite o AppUserModelID.
        }

        base.OnStartup(e);
    }
}
