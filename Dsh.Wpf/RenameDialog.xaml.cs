using System.Windows;

namespace Dsh.Wpf;

public partial class RenameDialog : Window
{
    public string NewTitle { get; private set; } = "";

    public RenameDialog(string currentTitle)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        TitleBox.SelectAll();
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        NewTitle = TitleBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
