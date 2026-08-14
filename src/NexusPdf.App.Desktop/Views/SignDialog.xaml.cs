using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Microsoft.Win32;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Signing;

namespace NexusPdf.App.Desktop.Views;

public sealed record SignRequest(X509Certificate2 Certificate, string Reason, string Location);

public partial class SignDialog : Window
{
    private sealed class Row
    {
        public required X509Certificate2 Certificate { get; init; }
        public string Display =>
            $"{Certificate.GetNameInfo(X509NameType.SimpleName, false)} (до {Certificate.NotAfter:d})";
    }

    private SignRequest? _result;

    private SignDialog()
    {
        InitializeComponent();
        LoadStoreCertificates();
    }

    public static SignRequest? Show(Window? owner)
    {
        var dialog = new SignDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void LoadStoreCertificates()
    {
        try
        {
            var rows = CertificateSource.FromPersonalStore()
                .Select(c => new Row { Certificate = c })
                .ToList();
            CertCombo.ItemsSource = rows;
            if (rows.Count > 0)
                CertCombo.SelectedIndex = 0;
            NoCertsLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Хранилище сертификатов недоступно");
            NoCertsLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnPickPfx(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.Get("PfxFilter") };
        if (dialog.ShowDialog(this) != true) return;

        var password = PasswordDialog.Show(this, System.IO.Path.GetFileName(dialog.FileName), false) ?? "";
        try
        {
            var certificate = CertificateSource.FromPfx(dialog.FileName, password);
            var row = new Row { Certificate = certificate };
            CertCombo.ItemsSource = new[] { row };
            CertCombo.SelectedIndex = 0;
            NoCertsLabel.Visibility = Visibility.Collapsed;
            ErrorLabel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = Loc.Get("SignPfxError") + " " + ex.Message;
            ErrorLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnSign(object sender, RoutedEventArgs e)
    {
        if (CertCombo.SelectedItem is not Row row)
        {
            ErrorLabel.Text = Loc.Get("SignNoCertSelected");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        if (!row.Certificate.HasPrivateKey)
        {
            ErrorLabel.Text = Loc.Get("SignNoPrivateKey");
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }
        _result = new SignRequest(row.Certificate, ReasonBox.Text.Trim(), LocationBox.Text.Trim());
        DialogResult = true;
    }
}
