using System.Windows;

namespace Notlar;

// Asks for a backup password; with confirmation when creating a backup.
public partial class PasswordDialog : Window
{
    private readonly bool confirm;
    public string Result { get; private set; } = "";
    public PasswordDialog(Window owner, string heading, string message, bool confirm)
    {
        this.confirm = confirm;
        Owner = owner;
        if (L10n.Current.RightToLeft) FlowDirection = FlowDirection.RightToLeft;
        InitializeComponent();
        Title = heading; Heading.Text = heading; Message.Text = message;
        Confirm.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Password.Focus();
    }
    private void SubmitClick(object sender, RoutedEventArgs e)
    {
        string password = Password.Password;
        if (confirm && password.Length < 12) { Fail(L10n.T("PasswordTooShort")); return; }
        if (confirm && password != Confirm.Password) { Fail(L10n.T("PasswordMismatch")); return; }
        if (password.Length == 0) { Fail(L10n.T("PasswordTooShort")); return; }
        Result = password; DialogResult = true;
    }
    private void Fail(string message) { Error.Text = message; Error.Visibility = Visibility.Visible; Password.Focus(); }
}
