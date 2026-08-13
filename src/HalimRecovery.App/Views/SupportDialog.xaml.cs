using System.Diagnostics;
using System.Windows;

namespace HalimRecovery.App.Views;

public partial class SupportDialog : Window
{
    public SupportDialog() => InitializeComponent();

    private void OpenKofi(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://ko-fi.com/htoklu") { UseShellExecute = true });

    private void CopyIban(object sender, RoutedEventArgs e) => Copy(IbanText.Text, "IBAN copied.");
    private void CopyUsdt(object sender, RoutedEventArgs e) => Copy(UsdtText.Text, "USDT address copied.");
    private void CopyEmail(object sender, RoutedEventArgs e) => Copy(EmailText.Text, "Email copied.");
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void Copy(string text, string note)
    {
        try
        {
            Clipboard.SetText(text);
            CopiedNote.Text = note;
        }
        catch
        {
            CopiedNote.Text = "Clipboard is busy — please try again.";
        }
    }
}
