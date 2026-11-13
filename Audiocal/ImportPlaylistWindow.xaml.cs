using System.Windows;
using System.Windows.Input;

namespace Audiocal
{
    public partial class ImportPlaylistWindow : Window
    {
        public string Url { get; private set; } = "";

        public ImportPlaylistWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => UrlTextBox.Focus();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            string url = MainWindow.NormalizeYouTubeUrl(UrlTextBox.Text.Trim());
            if (!MainWindow.IsYouTubePlaylistUrl(url) && !MainWindow.IsYouTubeVideoUrl(url))
            {
                ErrorText.Text = "Enter a valid YouTube song or playlist URL.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            Url = url;
            DialogResult = true;
        }

        private void UrlTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ImportButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
