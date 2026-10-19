using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace OpenMessages.Windows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _allowClose;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = _model = model;
        InputBindings.Add(new KeyBinding(model.RefreshCommand, Key.F5, ModifierKeys.None));
    }

    /// <summary>Lets the next Close() really close instead of hiding to the tray.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Keep running in the notification area so new codes still arrive.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async void UnlockClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Passphrase.Password) || _model.Busy) return;
        if (await _model.UnlockAsync(Passphrase.Password)) Passphrase.Clear();
    }

    private void PassphraseKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) UnlockClick(sender, e);
    }

    private void DeleteAllClick(object sender, RoutedEventArgs e)
    {
        if (_model.Codes.Count == 0) return;
        var answer = MessageBox.Show(this, "Delete all synced codes? They'll be removed from the server for every device.",
            "Delete all", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK) _model.DeleteAllCommand.Execute(null);
    }

    private void DismissStatus(object sender, RoutedEventArgs e) => _model.Status = null;
}
