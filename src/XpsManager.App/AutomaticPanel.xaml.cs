using System.Windows;
using System.Windows.Controls;

namespace XpsManager.App;

public partial class AutomaticPanel : UserControl
{
    public AutomaticPanel() => InitializeComponent();
    AutomaticViewModel Model => (AutomaticViewModel)DataContext;
    async void Toggle_Click(object sender, RoutedEventArgs e) => await Model.ToggleAsync(((CheckBox)sender).IsChecked == true);
    async void Save_Click(object sender, RoutedEventArgs e) => await Model.SaveAsync();
    async void Remove_Click(object sender, RoutedEventArgs e) => await Model.RemoveAsync();
    async void Update_Click(object sender, RoutedEventArgs e) => await Model.UpdateAsync();
}
