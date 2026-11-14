using System.ServiceProcess;
using System.Windows;
using XpsManager.Core;

namespace XpsManager.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--service"])
        {
            ServiceBase.Run(new XpsManagerService());
            return;
        }
        if (args is ["--install-service"] or ["--remove-service"] or ["--update-service"])
        {
            try
            {
                if (args[0] == "--install-service") ServiceInstaller.Install(AutoSwitchStore.Read() with { Enabled = true });
                else if (args[0] == "--remove-service") ServiceInstaller.Remove();
                else ServiceInstaller.Update();
            }
            catch (Exception e)
            {
                Environment.ExitCode = 1;
                // Do not write elevated diagnostics through a user-controlled path or junction.
                System.Diagnostics.Trace.TraceError(e.ToString());
            }
            return;
        }
        if (args.Length != 0)
        {
            MessageBox.Show("Open without arguments, or use --service through Windows Service Control Manager.", "XPS Manager");
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
