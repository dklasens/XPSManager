using System.ServiceProcess;
using System.Windows;
using System.IO;
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
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XpsManager");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "service-operation-error.txt"), e.ToString());
                Environment.ExitCode = 1;
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
