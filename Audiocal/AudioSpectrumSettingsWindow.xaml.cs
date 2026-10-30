using System.Windows;
using System.Windows.Input;

namespace Audiocal
{
    public partial class AudioSpectrumSettingsWindow : Window
    {
        public event Action<bool>? PreAnalyzeBeforePlayChanged;

        public AudioSpectrumSettingsWindow(
            AudioSpectrumOptions spectrumOptions,
            bool preAnalyzeBeforePlay)
        {
            InitializeComponent();
            DataContext = spectrumOptions;
            PreAnalyzeToggleButton.IsChecked = preAnalyzeBeforePlay;
        }

        private void PreAnalyzeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            PreAnalyzeBeforePlayChanged?.Invoke(PreAnalyzeToggleButton.IsChecked == true);
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
