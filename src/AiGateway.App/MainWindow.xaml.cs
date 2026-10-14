using System.Windows;

namespace AiGateway.App;

public partial class MainWindow : Window
{
    public MainWindow(Uri gatewayAddress)
    {
        InitializeComponent();
        AddressText.Text = gatewayAddress.ToString();
    }
}
