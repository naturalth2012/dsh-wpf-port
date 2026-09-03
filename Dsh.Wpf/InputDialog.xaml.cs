using System.Windows;

namespace Dsh.Wpf;

public partial class InputDialog : Window
{
    public string Text { get; private set; } = "";

    /// <summary>Confirm button label; defaults to "确定". Callers may override (e.g. "连接").</summary>
    public string OkButtonText
    {
        set => OkButton.Content = value;
    }

    public InputDialog(string title, string prompt, string initialText = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialText;
        Loaded += (_, _) => InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Text = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
