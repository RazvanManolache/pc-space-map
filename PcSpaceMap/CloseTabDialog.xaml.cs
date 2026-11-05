using System.Windows;

namespace PcSpaceMap;

public partial class CloseTabDialog : Window
{
    public CloseTabDialog(string tabTitle, string path)
    {
        InitializeComponent();
        Prompt.Text = $"Close the “{tabTitle}” tab?";
        PathText.Text = path;
        if (string.IsNullOrEmpty(path)) PathText.Visibility = Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
