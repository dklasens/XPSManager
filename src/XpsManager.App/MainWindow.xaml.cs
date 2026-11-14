using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace XpsManager.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly DispatcherTimer _serviceStatusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Activity.DataContext = _vm.Live;
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        MinWidth = Math.Min(MinWidth, MaxWidth);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        SourceInitialized += (_, _) => PaintTitleBar();
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(_vm.LoadAsync(), _vm.Live.StartAsync());
            await _vm.Automatic.LoadAsync();
            if (WindowState != WindowState.Minimized) _serviceStatusTimer.Start();
        };
        _serviceStatusTimer.Tick += async (_, _) => { _vm.Automatic.RefreshStatus(); await _vm.RefreshChargeBoostAsync(); };
        Activated += async (_, _) =>
        {
            if (!_vm.IsLoaded || _vm.IsBusy) return;
            await _vm.RefreshOnActivationAsync();
            _vm.Automatic.RefreshStatus();
        };
        Closed += (_, _) => { _serviceStatusTimer.Stop(); _vm.Live.Dispose(); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                _vm.Live.Pause();
                _serviceStatusTimer.Stop();
            }
            else
            {
                _vm.Live.Resume();
                _serviceStatusTimer.Start();
            }
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5 && _vm.CanRefresh)
            {
                e.Handled = true;
                await _vm.LoadAsync();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3)
            {
                e.Handled = true;
                (e.Key == Key.D1 ? SettingsTab : e.Key == Key.D2 ? AutomaticTab : ActivityTab).IsChecked = true;
            }
        };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await _vm.LoadAsync();

    async void Apply_Click(object sender, RoutedEventArgs e) => await _vm.ApplyAsync();

    async void ChargeBoost_Click(object sender, RoutedEventArgs e) => await _vm.SetChargeBoostAsync(true);

    async void ReturnCharge_Click(object sender, RoutedEventArgs e) => await _vm.SetChargeBoostAsync(false);

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
