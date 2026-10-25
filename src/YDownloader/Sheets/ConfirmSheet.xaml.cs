using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace YDownloader.Sheets;

/// <summary>Rückfrage als Sheet statt MessageBox.</summary>
public partial class ConfirmSheet : UserControl
{
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly MainWindow _host;

    private ConfirmSheet(MainWindow host, string heading, string message, string ok)
    {
        InitializeComponent();
        _host = host;
        Heading.Text = heading;
        Message.Text = message;
        Ok.Content = ok;
    }

    public static Task<bool> AskAsync(MainWindow host, string heading, string message, string ok)
    {
        var sheet = new ConfirmSheet(host, heading, message, ok);
        host.ShowSheet(sheet, sheet.Ok);
        return sheet._result.Task;
    }

    private void Close(bool result)
    {
        _host.CloseSheet();
        _result.TrySetResult(result);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
}
