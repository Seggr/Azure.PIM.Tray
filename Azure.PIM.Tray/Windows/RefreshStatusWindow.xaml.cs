using System.Collections.ObjectModel;
using System.Windows;
using Azure.PIM.Tray.Models;

namespace Azure.PIM.Tray.Windows;

public partial class RefreshStatusWindow : Window
{
    public RefreshStatusWindow(ObservableCollection<TenantStatusItem> items)
    {
        InitializeComponent();
        Azure.PIM.Tray.Services.WindowIconHelper.ApplyRefreshIcon(this);
        StatusList.ItemsSource = items;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
