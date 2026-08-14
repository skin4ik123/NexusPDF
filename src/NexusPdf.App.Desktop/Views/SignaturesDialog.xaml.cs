using System.Text;
using System.Windows;
using System.Windows.Media;
using NexusPdf.App.Desktop.Localization;
using NexusPdf.Signing;

namespace NexusPdf.App.Desktop.Views;

public partial class SignaturesDialog : Window
{
    private sealed class Row
    {
        public required PdfSignatureInfo Info { get; init; }
        private bool CryptoOk => Info.IsCryptoValid && Info.CoversWholeDocument;
        // Зелёная галочка — только при доверенной цепочке; криптографически
        // верная, но недоверенная подпись — жёлтое предупреждение.
        public bool IsOk => CryptoOk && Info.IsTrusted;
        public string StatusGlyph => IsOk ? "\uE73E" : "\uE7BA"; // галочка / предупреждение
        public Brush StatusBrush => IsOk ? Brushes.SeaGreen
            : CryptoOk ? Brushes.DarkOrange : Brushes.IndianRed;

        public string Title =>
            Info.SignerName.Length > 0 ? Info.SignerName : Loc.Get("SignUnknownSigner");

        public string Details
        {
            get
            {
                var builder = new StringBuilder();
                builder.AppendLine(Info.IsCryptoValid
                    ? Loc.Get("SignCryptoOk")
                    : Loc.Get("SignCryptoBad"));
                builder.AppendLine(Info.CoversWholeDocument
                    ? Loc.Get("SignNotModified")
                    : Loc.Get("SignModifiedAfter"));
                builder.AppendLine(Info.IsTrusted
                    ? Loc.Get("SignTrusted")
                    : Loc.Get("SignUntrusted"));
                if (Info.SignTime is { } time)
                    builder.AppendLine(Loc.F("SignTimeLabel", time.ToLocalTime().ToString("g")));
                if (Info.Reason.Length > 0)
                    builder.AppendLine(Loc.F("SignReasonLabel", Info.Reason));
                if (Info.Location.Length > 0)
                    builder.AppendLine(Loc.F("SignLocationLabel", Info.Location));
                if (Info.Error is { } error)
                    builder.AppendLine(error);
                return builder.ToString().TrimEnd();
            }
        }
    }

    private SignaturesDialog(IReadOnlyList<PdfSignatureInfo> signatures)
    {
        InitializeComponent();
        SigList.ItemsSource = signatures.Select(s => new Row { Info = s }).ToList();
    }

    public static void Show(Window? owner, IReadOnlyList<PdfSignatureInfo> signatures)
    {
        var dialog = new SignaturesDialog(signatures);
        if (owner is { IsLoaded: true })
            dialog.Owner = owner;
        dialog.ShowDialog();
    }
}
