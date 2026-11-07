using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace JobSpy.Desktop.Views;

public partial class StartupWindow : Window
{
    private TextBlock? _statusText;

    public StartupWindow()
    {
        InitializeComponent();
        _statusText = this.FindControl<TextBlock>("StatusText");
    }

    public void SetStatus(string message)
    {
        if (_statusText is not null)
        {
            _statusText.Text = message;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
