using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace XpsManager.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Activity.DataContext = _vm.Live;
        MaxHeight = SystemParameters.WorkArea.Height;
        SourceInitialized += (_, _) => PaintTitleBar();
        Loaded += async (_, _) => await Task.WhenAll(_vm.LoadAsync(), _vm.Live.StartAsync());
        Closed += (_, _) => _vm.Live.Dispose();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _vm.Live.Pause();
            else _vm.Live.Resume();
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5 && _vm.CanRefresh)
            {
                e.Handled = true;
                await _vm.LoadAsync();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2)
            {
                e.Handled = true;
                (e.Key == Key.D1 ? SettingsTab : ActivityTab).IsChecked = true;
            }
        };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.LoadAsync();

    async void Apply_Click(object sender, RoutedEventArgs e) => await _vm.ApplyAsync();

    void Revert_Click(object sender, RoutedEventArgs e) => _vm.Revert();

    // Run the real Windows title bar in graphite so it reads as the top of the readout band.
    // Windows 11 only; older builds ignore these attributes.
    void PaintTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetDwmColor(hwnd, DwmwaCaptionColor, (Color)FindResource("GraphiteColor"));
        SetDwmColor(hwnd, DwmwaBorderColor, (Color)FindResource("GraphiteColor"));
        SetDwmColor(hwnd, DwmwaTextColor, (Color)FindResource("GraphiteInkColor"));
    }

    const int DwmwaBorderColor = 34;
    const int DwmwaCaptionColor = 35;
    const int DwmwaTextColor = 36;

    static void SetDwmColor(IntPtr hwnd, int attribute, Color color)
    {
        var colorRef = color.R | (color.G << 8) | (color.B << 16); // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
