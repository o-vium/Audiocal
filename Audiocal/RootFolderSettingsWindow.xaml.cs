using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Audiocal
{
    public partial class RootFolderSettingsWindow : Window
    {
        public event Func<string, string, Task<bool>>? PathsApplyRequested;
        public event Action<string>? ProfilesImportRequested;
        public event Action<string>? ProfilesExportRequested;

        public RootFolderSettingsWindow(string currentRootFolder, string? currentYtDlpPath)
        {
            InitializeComponent();
            RootFolderTextBox.Text = currentRootFolder;
            YtDlpPathTextBox.Text = currentYtDlpPath ?? "";
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = "Choose Audiocal library root folder",
                FolderName = Directory.Exists(RootFolderTextBox.Text)
                    ? RootFolderTextBox.Text
                    : ""
            };

            if (folderDialog.ShowDialog(this) == true)
            {
                RootFolderTextBox.Text = folderDialog.FolderName;
                ErrorText.Visibility = Visibility.Collapsed;
            }
        }

        private void BrowseYtDlpButton_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = YtDlpPathTextBox.Text.Trim();
            string? currentDirectory = Path.GetDirectoryName(currentPath);
            var fileDialog = new OpenFileDialog
            {
                Title = "Choose yt-dlp executable",
                Filter = "yt-dlp executable (yt-dlp.exe)|yt-dlp.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                FileName = File.Exists(currentPath) ? currentPath : "yt-dlp.exe"
            };

            if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            {
                fileDialog.InitialDirectory = currentDirectory;
            }

            if (fileDialog.ShowDialog(this) == true)
            {
                YtDlpPathTextBox.Text = fileDialog.FileName;
                ErrorText.Visibility = Visibility.Collapsed;
            }
        }

        private void ImportProfilesButton_Click(object sender, RoutedEventArgs e)
        {
            var fileDialog = new OpenFileDialog
            {
                Title = "Import colour profiles",
                Filter = "Audiocal colour profiles (*.json)|*.json|JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true
            };

            if (fileDialog.ShowDialog(this) == true)
            {
                ProfilesImportRequested?.Invoke(fileDialog.FileName);
            }
        }

        private void ExportProfilesButton_Click(object sender, RoutedEventArgs e)
        {
            var fileDialog = new SaveFileDialog
            {
                Title = "Export colour profiles",
                Filter = "Audiocal colour profiles (*.json)|*.json|JSON files (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = "Audiocal-colour-profiles.json",
                OverwritePrompt = true
            };

            if (fileDialog.ShowDialog(this) == true)
            {
                ProfilesExportRequested?.Invoke(fileDialog.FileName);
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            string rootFolder = RootFolderTextBox.Text.Trim();
            string ytDlpPath = YtDlpPathTextBox.Text.Trim();
            if (!Directory.Exists(rootFolder))
            {
                ErrorText.Text = "Choose an existing library folder.";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (PathsApplyRequested != null &&
                await PathsApplyRequested.Invoke(rootFolder, ytDlpPath))
            {
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
