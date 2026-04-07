using System.Collections.ObjectModel;
using System.Windows;
using Azure.PIM.Tray.Models;
using Azure.PIM.Tray.Services;

namespace Azure.PIM.Tray.Windows;

public partial class RefreshStatusWindow : Window
{
    public RefreshStatusWindow(ObservableCollection<TenantStatusItem> items)
    {
        InitializeComponent();
        WindowIconHelper.ApplyRefreshIcon(this);
        WindowIconHelper.CenterOnActiveScreen(this);
        StatusList.ItemsSource = items;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
