using Audiocal.Models;
using Audiocal.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using NAudio.Wave;



namespace Audiocal
{
    public partial class MainWindow : Window
    {
        // Variables
        private List<Song> songs = new List<Song>();
        private MediaPlayer player = new MediaPlayer();
        private Song? currentPlayingSong;
        private bool isPlaying = false;
        private DispatcherTimer timer;
        private List<Song> playbackHistory = new List<Song>();
        private DateTime currentSongStartedAt = DateTime.MinValue;
        private enum LoopMode{
            None,
            Library,
            Song    }
        private LoopMode loopMode = LoopMode.None;

        private MetadataService metadataService = new MetadataService();
        private MusicLibrary musicLibrary;
        private string? currentLibraryFolder;
        private TimeSpan pendingPlaybackPosition;
        private bool pendingWasPlaying;
        private bool songHasEnded = false;

        private List<Song> playbackOrder = new List<Song>();
        private bool isShuffleEnabled = false;
        private int playbackOrderIndex = -1;
        private DispatcherTimer downloadStatusTimer;

        private bool isDraggingSlider = false;
        private bool isDraggingVolumeSlider = false;
        private bool isPlaylistModeEnabled;
        private bool isPlaylistDownloadInProgress;
        private bool isMiniPlayerEnabled;
        private bool isMiniPlayerPinned;
        private bool isAudioSpectrumEnabled;
        private bool isPreparingSong;
        private string nowPlayingTitle = "No song selected";
        private string? nowPlayingArtist;
        private double savedWindowWidth;
        private double savedWindowHeight;
        private double savedWindowLeft;
        private double savedWindowTop;
        private double savedWindowMinWidth;
        private double savedWindowMinHeight;
        private WindowState savedWindowState;
        private SizeToContent savedSizeToContent;
        private ResizeMode savedResizeMode;
        private WindowStyle savedWindowStyle;
        private Brush? savedWindowBackground;
        private double previousVolume = 1.0;
        private bool volumeSettingsSaveErrorShown;
        private readonly DispatcherTimer volumeSettingsSaveTimer;
        private readonly DispatcherTimer audioSpectrumTimer;
        private readonly DispatcherTimer spectrumSettingsSaveTimer;
        private readonly DispatcherTimer themeColorHoverTimer;
        private volatile float spectrumBassLevel;
        private AudioFileReader? spectrumAudioReader;
        private string? spectrumAudioPath;
        private float[] spectrumSampleBuffer = new float[2048];
        private DateTime spectrumLastFrameTime;
        private double smoothedSpectrumBass;
        private float[] spectrumBassFilterState = Array.Empty<float>();
        private readonly AudioAnalysisService audioAnalysisService = new();
        private AudioAnalysisData? currentSpectrumAnalysis;
        private string? currentSpectrumAnalysisPath;
        private CancellationTokenSource? songAnalysisCancellation;
        private int playbackRequestId;
        private string? hoveredThemeColorKey;
        private bool hoveredThemeColorIsMiniPlayer;
        private bool themeColorHoverShowingContrast;
        private Color themeColorHoverFlashColor;
        private bool themeColorHoverPreviewInSettingsWindow;
        private VolumeSettings savedSettings = new();
        private ThemeSettingsWindow? themeSettingsWindow;
        private AudioSpectrumSettingsWindow? audioSpectrumSettingsWindow;
        private RootFolderSettingsWindow? rootFolderSettingsWindow;
        private static readonly string VolumeSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Audiocal",
            "settings.json");
        private readonly DispatcherTimer youtubeSearchDebounceTimer;
        private CancellationTokenSource? youtubeSearchCancellation;
        private string? pendingYouTubeSearchQuery;
        private readonly MediaPlayer previewPlayer = new();
        private YouTubeSearchResult? activePreviewResult;
        private YouTubeSearchResult? retainedPreviewResult;
        private string? activePreviewDirectory;
        private Process? previewDownloadProcess;
        private CancellationTokenSource? previewPreparationCancellation;
        private Task? previewPreparationTask;
        private bool isPreparingPreview;
        private bool isAddingYouTubeResult;
        private bool mainPlaybackWasPlayingBeforePreview;
        private TimeSpan mainPlaybackPositionBeforePreview;
        private bool previewPausedMainPlayback;
        private bool isClosingAfterPreviewCleanup;
        private TaskCompletionSource<string?>? libraryNameDialogCompletion;
        private Func<string, string?>? libraryNameDialogValidator;

        private string musicRootFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        


        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            themeColorHoverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            themeColorHoverTimer.Tick += ThemeColorHoverTimer_Tick;
            SourceInitialized += MainWindow_SourceInitialized;
            LocationChanged += MainWindow_LocationChanged;

            youtubeSearchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            youtubeSearchDebounceTimer.Tick += YouTubeSearchDebounceTimer_Tick;

            savedSettings = LoadSavedSettings();
            musicRootFolder = string.IsNullOrWhiteSpace(savedSettings.RootFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
                : savedSettings.RootFolder;
            savedSettings.RootFolder = musicRootFolder;
            savedSettings.AudioSpectrum ??= new AudioSpectrumOptions();
            NormalizeThemeColors();
            ApplyThemeColors();
            double initialVolume = savedSettings.Volume;
            VolumeSlider.Value = initialVolume;
            VolumeText.Text = $"{Math.Round(initialVolume)}%";
            player.Volume = initialVolume / 100.0;
            previousVolume = initialVolume > 0 ? player.Volume : 1.0;
            MuteButton.Content = initialVolume > 0 ? "🔊" : "🔇";
            VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            volumeSettingsSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            volumeSettingsSaveTimer.Tick += VolumeSettingsSaveTimer_Tick;
            previewPlayer.MediaEnded += PreviewPlayer_MediaEnded;

            musicLibrary = new MusicLibrary(musicRootFolder);
            CreateLibraryMenu();
            SongList.ItemsSource = songs;

            if (musicLibrary.LibraryFolders.Count > 0)
            {
                string? savedLibraryFolder = musicLibrary.LibraryFolders.FirstOrDefault(
                    folder => PathsEqual(folder, savedSettings.LibraryFolder));
                string initialLibraryFolder = savedLibraryFolder ?? musicLibrary.LibraryFolders[0];
                ChangeMusicFolder(
                    initialLibraryFolder,
                    restoreLastSong: savedLibraryFolder != null);
                SaveVolumeSettings();
            }
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += Timer_Tick;
            player.MediaEnded += Player_MediaEnded;

            downloadStatusTimer = new DispatcherTimer();
            downloadStatusTimer.Interval = TimeSpan.FromSeconds(5);
            downloadStatusTimer.Tick += DownloadStatusTimer_Tick;
            audioSpectrumTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            audioSpectrumTimer.Tick += AudioSpectrumTimer_Tick;
            spectrumSettingsSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            spectrumSettingsSaveTimer.Tick += (_, _) =>
            {
                spectrumSettingsSaveTimer.Stop();
                SaveVolumeSettings();
            };
            savedSettings.AudioSpectrum.PropertyChanged += (_, _) =>
            {
                UpdateAudioSpectrumRowHeight();
                spectrumSettingsSaveTimer.Stop();
                spectrumSettingsSaveTimer.Start();
            };
        }
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(musicRootFolder))
            {
                RootFolderMenuItem_Click(this, new RoutedEventArgs());
            }
        }
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            nint windowHandle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            int result = DwmSetWindowAttribute(
                windowHandle,
                20,
                ref enabled,
                (uint)Marshal.SizeOf<int>());
            if (result != 0)
            {
                result = DwmSetWindowAttribute(
                    windowHandle,
                    19,
                    ref enabled,
                    (uint)Marshal.SizeOf<int>());
            }

            if (result != 0)
            {
                Debug.WriteLine($"Could not enable the dark title bar (HRESULT 0x{result:X8}).");
                return;
            }

            uint captionColor = 0x0020231B;
            int captionResult = DwmSetWindowAttribute(
                windowHandle,
                35,
                ref captionColor,
                (uint)Marshal.SizeOf<uint>());
            if (captionResult != 0)
            {
                Debug.WriteLine($"Could not set the title bar color (HRESULT 0x{captionResult:X8}).");
            }

            uint textColor = 0x00F3F5F1;
            int textResult = DwmSetWindowAttribute(
                windowHandle,
                36,
                ref textColor,
                (uint)Marshal.SizeOf<uint>());
            if (textResult != 0)
            {
                Debug.WriteLine($"Could not set the title bar text color (HRESULT 0x{textResult:X8}).");
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            nint hwnd,
            int attribute,
            ref int value,
            uint valueSize);

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int DwmSetWindowAttribute(
            nint hwnd,
            int attribute,
            ref uint value,
            uint valueSize);

        private void MainWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (!YouTubeSearchPopup.IsOpen)
            {
                return;
            }

            double horizontalOffset = YouTubeSearchPopup.HorizontalOffset;
            YouTubeSearchPopup.HorizontalOffset = horizontalOffset + 1;
            YouTubeSearchPopup.HorizontalOffset = horizontalOffset;
        }
        private void MiniPlayerToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isMiniPlayerEnabled)
            {
                savedWindowState = WindowState;
                Rect restoreBounds = RestoreBounds;
                savedWindowWidth = savedWindowState == WindowState.Maximized ? restoreBounds.Width : Width;
                savedWindowHeight = savedWindowState == WindowState.Maximized ? restoreBounds.Height : Height;
                savedWindowLeft = savedWindowState == WindowState.Maximized ? restoreBounds.Left : Left;
                savedWindowTop = savedWindowState == WindowState.Maximized ? restoreBounds.Top : Top;
                savedWindowMinWidth = MinWidth;
                savedWindowMinHeight = MinHeight;
                savedSizeToContent = SizeToContent;
                savedResizeMode = ResizeMode;
                savedWindowStyle = WindowStyle;
                savedWindowBackground = Background;

                YouTubeSearchPopup.IsOpen = false;
                youtubeSearchDebounceTimer.Stop();
                youtubeSearchCancellation?.Cancel();

                WindowState = WindowState.Normal;
                MainLayoutGrid.Margin = new Thickness(0);
                MainLayoutGrid.RowDefinitions[0].Height = new GridLength(0);
                MainLayoutGrid.RowDefinitions[1].Height = new GridLength(0);
                TopBarBorder.Visibility = Visibility.Collapsed;
                LibraryBorder.Visibility = Visibility.Collapsed;
                PlayerBorder.Margin = new Thickness(0);
                PlayerBorder.Padding = new Thickness(14, 8, 14, 8);
                MinWidth = 0;
                MinHeight = 0;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                WindowStyle = WindowStyle.None;
                Background = (Brush)FindResource("PanelBrush");
                SetWindowCornerPreference(2);
                Width = 520;
                Left = savedWindowLeft;
                Top = savedWindowTop;

                MiniPlayerToggleButton.ToolTip = "Return to full player";
                isMiniPlayerEnabled = true;
                ApplyThemeColors();
                Background = (Brush)FindResource("PanelBrush");
                AudioSpectrumToggleButton.Visibility = Visibility.Visible;
                AudioSpectrumToggleButton.Opacity = isAudioSpectrumEnabled ? 1 : 0.55;
                NowPlayingLabel.Visibility = Visibility.Collapsed;
                MiniPlayerPinButton.Visibility = Visibility.Visible;
                NowPlayingPanel.Margin = new Thickness(42, 0, 0, 0);
                NowPlayingPanel.VerticalAlignment = VerticalAlignment.Center;
                NowPlayingText.FontSize = 16;
                RefreshNowPlayingText();
                return;
            }

            double miniPlayerLeft = Left;
            double miniPlayerTop = Top;
            savedWindowLeft = miniPlayerLeft;
            savedWindowTop = miniPlayerTop;

            SetAudioSpectrumEnabled(false);
            AudioSpectrumToggleButton.Visibility = Visibility.Collapsed;
            isMiniPlayerEnabled = false;
            ApplyThemeColors();
            MainLayoutGrid.Margin = new Thickness(24);
            MainLayoutGrid.RowDefinitions[0].Height = GridLength.Auto;
            MainLayoutGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            TopBarBorder.Visibility = Visibility.Visible;
            LibraryBorder.Visibility = Visibility.Visible;
            PlayerBorder.Margin = new Thickness(0, 12, 0, 0);
            PlayerBorder.Padding = new Thickness(22, 10, 22, 10);
            SizeToContent = savedSizeToContent;
            ResizeMode = savedResizeMode;
            WindowStyle = savedWindowStyle;
            if (savedWindowBackground != null)
            {
                Background = savedWindowBackground;
            }
            Background = (Brush)FindResource("WindowBackgroundBrush");
            SetWindowCornerPreference(0);
            MinWidth = savedWindowMinWidth;
            MinHeight = savedWindowMinHeight;
            Width = savedWindowWidth;
            Height = savedWindowHeight;
            Left = savedWindowLeft;
            Top = savedWindowTop;
            WindowState = savedWindowState;

            isMiniPlayerPinned = false;
            Topmost = false;
            MiniPlayerPinButton.Tag = false;
            MiniPlayerPinButton.Foreground = (Brush)FindResource("TextMutedBrush");
            MiniPlayerPinButton.ToolTip = "Keep miniplayer on top";
            MiniPlayerPinButton.Visibility = Visibility.Collapsed;
            NowPlayingLabel.Visibility = Visibility.Visible;
            NowPlayingPanel.Margin = new Thickness(0);
            NowPlayingPanel.VerticalAlignment = VerticalAlignment.Stretch;
            NowPlayingText.FontSize = 14;
            MiniPlayerToggleButton.ToolTip = "Switch to miniplayer";
            RefreshNowPlayingText();
        }
        private void MiniPlayerPinButton_Click(object sender, RoutedEventArgs e)
        {
            isMiniPlayerPinned = !isMiniPlayerPinned;
            Topmost = isMiniPlayerPinned;
            MiniPlayerPinButton.Tag = isMiniPlayerPinned;
            MiniPlayerPinButton.Foreground = isMiniPlayerPinned
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TextMutedBrush");
            MiniPlayerPinButton.ToolTip = isMiniPlayerPinned
                ? "Stop keeping miniplayer on top"
                : "Keep miniplayer on top";
        }
        private void SetNowPlayingText(string? title, string? artist = null)
        {
            nowPlayingTitle = title ?? "No song selected";
            nowPlayingArtist = title == null ? null : artist;
            RefreshNowPlayingText();
        }
        private void RefreshNowPlayingText()
        {
            NowPlayingText.Text = !isMiniPlayerEnabled && nowPlayingArtist != null
                ? $"{nowPlayingTitle} — {nowPlayingArtist}"
                : nowPlayingTitle;
        }
        private void SetWindowCornerPreference(uint preference)
        {
            nint windowHandle = new WindowInteropHelper(this).Handle;
            int result = DwmSetWindowAttribute(
                windowHandle,
                33,
                ref preference,
                (uint)Marshal.SizeOf<uint>());
            if (result != 0)
            {
                Debug.WriteLine($"Could not set the window corner preference (HRESULT 0x{result:X8}).");
            }
        }
        private void PlayerBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isMiniPlayerEnabled)
            {
                return;
            }

            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current != null)
            {
                if (current is ButtonBase or Slider or TextBox or ScrollBar)
                {
                    return;
                }

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            DragMove();
            e.Handled = true;
        }
        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinElement(e.OriginalSource as DependencyObject, YouTubeSearchPopupContent))
            {
                return;
            }

            Point position = e.GetPosition(YouTubeUrlTextBox);
            bool clickedSearchArea =
                position.X >= 0 &&
                position.Y >= 0 &&
                position.X <= YouTubeUrlTextBox.ActualWidth &&
                position.Y <= YouTubeUrlTextBox.ActualHeight;
            if (clickedSearchArea)
            {
                ShowSearchResultsForCurrentQuery();
                return;
            }

            if (YouTubeSearchPopup.IsOpen)
            {
                YouTubeSearchPopup.IsOpen = false;
                youtubeSearchDebounceTimer.Stop();
                youtubeSearchCancellation?.Cancel();
                YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
                if (DownloadStatusText.Text == "Searching YouTube...")
                {
                    DownloadStatusText.Visibility = Visibility.Collapsed;
                    downloadStatusTimer.Stop();
                }
            }
        }
        private static bool IsWithinElement(DependencyObject? source, DependencyObject target)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, target))
                {
                    return true;
                }

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return false;
        }
        private MediaPlayer CreateMediaPlayer()
        {
            MediaPlayer newPlayer = new MediaPlayer();

            newPlayer.Volume = player.Volume;
            newPlayer.MediaEnded += Player_MediaEnded;

            return newPlayer;
        }
        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (activePreviewResult != null || retainedPreviewResult != null || isPreparingPreview)
            {
                await StopPreviewAsync(deleteTemporaryFile: true);
            }

            if (currentPlayingSong is not Song currentSong)
                return;

            TimeSpan timeSinceStart =
                DateTime.Now - currentSongStartedAt;

            // More than 3 seconds into the song:
            // restart the current song.
            if (timeSinceStart.TotalSeconds > 3)
            {
                player.Position = TimeSpan.Zero;

                songHasEnded = false;

                isPlaying = true;
                PlayPauseButton.Content = "⏸";

                timer.Start();

                currentSongStartedAt = DateTime.Now;

                return;
            }

            // We are within the first 3 seconds.
            // Go back in history.
            if (playbackHistory.Count >= 2)
            {
                // Remove the song currently playing.
                playbackHistory.RemoveAt(
                    playbackHistory.Count - 1);

                // Find the previous song.
                Song previousSong =
                    playbackHistory[playbackHistory.Count - 1];

                PlaySong(previousSong, addToHistory: false);

                return;
            }

            // No previous song:
            // restart the current one.
            player.Position = TimeSpan.Zero;

            songHasEnded = false;

            isPlaying = true;
            PlayPauseButton.Content = "⏸";

            timer.Start();

            currentSongStartedAt = DateTime.Now;
        }
        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (isPreparingSong)
            {
                return;
            }

            if (SongList.SelectedItem is Song selectedSong &&
                !ReferenceEquals(selectedSong, currentPlayingSong))
            {
                if (activePreviewResult != null || retainedPreviewResult != null || isPreparingPreview)
                {
                    await StopPreviewAsync(deleteTemporaryFile: true);
                }

                PlaySong(selectedSong);
            }
            else if (currentPlayingSong != null)
            {
                if (isPlaying)
                {
                    player.Pause();
                    isPlaying = false;
                    PlayPauseButton.Content = "▶";
                }
                else
                {
                    if (songHasEnded)
                    {
                        player.Position = TimeSpan.Zero;
                        songHasEnded = false;
                    }

                    player.Play();

                    isPlaying = true;
                    PlayPauseButton.Content = "⏸";

                    timer.Start();
                }
            }
        }
        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (activePreviewResult != null || retainedPreviewResult != null || isPreparingPreview)
            {
                await StopPreviewAsync(deleteTemporaryFile: true);
            }

            if (currentPlayingSong is not Song currentSong)
                return;

            // 🔂 Song Loop → 🔁 Library Loop
            if (loopMode == LoopMode.Song)
            {
                loopMode = LoopMode.Library;
                LoopButton.Content = "🔁";
            }

            // 🔀 Shuffle
            if (isShuffleEnabled)
            {
                playbackOrderIndex++;

                if (playbackOrderIndex < playbackOrder.Count)
                {
                    Song nextSong = playbackOrder[playbackOrderIndex];

                    PlaySong(nextSong);

                    return;
                }

                // Last song in the shuffle order
                if (loopMode == LoopMode.Library)
                {
                    CreateShuffleOrder();

                    playbackOrderIndex = 0;

                    PlaySong(playbackOrder[0]);

                    return;
                }

                // No loop → stop
                isPlaying = false;
                songHasEnded = true;

                PlayPauseButton.Content = "▶";

                player.Stop();

                ProgressSlider.Value = 0;
                CurrentTimeText.Text = "00:00";

                timer.Stop();

                return;
            }

            // Normal playback order
            int currentIndex = songs.IndexOf(currentSong);

            if (currentIndex < 0)
                return;

            if (currentIndex < songs.Count - 1)
            {
                Song nextSong = songs[currentIndex + 1];

                PlaySong(nextSong);

                return;
            }

            // 🔁 Loop Library
            if (loopMode == LoopMode.Library &&
                songs.Count > 0)
            {
                PlaySong(songs[0]);

                return;
            }

            // No loop → stop
            isPlaying = false;
            songHasEnded = true;

            PlayPauseButton.Content = "▶";

            player.Stop();

            ProgressSlider.Value = 0;
            CurrentTimeText.Text = "00:00";

            timer.Stop();
        }
        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            isShuffleEnabled = !isShuffleEnabled;

            if (isShuffleEnabled)
            {
                CreateShuffleOrder(currentPlayingSong);

                ShuffleButton.Content = "🔀";
            }
            else
            {
                playbackOrder.Clear();
                playbackOrderIndex = -1;

                ShuffleButton.Content = "↕";
            }
        }
        private void LoopButton_Click(object sender, RoutedEventArgs e)
        {
            switch (loopMode)
            {
                case LoopMode.None:
                    loopMode = LoopMode.Library;
                    LoopButton.Content = "🔁";
                    break;

                case LoopMode.Library:
                    loopMode = LoopMode.Song;
                    LoopButton.Content = "🔂";
                    break;

                case LoopMode.Song:
                    loopMode = LoopMode.None;
                    LoopButton.Content = "↻";
                    break;
            }
        }
        private void LoadMusicFolder(string folderPath)
        {
            string[] mp3Files = Directory.GetFiles(folderPath, "*.mp3");

            foreach (string file in mp3Files)
            {
                Song song = metadataService.GetSongMetadata(file);

                songs.Add(song);
            }
        }
        private void RefreshCurrentLibrary()
        {
            if (currentLibraryFolder == null)
                return;

            string? selectedFilePath = currentPlayingSong?.FilePath;
            string? selectedRowFilePath = (SongList.SelectedItem as Song)?.FilePath;

            bool wasPlaying = isPlaying;
            TimeSpan currentPosition = player.Position;

            songs.Clear();

            LoadMusicFolder(currentLibraryFolder);

            SongList.Items.Refresh();

            if (selectedRowFilePath != null)
            {
                Song? newSelectedSong = songs.FirstOrDefault(
                    song => song.FilePath == selectedRowFilePath);

                if (newSelectedSong != null)
                {
                    SongList.SelectedItem = newSelectedSong;
                }
            }

            if (selectedFilePath != null)
            {
                currentPlayingSong = songs.FirstOrDefault(
                    song => song.FilePath == selectedFilePath);
            }

            // Restore playback after refresh
            if (selectedFilePath != null &&
                currentPlayingSong != null)
            {
                player.Position = currentPosition;

                isPlaying = wasPlaying;

                if (wasPlaying)
                {
                    player.Play();
                    PlayPauseButton.Content = "⏸";
                    timer.Start();
                }
                else
                {
                    PlayPauseButton.Content = "▶";
                }

                ProgressSlider.Value = currentPosition.TotalSeconds;
            }
        }
        private void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCurrentLibrary();
        }
        private async void SongListItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: Song song })
            {
                if (activePreviewResult != null || retainedPreviewResult != null || isPreparingPreview)
                {
                    await StopPreviewAsync(deleteTemporaryFile: true);
                }

                PlaySong(song);
                e.Handled = true;
            }
        }
        private void SongListItem_PreviewMouseRightButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem item ||
                item.DataContext is not Song)
            {
                return;
            }

            ContextMenu contextMenu = new ContextMenu
            {
                Style = (Style)FindResource(typeof(ContextMenu)),
                PlacementTarget = item
            };
            MenuItem renameItem = new MenuItem { Header = "Rename title" };
            renameItem.Click += RenameSongTitleMenuItem_Click;
            MenuItem renameArtistItem = new MenuItem { Header = "Rename artist" };
            renameArtistItem.Click += RenameSongArtistMenuItem_Click;
            MenuItem deleteItem = new MenuItem { Header = "Delete song" };
            deleteItem.Click += DeleteSongMenuItem_Click;
            contextMenu.Items.Add(renameItem);
            contextMenu.Items.Add(renameArtistItem);
            contextMenu.Items.Add(deleteItem);
            item.ContextMenu = contextMenu;
        }
        private async void PlaySong(Song song, bool addToHistory = true)
        {
            songAnalysisCancellation?.Cancel();
            var analysisCancellation = new CancellationTokenSource();
            songAnalysisCancellation = analysisCancellation;
            int requestId = ++playbackRequestId;

            SongList.SelectedItem = song;
            SetNowPlayingText(song.Title, song.Artist);
            currentPlayingSong = song;
            savedSettings.LastSongPath = song.FilePath;
            if (currentLibraryFolder != null)
            {
                savedSettings.LibraryFolder = currentLibraryFolder;
            }
            SaveVolumeSettings();

            player.Pause();
            timer.Stop();
            isPlaying = false;
            isPreparingSong = savedSettings.PreAnalyzeAudioOnPlay;
            PlayPauseButton.Content = "▶";
            currentSpectrumAnalysis = null;
            currentSpectrumAnalysisPath = null;
            CloseSpectrumAudioReader();

            try
            {
                if (savedSettings.PreAnalyzeAudioOnPlay)
                {
                    ShowDownloadProgress($"Analyzing {song.Title} before playback...");
                    currentSpectrumAnalysis = await audioAnalysisService.GetOrCreateAsync(
                        song.FilePath,
                        analysisCancellation.Token);
                    currentSpectrumAnalysisPath = song.FilePath;
                }

                if (analysisCancellation.IsCancellationRequested || requestId != playbackRequestId)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (analysisCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException or
                NotSupportedException)
            {
                if (requestId != playbackRequestId)
                {
                    return;
                }

                currentSpectrumAnalysis = null;
                currentSpectrumAnalysisPath = null;
                MessageBox.Show(
                    $"Could not pre-analyze the song. Playback will continue without cached spectrum data.\n\n{exception.Message}",
                    "Audiocal audio analysis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (ReferenceEquals(songAnalysisCancellation, analysisCancellation))
                {
                    songAnalysisCancellation = null;
                }
                analysisCancellation.Dispose();
            }

            if (requestId != playbackRequestId)
            {
                return;
            }

            if (DownloadStatusText.Text.StartsWith("Analyzing ", StringComparison.Ordinal))
            {
                DownloadStatusText.Visibility = Visibility.Collapsed;
            }
            isPreparingSong = false;
            player.Open(new Uri(song.FilePath));
            songHasEnded = false;
            player.Play();
            isPlaying = true;
            PlayPauseButton.Content = "⏸";
            timer.Start();
            currentSongStartedAt = DateTime.Now;

            if (addToHistory &&
                (playbackHistory.Count == 0 ||
                 !ReferenceEquals(playbackHistory[^1], song)))
            {
                playbackHistory.Add(song);
            }
        }
        private void Timer_Tick(object? sender, EventArgs e)
        {
            MediaPlayer progressPlayer = activePreviewResult?.IsPreviewing == true
                ? previewPlayer
                : player;
            if (progressPlayer.NaturalDuration.HasTimeSpan)
            {
                TimeSpan duration = progressPlayer.NaturalDuration.TimeSpan;
                TimeSpan position = progressPlayer.Position;

                ProgressSlider.Maximum = duration.TotalSeconds;

                if (!isDraggingSlider)
                {
                    ProgressSlider.Value = position.TotalSeconds;
                }
                CurrentTimeText.Text = position.ToString(@"mm\:ss");
                DurationText.Text = duration.ToString(@"mm\:ss");
            }
        }
        private void AudioSpectrumToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isMiniPlayerEnabled)
            {
                return;
            }

            SetAudioSpectrumEnabled(!isAudioSpectrumEnabled);
        }
        private void SetAudioSpectrumEnabled(bool enabled)
        {
            if (enabled == isAudioSpectrumEnabled)
            {
                return;
            }

            isAudioSpectrumEnabled = enabled && isMiniPlayerEnabled;
            AudioSpectrumCanvas.Visibility = isAudioSpectrumEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            AudioSpectrumToggleButton.Opacity = isAudioSpectrumEnabled ? 1 : 0.55;
            AudioSpectrumToggleButton.ToolTip = isAudioSpectrumEnabled
                ? "Hide audio spectrum"
                : "Show audio spectrum";
            if (!isAudioSpectrumEnabled)
            {
                audioSpectrumTimer.Stop();
                CloseSpectrumAudioReader();
                smoothedSpectrumBass = 0;
                UpdateAudioSpectrumRowHeight();
                return;
            }

            UpdateAudioSpectrumRowHeight();
            spectrumLastFrameTime = DateTime.UtcNow;
            audioSpectrumTimer.Start();
        }
        private void UpdateAudioSpectrumRowHeight()
        {
            if (!isAudioSpectrumEnabled || !isMiniPlayerEnabled)
            {
                PlayerContentGrid.RowDefinitions[2].Height = new GridLength(0);
                return;
            }

            AudioSpectrumOptions options = savedSettings.AudioSpectrum;
            double maximumScale = 1 + options.BassExpansion * 0.18;
            double largestCircle = Math.Max(options.CircleSize, options.UnderlayCircleSize);
            double rowHeight = Math.Ceiling(largestCircle * maximumScale + 16);
            PlayerContentGrid.RowDefinitions[2].Height = new GridLength(rowHeight);
        }
        private void CloseSpectrumAudioReader()
        {
            spectrumAudioReader?.Dispose();
            spectrumAudioReader = null;
            spectrumAudioPath = null;
            spectrumBassFilterState = Array.Empty<float>();
        }
        private void UpdateSpectrumAudioLevels(double deltaSeconds)
        {
            bool previewPlaying = activePreviewResult?.IsPreviewing == true;
            if (!previewPlaying &&
                isPlaying &&
                savedSettings.PreAnalyzeAudioOnPlay &&
                currentSpectrumAnalysis != null &&
                string.Equals(
                    currentSpectrumAnalysisPath,
                    currentPlayingSong?.FilePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                spectrumBassLevel = currentSpectrumAnalysis.GetBassLevel(player.Position);
                return;
            }

            string? sourcePath = previewPlaying
                ? activePreviewResult?.TemporaryAudioPath
                : currentPlayingSong?.FilePath;
            bool sourcePlaying = previewPlaying || isPlaying;
            if (!sourcePlaying || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                spectrumBassLevel = 0;
                return;
            }

            if (spectrumAudioReader == null ||
                !string.Equals(spectrumAudioPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                CloseSpectrumAudioReader();
                spectrumAudioReader = new AudioFileReader(sourcePath);
                spectrumAudioPath = sourcePath;
                spectrumBassFilterState = new float[Math.Max(1, spectrumAudioReader.WaveFormat.Channels)];
            }

            AudioFileReader reader = spectrumAudioReader ??
                throw new InvalidOperationException("The audio spectrum reader was not initialized.");
            TimeSpan playbackPosition = previewPlaying
                ? previewPlayer.Position
                : player.Position;
            if (Math.Abs((reader.CurrentTime - playbackPosition).TotalMilliseconds) > 15)
            {
                reader.CurrentTime = playbackPosition;
            }

            int channelCount = Math.Max(1, reader.WaveFormat.Channels);
            int frameCount = Math.Max(
                1,
                (int)Math.Round(reader.WaveFormat.SampleRate * Math.Clamp(deltaSeconds, 0.005, 0.05)));
            int sampleCount = frameCount * channelCount;
            if (spectrumSampleBuffer.Length < sampleCount)
            {
                spectrumSampleBuffer = new float[sampleCount];
            }

            sampleCount = reader.Read(spectrumSampleBuffer, 0, sampleCount);
            if (sampleCount == 0)
            {
                spectrumBassLevel = 0;
                return;
            }

            double sampleRate = reader.WaveFormat.SampleRate;
            double lowPassAlpha = 1 - Math.Exp(-2 * Math.PI * 150 / sampleRate);
            double bassSquare = 0;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                float sample = spectrumSampleBuffer[sampleIndex];
                if (!float.IsFinite(sample))
                {
                    continue;
                }

                sample = Math.Clamp(sample, -1, 1);
                int channel = sampleIndex % channelCount;
                float bassSample = spectrumBassFilterState[channel] +
                                   (float)lowPassAlpha * (sample - spectrumBassFilterState[channel]);
                spectrumBassFilterState[channel] = bassSample;
                bassSquare += bassSample * bassSample;
            }

            spectrumBassLevel = (float)Math.Sqrt(bassSquare / sampleCount);
        }
        private void AudioSpectrumTimer_Tick(object? sender, EventArgs e)
        {
            if (!isMiniPlayerEnabled || !isAudioSpectrumEnabled)
            {
                return;
            }

            double deltaSeconds = Math.Clamp(
                (DateTime.UtcNow - spectrumLastFrameTime).TotalSeconds,
                0.005,
                0.05);
            spectrumLastFrameTime = DateTime.UtcNow;

            try
            {
                UpdateSpectrumAudioLevels(deltaSeconds);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException or
                NotSupportedException)
            {
                SetAudioSpectrumEnabled(false);
                MessageBox.Show(
                    $"Could not analyze the current audio for the spectrum.\n\n{exception.Message}",
                    "Audiocal audio spectrum",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AudioSpectrumOptions options = savedSettings.AudioSpectrum;
            double sensitivity = options.Sensitivity;
            double bass = Math.Clamp(spectrumBassLevel * sensitivity * 2.5, 0, 1);
            double bassTimeConstant = bass > smoothedSpectrumBass ? 0.004 : 0.13;
            double bassSmoothing = 1 - Math.Exp(
                -deltaSeconds * options.MotionSpeed / bassTimeConstant);
            smoothedSpectrumBass += (bass - smoothedSpectrumBass) *
                                    bassSmoothing;

            double size = options.CircleSize;
            double underlaySize = options.UnderlayCircleSize;
            AudioSpectrumCircle.Width = size;
            AudioSpectrumCircle.Height = size;
            AudioSpectrumRedCopy.Width = underlaySize;
            AudioSpectrumRedCopy.Height = underlaySize;
            AudioSpectrumBlueCopy.Width = underlaySize;
            AudioSpectrumBlueCopy.Height = underlaySize;
            double canvasWidth = AudioSpectrumCanvas.ActualWidth;
            double canvasHeight = AudioSpectrumCanvas.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0)
            {
                return;
            }

            double left = (canvasWidth - size) / 2;
            double top = (canvasHeight - size) / 2;
            Canvas.SetLeft(AudioSpectrumCircle, left);
            Canvas.SetTop(AudioSpectrumCircle, top);
            double underlayLeft = (canvasWidth - underlaySize) / 2;
            double underlayTop = (canvasHeight - underlaySize) / 2;
            Canvas.SetLeft(AudioSpectrumRedCopy, underlayLeft);
            Canvas.SetTop(AudioSpectrumRedCopy, underlayTop);
            Canvas.SetLeft(AudioSpectrumBlueCopy, underlayLeft);
            Canvas.SetTop(AudioSpectrumBlueCopy, underlayTop);

            double bassScale = smoothedSpectrumBass * options.BassExpansion * 0.18;
            double scaleValue = 1 + bassScale;
            double availableTravel = Math.Max(0, (canvasWidth - underlaySize * scaleValue) / 2);
            double horizontalTravel = Math.Min(size * smoothedSpectrumBass * 0.2, availableTravel);
            SetSpectrumCircleTransform(AudioSpectrumCircle, scaleValue, 0);
            SetSpectrumCircleTransform(AudioSpectrumRedCopy, scaleValue, -horizontalTravel);
            SetSpectrumCircleTransform(AudioSpectrumBlueCopy, scaleValue, horizontalTravel);
        }
        private static void SetSpectrumCircleTransform(
            System.Windows.Shapes.Ellipse circle,
            double scaleValue,
            double horizontalOffset)
        {
            if (circle.RenderTransform is not TransformGroup transforms ||
                transforms.Children.Count < 2 ||
                transforms.Children[0] is not ScaleTransform scale ||
                transforms.Children[1] is not TranslateTransform translate)
            {
                throw new InvalidOperationException("The audio spectrum circle transform is invalid.");
            }

            scale.ScaleX = scaleValue;
            scale.ScaleY = scaleValue;
            translate.X = horizontalOffset;
        }
        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            timer.Stop();
            currentSongStartedAt = DateTime.Now;

            // 🔂 Loop the current song
            if (loopMode == LoopMode.Song)
            {
                player.Position = TimeSpan.Zero;
                player.Play();

                isPlaying = true;
                songHasEnded = false;

                PlayPauseButton.Content = "⏸";

                timer.Start();

                return;
            }

            // 🔀 Shuffle
            if (isShuffleEnabled)
            {
                playbackOrderIndex++;

                // There are still songs left in the current shuffle order
                if (playbackOrderIndex < playbackOrder.Count)
                {
                    Song nextSong = playbackOrder[playbackOrderIndex];

                    PlaySong(nextSong);

                    return;
                }

                
                // The entire shuffle order has been completed
                if (loopMode == LoopMode.Library)
                {
                    Song? lastSong = SongList.SelectedItem as Song;

                    // Create a new random shuffle order
                    CreateShuffleOrder();

                    // Make sure the new order does not start
                    // with the song that just finished
                    if (lastSong != null &&
                        playbackOrder.Count > 1 &&
                        playbackOrder[0] == lastSong)
                    {
                        Song temp = playbackOrder[0];
                        playbackOrder[0] = playbackOrder[1];
                        playbackOrder[1] = temp;
                    }

                    playbackOrderIndex = 0;

                    Song nextSong = playbackOrder[playbackOrderIndex];

                    PlaySong(nextSong);

                    return;
                }

                // No loop → stop
                isPlaying = false;
                songHasEnded = true;

                PlayPauseButton.Content = "▶";

                ProgressSlider.Value = 0;
                CurrentTimeText.Text = "00:00";

                return;
            }
            else
            {
                // Normal playback order
                if (currentPlayingSong is Song currentSong)
                {
                    int currentIndex = songs.IndexOf(currentSong);

                    if (currentIndex >= 0 &&
                        currentIndex < songs.Count - 1)
                    {
                        Song nextSong = songs[currentIndex + 1];

                        PlaySong(nextSong);

                        return;
                    }

                    // 🔁 Loop the library
                    if (loopMode == LoopMode.Library &&
                        songs.Count > 0)
                    {
                        PlaySong(songs[0]);

                        return;
                    }
                }
            }

            // No next song and no loop
            isPlaying = false;
            songHasEnded = true;

            PlayPauseButton.Content = "▶";

            ProgressSlider.Value = 0;
            CurrentTimeText.Text = "00:00";
        }
        private void ProgressSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = true;
        }
        private void ProgressSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            UpdateSliderHoverPreview(ProgressSlider, e, forwardOnly: true);
        }
        private void ProgressSlider_MouseLeave(object sender, MouseEventArgs e)
        {
            HideSliderHoverPreview(ProgressSlider);
        }
        private void ProgressSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            MediaPlayer progressPlayer = activePreviewResult?.IsPreviewing == true
                ? previewPlayer
                : player;
            if (progressPlayer.NaturalDuration.HasTimeSpan)
            {
                double newValue = GetSliderValueFromMousePosition(ProgressSlider, e);

                ProgressSlider.Value = newValue;

                progressPlayer.Position = TimeSpan.FromSeconds(newValue);
            }

            isDraggingSlider = false;

        }
        private void VolumeSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingVolumeSlider = true;

            SetVolumeFromMousePosition(e);
        }
        private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (isDraggingVolumeSlider)
            {
                SetVolumeFromMousePosition(e);
            }

            UpdateSliderHoverPreview(VolumeSlider, e, forwardOnly: true);
        }
        private void VolumeSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingVolumeSlider = false;
        }
        private void VolumeSlider_MouseLeave(object sender, MouseEventArgs e)
        {
            HideSliderHoverPreview(VolumeSlider);
        }
        private void SetVolumeFromMousePosition(MouseEventArgs e)
        {
            VolumeSlider.Value = GetSliderValueFromMousePosition(VolumeSlider, e);
        }
        private static double GetSliderValueFromMousePosition(Slider slider, MouseEventArgs e)
        {
            const double thumbWidth = 13;
            double trackWidth = slider.ActualWidth - thumbWidth;
            double range = slider.Maximum - slider.Minimum;
            if (trackWidth <= 0 || range <= 0)
            {
                return slider.Value;
            }

            double x = Math.Clamp(
                e.GetPosition(slider).X - thumbWidth / 2,
                0,
                trackWidth);
            double progress = x / trackWidth;
            if (slider.IsDirectionReversed)
            {
                progress = 1 - progress;
            }

            return slider.Minimum + progress * range;
        }
        private static void UpdateSliderHoverPreview(
            Slider slider,
            MouseEventArgs e,
            bool forwardOnly)
        {
            const double thumbWidth = 13;
            double trackWidth = slider.ActualWidth - thumbWidth;
            double range = slider.Maximum - slider.Minimum;
            if (trackWidth <= 0 || range <= 0 ||
                slider.Template.FindName("SliderPreviewSegment", slider) is not Border previewSegment)
            {
                return;
            }

            double pointerX = Math.Clamp(
                e.GetPosition(slider).X,
                thumbWidth / 2,
                slider.ActualWidth - thumbWidth / 2);
            double pointerProgress = (pointerX - thumbWidth / 2) / trackWidth;
            double targetProgress = slider.IsDirectionReversed
                ? 1 - pointerProgress
                : pointerProgress;
            double targetValue = slider.Minimum + targetProgress * range;
            if (forwardOnly && targetValue <= slider.Value)
            {
                previewSegment.Visibility = Visibility.Collapsed;
                return;
            }

            double currentProgress = (slider.Value - slider.Minimum) / range;
            double currentX = slider.IsDirectionReversed
                ? slider.ActualWidth - thumbWidth / 2 - currentProgress * trackWidth
                : thumbWidth / 2 + currentProgress * trackWidth;
            double segmentStart = pointerX >= currentX
                ? currentX + thumbWidth / 2
                : pointerX;
            double segmentEnd = pointerX >= currentX
                ? pointerX
                : currentX - thumbWidth / 2;
            double segmentWidth = segmentEnd - segmentStart;
            if (segmentWidth <= 0)
            {
                previewSegment.Visibility = Visibility.Collapsed;
                return;
            }

            previewSegment.Margin = new Thickness(segmentStart, 0, 0, 0);
            previewSegment.Width = segmentWidth;
            previewSegment.Visibility = Visibility.Visible;
        }
        private static void HideSliderHoverPreview(Slider slider)
        {
            if (slider.Template.FindName("SliderPreviewSegment", slider) is Border previewSegment)
            {
                previewSegment.Visibility = Visibility.Collapsed;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double volume = e.NewValue / 100.0;
            player.Volume = volume;
            previewPlayer.Volume = volume;

            VolumeText.Text = $"{Math.Round(e.NewValue)}%";
            volumeSettingsSaveTimer.Stop();
            volumeSettingsSaveTimer.Start();

            if (player.Volume > 0)
            {
                previousVolume = player.Volume;
                MuteButton.Content = "🔊";
            }
            else
            {
                MuteButton.Content = "🔇";
            }
        }
        private void VolumeSettingsSaveTimer_Tick(object? sender, EventArgs e)
        {
            volumeSettingsSaveTimer.Stop();
            SaveVolumeSettings();
        }
        private static VolumeSettings LoadSavedSettings()
        {
            if (!File.Exists(VolumeSettingsPath))
            {
                return new VolumeSettings();
            }

            try
            {
                string json = File.ReadAllText(VolumeSettingsPath);
                VolumeSettings? settings = JsonSerializer.Deserialize<VolumeSettings>(json);
                if (settings == null)
                {
                    return new VolumeSettings();
                }

                if (!double.IsFinite(settings.Volume) ||
                    settings.Volume is < 0 or > 100)
                {
                    settings.Volume = 100;
                }

                return settings;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException)
            {
                MessageBox.Show(
                    $"Could not load saved app settings. The volume will start at 100%, and the first library will be selected.\n\n{exception.Message}",
                    "Audiocal settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return new VolumeSettings();
            }
        }
        private void SaveVolumeSettings()
        {
            try
            {
                string? settingsDirectory = Path.GetDirectoryName(VolumeSettingsPath);
                if (settingsDirectory == null)
                {
                    throw new IOException("The settings directory could not be determined.");
                }

                Directory.CreateDirectory(settingsDirectory);
                savedSettings.Volume = VolumeSlider.Value;
                NormalizeThemeColors();
                string json = JsonSerializer.Serialize(savedSettings);
                File.WriteAllText(VolumeSettingsPath, json);
                volumeSettingsSaveErrorShown = false;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (!volumeSettingsSaveErrorShown)
                {
                    volumeSettingsSaveErrorShown = true;
                    MessageBox.Show(
                        $"Could not save app settings.\n\n{exception.Message}",
                        "Audiocal settings",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        private void NormalizeThemeColors()
        {
            savedSettings.ThemeColors ??= new Dictionary<string, string>();
            savedSettings.MiniPlayerThemeColors ??= new Dictionary<string, string>();
            savedSettings.AudioSpectrum ??= new AudioSpectrumOptions();
            savedSettings.ThemeColors.Remove("InputBackgroundBrush");
            foreach ((string key, string defaultColor) in ThemeSettingsWindow.DefaultColors)
            {
                if (!savedSettings.ThemeColors.TryGetValue(key, out string? color) ||
                    !ThemeSettingsWindow.TryParseColor(color, out _))
                {
                    savedSettings.ThemeColors[key] = defaultColor;
                }

                if (!savedSettings.MiniPlayerThemeColors.TryGetValue(key, out string? miniPlayerColor) ||
                    !ThemeSettingsWindow.TryParseColor(miniPlayerColor, out _))
                {
                    savedSettings.MiniPlayerThemeColors[key] = savedSettings.ThemeColors[key];
                }
            }
        }
        private void ApplyThemeColors()
        {
            StopThemeColorHoverPreview();
            Dictionary<string, string> palette = isMiniPlayerEnabled
                ? savedSettings.MiniPlayerThemeColors
                : savedSettings.ThemeColors;
            foreach (string key in ThemeSettingsWindow.DefaultColors.Keys)
            {
                ApplyThemeColor(key, palette[key]);
            }
            if (isMiniPlayerEnabled)
            {
                Background = (Brush)FindResource("PanelBrush");
            }
        }
        private void ThemeColorName_HoverChanged(bool isMiniPlayerPalette, string? key)
        {
            StopThemeColorHoverPreview();
            if (key == null)
            {
                return;
            }

            if (key == "PanelRaisedBrush" && !isMiniPlayerPalette && themeSettingsWindow != null)
            {
                string panelColor = savedSettings.ThemeColors[key];
                hoveredThemeColorKey = key;
                hoveredThemeColorIsMiniPlayer = false;
                themeColorHoverPreviewInSettingsWindow = true;
                themeColorHoverFlashColor = GetThemeColorHoverFlashColor(panelColor);
                themeColorHoverShowingContrast = true;
                UpdateThemeColorHoverBrush(key, themeColorHoverFlashColor);
                themeColorHoverTimer.Start();
                return;
            }

            if (isMiniPlayerPalette != isMiniPlayerEnabled)
            {
                return;
            }

            if (key == "HoverBrush")
            {
                hoveredThemeColorKey = key;
                hoveredThemeColorIsMiniPlayer = isMiniPlayerPalette;
                themeColorHoverShowingContrast = true;
                Tag = true;
                themeColorHoverTimer.Start();
                return;
            }

            Dictionary<string, string> palette = isMiniPlayerPalette
                ? savedSettings.MiniPlayerThemeColors
                : savedSettings.ThemeColors;
            if (!palette.TryGetValue(key, out string? color) ||
                !ThemeSettingsWindow.TryParseColor(color, out _))
            {
                return;
            }

            hoveredThemeColorKey = key;
            hoveredThemeColorIsMiniPlayer = isMiniPlayerPalette;
            themeColorHoverPreviewInSettingsWindow = false;
            themeColorHoverFlashColor = GetThemeColorHoverFlashColor(color);
            themeColorHoverShowingContrast = true;
            UpdateThemeBrush(Resources, key, themeColorHoverFlashColor);
            themeColorHoverTimer.Start();
        }
        private void ThemeColorHoverTimer_Tick(object? sender, EventArgs e)
        {
            if (hoveredThemeColorKey == null)
            {
                StopThemeColorHoverPreview();
                return;
            }

            themeColorHoverShowingContrast = !themeColorHoverShowingContrast;
            if (hoveredThemeColorKey == "HoverBrush")
            {
                Tag = themeColorHoverShowingContrast;
                return;
            }

            if (themeColorHoverShowingContrast)
            {
                UpdateThemeColorHoverBrush(hoveredThemeColorKey, themeColorHoverFlashColor);
                return;
            }

            Dictionary<string, string> palette = hoveredThemeColorIsMiniPlayer
                ? savedSettings.MiniPlayerThemeColors
                : savedSettings.ThemeColors;
            if (palette.TryGetValue(hoveredThemeColorKey, out string? color) &&
                ThemeSettingsWindow.TryParseColor(color, out Color parsedColor))
            {
                UpdateThemeColorHoverBrush(hoveredThemeColorKey, parsedColor);
            }
        }
        private void UpdateThemeColorHoverBrush(string key, Color color)
        {
            if (themeColorHoverPreviewInSettingsWindow)
            {
                if (themeSettingsWindow != null)
                {
                    UpdateThemeBrush(themeSettingsWindow.Resources, key, color);
                }

                if (key == "PanelRaisedBrush")
                {
                    UpdateThemeBrush(Resources, key, color);
                }

                return;
            }

            UpdateThemeBrush(Resources, key, color);
            if (!isMiniPlayerEnabled && key == "WindowBackgroundBrush")
            {
                Background = (Brush)FindResource(key);
            }
        }
        private static Color GetThemeColorHoverFlashColor(string color)
        {
            return ThemeSettingsWindow.TryParseColor(color, out Color parsedColor) &&
                   parsedColor.R > 200 &&
                   parsedColor.G > 200 &&
                   parsedColor.B > 200
                ? Colors.Black
                : Colors.White;
        }
        private void StopThemeColorHoverPreview()
        {
            themeColorHoverTimer.Stop();
            if (hoveredThemeColorKey is not string key)
            {
                return;
            }

            if (key == "HoverBrush")
            {
                Tag = null;
                hoveredThemeColorKey = null;
                hoveredThemeColorIsMiniPlayer = false;
                themeColorHoverShowingContrast = false;
                themeColorHoverPreviewInSettingsWindow = false;
                return;
            }

            Dictionary<string, string> palette = hoveredThemeColorIsMiniPlayer
                ? savedSettings.MiniPlayerThemeColors
                : savedSettings.ThemeColors;
            if (palette.TryGetValue(key, out string? color) &&
                ThemeSettingsWindow.TryParseColor(color, out Color parsedColor))
            {
                if (themeColorHoverPreviewInSettingsWindow)
                {
                    if (themeSettingsWindow != null)
                    {
                        UpdateThemeBrush(themeSettingsWindow.Resources, key, parsedColor);
                    }

                    if (hoveredThemeColorIsMiniPlayer == isMiniPlayerEnabled)
                    {
                        UpdateThemeBrush(Resources, key, parsedColor);
                    }
                }
                else if (hoveredThemeColorIsMiniPlayer == isMiniPlayerEnabled)
                {
                    UpdateThemeBrush(Resources, key, parsedColor);
                    if (!isMiniPlayerEnabled && key == "WindowBackgroundBrush")
                    {
                        Background = (Brush)FindResource(key);
                    }
                }
            }

            hoveredThemeColorKey = null;
            hoveredThemeColorIsMiniPlayer = false;
            themeColorHoverShowingContrast = false;
            themeColorHoverFlashColor = Colors.Transparent;
            themeColorHoverPreviewInSettingsWindow = false;
        }
        private void ApplyThemeColor(string key, string value)
        {
            if (!ThemeSettingsWindow.TryParseColor(value, out Color color))
            {
                return;
            }

            ApplyThemeColor(key, color, isMiniPlayerEnabled);
        }
        private void ApplyThemeColor(string key, Color color, bool isMiniPlayerPalette)
        {
            if (isMiniPlayerPalette == isMiniPlayerEnabled)
            {
                if (hoveredThemeColorKey != key || !themeColorHoverShowingContrast)
                {
                    UpdateThemeBrush(Resources, key, color);
                    if (!isMiniPlayerEnabled && key == "WindowBackgroundBrush")
                    {
                        Background = (Brush)FindResource(key);
                    }
                }
            }
            if (!isMiniPlayerPalette && themeSettingsWindow != null)
            {
                UpdateThemeBrush(themeSettingsWindow.Resources, key, color);
            }
            if (!isMiniPlayerPalette && audioSpectrumSettingsWindow != null)
            {
                UpdateThemeBrush(audioSpectrumSettingsWindow.Resources, key, color);
            }
            if (!isMiniPlayerPalette && rootFolderSettingsWindow != null)
            {
                UpdateThemeBrush(rootFolderSettingsWindow.Resources, key, color);
            }
        }
        private static void UpdateThemeBrush(ResourceDictionary resources, string key, Color color)
        {
            resources[key] = new SolidColorBrush(color);
        }
        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (themeSettingsWindow != null)
            {
                themeSettingsWindow.Activate();
                return;
            }

            var settingsWindow = new ThemeSettingsWindow(
                savedSettings.ThemeColors,
                savedSettings.MiniPlayerThemeColors,
                isMiniPlayerEnabled)
            {
                Owner = this
            };
            themeSettingsWindow = settingsWindow;
            settingsWindow.PaletteSelectionChanged += isMiniPlayerPalette =>
            {
                if (isMiniPlayerPalette != isMiniPlayerEnabled)
                {
                    MiniPlayerToggleButton_Click(this, new RoutedEventArgs());
                }
            };
            settingsWindow.ColorNameHoverChanged += ThemeColorName_HoverChanged;
            settingsWindow.ThemeColorChanged += (isMiniPlayerPalette, key, color) =>
            {
                string hex = color.A < 255
                    ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.R:X2}{color.G:X2}{color.B:X2}";
                Dictionary<string, string> palette = isMiniPlayerPalette
                    ? savedSettings.MiniPlayerThemeColors
                    : savedSettings.ThemeColors;
                palette[key] = hex;
                ApplyThemeColor(key, color, isMiniPlayerPalette);
                SaveVolumeSettings();
            };
            settingsWindow.Closed += (_, _) =>
            {
                StopThemeColorHoverPreview();
                SaveVolumeSettings();
                themeSettingsWindow = null;
            };

            Rect workArea = SystemParameters.WorkArea;
            double settingsLeft = Left + ActualWidth;
            if (settingsLeft + settingsWindow.Width > workArea.Right)
            {
                settingsLeft = Left - settingsWindow.Width;
            }

            settingsWindow.Left = Math.Clamp(
                settingsLeft,
                workArea.Left,
                Math.Max(workArea.Left, workArea.Right - settingsWindow.Width));
            settingsWindow.Top = Math.Clamp(
                Top + 40,
                workArea.Top,
                Math.Max(workArea.Top, workArea.Bottom - settingsWindow.Height));
            settingsWindow.Show();
        }
        private void AudioSpectrumSettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (audioSpectrumSettingsWindow != null)
            {
                audioSpectrumSettingsWindow.Activate();
                return;
            }

            var settingsWindow = new AudioSpectrumSettingsWindow(
                savedSettings.AudioSpectrum,
                savedSettings.PreAnalyzeAudioOnPlay)
            {
                Owner = this
            };
            foreach (string key in ThemeSettingsWindow.DefaultColors.Keys)
            {
                if (savedSettings.ThemeColors.TryGetValue(key, out string? color) &&
                    ThemeSettingsWindow.TryParseColor(color, out Color parsedColor))
                {
                    UpdateThemeBrush(settingsWindow.Resources, key, parsedColor);
                }
            }

            audioSpectrumSettingsWindow = settingsWindow;
            settingsWindow.PreAnalyzeBeforePlayChanged += enabled =>
            {
                savedSettings.PreAnalyzeAudioOnPlay = enabled;
                SaveVolumeSettings();
            };
            settingsWindow.Closed += (_, _) =>
            {
                SaveVolumeSettings();
                audioSpectrumSettingsWindow = null;
            };
            PositionSettingsWindow(settingsWindow);
            settingsWindow.Show();
        }
        private void RootFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (rootFolderSettingsWindow != null)
            {
                rootFolderSettingsWindow.Activate();
                return;
            }

            var settingsWindow = new RootFolderSettingsWindow(
                musicRootFolder,
                savedSettings.YtDlpPath)
            {
                Owner = this
            };
            rootFolderSettingsWindow = settingsWindow;
            foreach (string key in ThemeSettingsWindow.DefaultColors.Keys)
            {
                ApplyThemeColor(key, savedSettings.ThemeColors[key]);
            }
            settingsWindow.PathsApplyRequested += ChangeStoragePaths;
            settingsWindow.Closed += (_, _) => rootFolderSettingsWindow = null;
            PositionSettingsWindow(settingsWindow);
            settingsWindow.Show();
        }
        private bool ChangeStoragePaths(string rootFolder, string ytDlpPath)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(rootFolder);
                string? normalizedYtDlpPath = string.IsNullOrWhiteSpace(ytDlpPath)
                    ? null
                    : Path.GetFullPath(ytDlpPath);
                var newMusicLibrary = new MusicLibrary(normalizedRoot);
                string? savedLibraryFolder = newMusicLibrary.LibraryFolders.FirstOrDefault(
                    folder => PathsEqual(folder, savedSettings.LibraryFolder));

                musicRootFolder = normalizedRoot;
                musicLibrary = newMusicLibrary;
                savedSettings.RootFolder = normalizedRoot;
                savedSettings.YtDlpPath = normalizedYtDlpPath;
                CreateLibraryMenu();

                if (musicLibrary.LibraryFolders.Count > 0)
                {
                    string selectedFolder = savedLibraryFolder ?? musicLibrary.LibraryFolders[0];
                    ChangeMusicFolder(
                        selectedFolder,
                        restoreLastSong: savedLibraryFolder != null);
                }
                else
                {
                    currentLibraryFolder = null;
                    songs.Clear();
                    SongList.Items.Refresh();
                    CurrentLibraryNameText.Text = "No libraries found";
                    SongList.SelectedItem = null;
                    savedSettings.LibraryFolder = null;
                }

                SaveVolumeSettings();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException)
            {
                MessageBox.Show(
                    $"Could not use the selected storage paths.\n\n{exception.Message}",
                    "Audiocal storage paths",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }
        private string? ResolveYtDlpPath()
        {
            if (!string.IsNullOrWhiteSpace(savedSettings.YtDlpPath) &&
                File.Exists(savedSettings.YtDlpPath))
            {
                return Path.GetFullPath(savedSettings.YtDlpPath);
            }

            string? savedDirectory = Path.GetDirectoryName(savedSettings.YtDlpPath ?? "");
            var fileDialog = new OpenFileDialog
            {
                Title = "Locate yt-dlp.exe",
                Filter = "yt-dlp executable (yt-dlp.exe)|yt-dlp.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                FileName = "yt-dlp.exe"
            };
            if (!string.IsNullOrWhiteSpace(savedDirectory) && Directory.Exists(savedDirectory))
            {
                fileDialog.InitialDirectory = savedDirectory;
            }

            if (fileDialog.ShowDialog(this) != true)
            {
                ShowDownloadStatus("Choose yt-dlp.exe to use YouTube features");
                return null;
            }

            savedSettings.YtDlpPath = Path.GetFullPath(fileDialog.FileName);
            SaveVolumeSettings();
            return savedSettings.YtDlpPath;
        }
        private void PositionSettingsWindow(Window settingsWindow)
        {
            Rect workArea = SystemParameters.WorkArea;
            double settingsLeft = Left + ActualWidth;
            if (settingsLeft + settingsWindow.Width > workArea.Right)
            {
                settingsLeft = Left - settingsWindow.Width;
            }

            settingsWindow.Left = Math.Clamp(
                settingsLeft,
                workArea.Left,
                Math.Max(workArea.Left, workArea.Right - settingsWindow.Width));
            settingsWindow.Top = Math.Clamp(
                Top + 40,
                workArea.Top,
                Math.Max(workArea.Top, workArea.Bottom - settingsWindow.Height));
        }
        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (player.Volume > 0)
            {
                previousVolume = player.Volume;

                player.Volume = 0;
                previewPlayer.Volume = 0;

                VolumeSlider.Value = 0;
                MuteButton.Content = "🔇";
            }
            else
            {
                VolumeSlider.Value = previousVolume * 100;
            }
        }
        private void CreateLibraryMenu()
        {
            LibraryMenuItem.Items.Clear();

            foreach (string folder in musicLibrary.LibraryFolders)
            {
                MenuItem menuItem = new MenuItem();

                menuItem.Header = Path.GetFileName(folder);
                menuItem.Tag = folder;

                menuItem.Click += LibraryMenuItem_Click;

                ContextMenu contextMenu = new ContextMenu
                {
                    Style = (Style)FindResource(typeof(ContextMenu))
                };

                MenuItem renameItem = new MenuItem
                {
                    Header = "Rename"
                };

                renameItem.Click += RenameLibraryItem_Click;

                contextMenu.Items.Add(renameItem);

                MenuItem deleteItem = new MenuItem
                {
                    Header = "Delete"
                };
                deleteItem.Click += DeleteLibraryItem_Click;
                contextMenu.Items.Add(deleteItem);

                menuItem.ContextMenu = contextMenu;

                LibraryMenuItem.Items.Add(menuItem);
            }

            MenuItem createLibraryItem = new MenuItem
            {
                Header = "+ Create new library"
            };

            createLibraryItem.Click += CreateLibraryItem_Click;



            LibraryMenuItem.Items.Add(createLibraryItem);
        }
        private void LibraryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is string folderPath)
            {
                ChangeMusicFolder(folderPath);
            }
        }
        private void ChangeMusicFolder(string folderPath, bool restoreLastSong = false)
        {
            currentLibraryFolder = folderPath;
            CurrentLibraryNameText.Text = Path.GetFileName(folderPath);

            songs.Clear();

            LoadMusicFolder(folderPath);

            SongList.Items.Refresh();
            Song? savedSong = restoreLastSong
                ? songs.FirstOrDefault(song => PathsEqual(song.FilePath, savedSettings.LastSongPath))
                : null;
            SongList.SelectedItem = savedSong;
            Song? displayedSong = currentPlayingSong ?? savedSong;
            SetNowPlayingText(displayedSong?.Title, displayedSong?.Artist);
            savedSettings.LibraryFolder = folderPath;
            SaveVolumeSettings();
        }
        private async void DeleteLibraryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem ||
                menuItem.Parent is not ContextMenu contextMenu ||
                contextMenu.PlacementTarget is not MenuItem libraryMenuItem ||
                libraryMenuItem.Tag is not string folderPath)
            {
                return;
            }

            string folderName = Path.GetFileName(folderPath);
            MessageBoxResult confirmation = MessageBox.Show(
                $"Are you sure you want to delete {folderName}?\n\n" +
                "All songs in this folder will be permanently deleted.",
                "Delete library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            bool isCurrentLibrary = currentLibraryFolder == folderPath;
            Song? playingSong = currentPlayingSong;
            bool isPlayingSongInFolder = playingSong != null &&
                                         string.Equals(
                                             Path.GetDirectoryName(playingSong.FilePath),
                                             folderPath,
                                             StringComparison.OrdinalIgnoreCase);
            TimeSpan playbackPosition = player.Position;
            bool wasPlaying = isPlaying;
            if (isPlayingSongInFolder)
            {
                ReplacePlayer();
            }

            try
            {
                await Task.Run(() => Directory.Delete(folderPath, recursive: true));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    $"Could not delete {folderName}.\n\n{exception.Message}",
                    "Delete library",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                if (isPlayingSongInFolder && playingSong != null)
                {
                    ReopenSongAfterFileOperation(playingSong, playbackPosition, wasPlaying);
                }
                return;
            }

            musicLibrary.RemoveLibrary(folderPath);
            if (PathsEqual(savedSettings.LibraryFolder, folderPath))
            {
                savedSettings.LibraryFolder = null;
            }
            if (IsPathInFolder(savedSettings.LastSongPath, folderPath))
            {
                savedSettings.LastSongPath = null;
            }
            playbackHistory.RemoveAll(historySong =>
                string.Equals(
                    Path.GetDirectoryName(historySong.FilePath),
                    folderPath,
                    StringComparison.OrdinalIgnoreCase));
            playbackOrder.RemoveAll(playlistSong =>
                string.Equals(
                    Path.GetDirectoryName(playlistSong.FilePath),
                    folderPath,
                    StringComparison.OrdinalIgnoreCase));
            if (playbackOrderIndex >= playbackOrder.Count)
            {
                playbackOrderIndex = playbackOrder.Count - 1;
            }
            if (isPlayingSongInFolder)
            {
                currentPlayingSong = null;
                isPlaying = false;
                SetNowPlayingText(null);
            }
            CreateLibraryMenu();
            if (isCurrentLibrary)
            {
                playbackOrderIndex = -1;
                currentLibraryFolder = null;
                CurrentLibraryNameText.Text = "";
                songs.Clear();
                SongList.SelectedItem = null;
                SongList.Items.Refresh();

                if (musicLibrary.LibraryFolders.Count > 0)
                {
                    ChangeMusicFolder(musicLibrary.LibraryFolders[0]);
                }
            }
            SaveVolumeSettings();
        }
        private async void CreateLibraryItem_Click(object sender, RoutedEventArgs e)
        {
            string? libraryName = await ShowLibraryNameDialogAsync(
                existingName: null,
                title: "Create library",
                instruction: "Folder name",
                validator: name => ValidateLibraryName(name, musicLibrary.RootFolder));
            if (libraryName == null)
            {
                return;
            }

            string? folderPath = musicLibrary.CreateLibrary(libraryName);
            if (folderPath == null)
            {
                MessageBox.Show("A library with this name already exists.");
                return;
            }

            CreateLibraryMenu();
            ChangeMusicFolder(folderPath);
        }
        private async void RenameLibraryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem renameItem &&
                renameItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is MenuItem libraryMenuItem &&
                libraryMenuItem.Tag is string oldFolderPath)
            {
                string oldName = Path.GetFileName(oldFolderPath);
                string? parentFolder = Path.GetDirectoryName(oldFolderPath);
                if (parentFolder == null)
                {
                    return;
                }

                string? newName = await ShowLibraryNameDialogAsync(
                    oldName,
                    title: "Rename library",
                    instruction: "New library name",
                    validator: name => ValidateLibraryName(name, parentFolder, oldFolderPath));
                if (newName == null)
                {
                    return;
                }
                if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string newFolderPath = Path.Combine(parentFolder, newName);

                // Save the current playback information
                Song? selectedSong = currentPlayingSong;

                bool wasPlaying = isPlaying;
                TimeSpan currentPosition = player.Position;

                bool isCurrentLibrary =
                    currentLibraryFolder == oldFolderPath;

                double currentVolume = player.Volume;

                // If renaming the active library,
                // release the MediaPlayer first.
                if (isCurrentLibrary)
                {
                    player.Stop();
                    timer.Stop();

                    player.MediaEnded -= Player_MediaEnded;
                    player.Close();

                    // Keep the old MediaPlayer in a local variable.
                    MediaPlayer oldPlayer = player;

                    // Create a new MediaPlayer
                    player = new MediaPlayer();

                    player.Volume = currentVolume;
                    player.MediaEnded += Player_MediaEnded;

                    isPlaying = false;

                    // Give WPF/Windows some time to release
                    // the old file reference.
                    await Task.Delay(300);
                }

                // Rename the Windows folder itself
                bool success =
                    await musicLibrary.RenameLibraryAsync(
                        oldFolderPath,
                        newName);

                if (!success)
                {
                    MessageBox.Show(
                        "Could not rename the library.\n\n" +
                        "Windows could not release the folder.");

                    if (isCurrentLibrary && selectedSong != null)
                    {
                        ReopenSongAfterFileOperation(selectedSong, currentPosition, wasPlaying);
                    }
                    return;
                }

                // Update the active folder
                if (isCurrentLibrary)
                {
                    currentLibraryFolder = newFolderPath;
                    CurrentLibraryNameText.Text = Path.GetFileName(newFolderPath);
                    savedSettings.LibraryFolder = newFolderPath;
                }
                if (IsPathInFolder(savedSettings.LastSongPath, oldFolderPath) &&
                    savedSettings.LastSongPath is string savedSongPath)
                {
                    savedSettings.LastSongPath = Path.Combine(
                        newFolderPath,
                        Path.GetFileName(savedSongPath));
                }
                SaveVolumeSettings();

                // Update the library menu
                CreateLibraryMenu();

                // Reopen the previous song
                if (isCurrentLibrary && selectedSong != null)
                {
                    string songFolder = Path.GetDirectoryName(selectedSong.FilePath) ?? "";
                    string newFilePath = string.Equals(
                        songFolder,
                        oldFolderPath,
                        StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(newFolderPath, Path.GetFileName(selectedSong.FilePath))
                        : selectedSong.FilePath;
                    selectedSong.FilePath = newFilePath;
                    currentPlayingSong = selectedSong;

                    pendingPlaybackPosition = currentPosition;
                    pendingWasPlaying = wasPlaying;

                    // The event handler must be attached BEFORE Open()
                    player.MediaOpened += RestorePlaybackAfterRename;

                    player.Open(new Uri(newFilePath));
                }
            }
        }
        private async void RenameSongTitleMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetSongFromContextMenu(sender) is not Song song)
            {
                return;
            }

            await RenameSongMetadataAsync(song, renameArtist: false);
        }

        private async void RenameSongArtistMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetSongFromContextMenu(sender) is not Song song)
            {
                return;
            }

            await RenameSongMetadataAsync(song, renameArtist: true);
        }

        private async Task RenameSongMetadataAsync(Song song, bool renameArtist)
        {
            string existingValue = renameArtist ? song.Artist : song.Title;
            string fieldName = renameArtist ? "artist" : "title";
            string? newValue = await ShowLibraryNameDialogAsync(
                existingValue,
                $"Rename {fieldName}",
                $"Song {fieldName}",
                validator: _ => null);
            if (string.IsNullOrWhiteSpace(newValue) ||
                string.Equals(newValue, existingValue, StringComparison.Ordinal))
            {
                return;
            }

            bool isPlayingSong = ReferenceEquals(currentPlayingSong, song);
            bool wasPlaying = isPlaying;
            TimeSpan position = player.Position;
            MediaPlayer? closedPlayer = null;
            if (isPlayingSong)
            {
                closedPlayer = ReplacePlayer();
                await Task.Delay(300);
            }

            try
            {
                await Task.Run(() =>
                {
                    if (renameArtist)
                    {
                        metadataService.UpdateSongArtist(song.FilePath, newValue);
                    }
                    else
                    {
                        metadataService.UpdateSongTitle(song.FilePath, newValue);
                    }
                });
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(
                    $"Could not rename the song {fieldName}.\n\n{exception.Message}",
                    $"Rename {fieldName}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                if (isPlayingSong)
                {
                    ReopenSongAfterFileOperation(song, position, wasPlaying);
                }
                return;
            }
            finally
            {
                GC.KeepAlive(closedPlayer);
            }

            if (renameArtist)
            {
                song.Artist = newValue;
            }
            else
            {
                song.Title = newValue;
            }

            SongList.Items.Refresh();
            if (isPlayingSong)
            {
                SetNowPlayingText(song.Title, song.Artist);
                ReopenSongAfterFileOperation(song, position, wasPlaying);
            }
        }
        private void DeleteSongMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetSongFromContextMenu(sender) is not Song song)
            {
                return;
            }

            bool isPlayingSong = ReferenceEquals(currentPlayingSong, song);
            TimeSpan position = player.Position;
            bool wasPlaying = isPlaying;
            if (isPlayingSong)
            {
                ReplacePlayer();
            }

            try
            {
                File.Delete(song.FilePath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    $"Could not delete the song.\n\n{exception.Message}",
                    "Delete song",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                if (isPlayingSong)
                {
                    ReopenSongAfterFileOperation(song, position, wasPlaying);
                }
                return;
            }

            songs.Remove(song);
            playbackHistory.RemoveAll(historySong => ReferenceEquals(historySong, song));
            playbackOrder.Remove(song);
            if (PathsEqual(savedSettings.LastSongPath, song.FilePath))
            {
                savedSettings.LastSongPath = null;
                SaveVolumeSettings();
            }
            if (playbackOrderIndex >= playbackOrder.Count)
            {
                playbackOrderIndex = playbackOrder.Count - 1;
            }
            if (isPlayingSong)
            {
                currentPlayingSong = null;
                isPlaying = false;
                SetNowPlayingText(null);
            }
            if (ReferenceEquals(SongList.SelectedItem, song))
            {
                SongList.SelectedItem = null;
            }

            SongList.Items.Refresh();
        }
        private static Song? GetSongFromContextMenu(object sender)
        {
            return sender is MenuItem { Parent: ContextMenu contextMenu } &&
                   contextMenu.PlacementTarget is ListBoxItem { DataContext: Song song }
                ? song
                : null;
        }
        private static string? ValidateLibraryName(
            string name,
            string parentFolder,
            string? existingFolderPath = null)
        {
            if (name is "." or ".." ||
                name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "Enter a valid folder name.";
            }

            string targetFolder = Path.Combine(parentFolder, name);
            if (Directory.Exists(targetFolder) &&
                !string.Equals(
                    Path.GetFullPath(targetFolder),
                    existingFolderPath == null ? null : Path.GetFullPath(existingFolderPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "A library with this name already exists.";
            }

            return null;
        }
        private static bool PathsEqual(string? firstPath, string? secondPath)
        {
            string? first = NormalizePath(firstPath);
            string? second = NormalizePath(secondPath);
            return first != null &&
                   second != null &&
                   string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsPathInFolder(string? filePath, string folderPath)
        {
            string? normalizedFilePath = NormalizePath(filePath);
            string? normalizedFolderPath = NormalizePath(folderPath);
            if (normalizedFilePath == null || normalizedFolderPath == null)
            {
                return false;
            }

            string folderPrefix = Path.TrimEndingDirectorySeparator(normalizedFolderPath) +
                                  Path.DirectorySeparatorChar;
            return normalizedFilePath.StartsWith(
                folderPrefix,
                StringComparison.OrdinalIgnoreCase);
        }
        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                return null;
            }
        }
        private MediaPlayer ReplacePlayer()
        {
            MediaPlayer oldPlayer = player;
            double volume = player.Volume;
            player.Stop();
            timer.Stop();
            player.MediaEnded -= Player_MediaEnded;
            player.Close();
            player = new MediaPlayer { Volume = volume };
            player.MediaEnded += Player_MediaEnded;
            isPlaying = false;
            PlayPauseButton.Content = "▶";
            return oldPlayer;
        }
        private void ReopenSongAfterFileOperation(Song song, TimeSpan position, bool wasPlaying)
        {
            pendingPlaybackPosition = position;
            pendingWasPlaying = wasPlaying;
            player.MediaOpened += RestorePlaybackAfterRename;
            player.Open(new Uri(song.FilePath));
        }
        private Task<string?> ShowLibraryNameDialogAsync(
            string? existingName,
            string title,
            string instruction,
            Func<string, string?> validator)
        {
            libraryNameDialogCompletion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            libraryNameDialogValidator = validator;

            LibraryDialogTitle.Text = title;
            LibraryDialogInstruction.Text = instruction;
            LibraryDialogActionButton.Content = title.StartsWith("Rename", StringComparison.Ordinal)
                ? "Rename"
                : "Create";
            LibraryNameTextBox.Text = existingName ?? "";
            LibraryDialogErrorText.Text = "";
            LibraryDialogErrorText.Visibility = Visibility.Collapsed;
            LibraryDialogOverlay.Visibility = Visibility.Visible;

            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() =>
                {
                    LibraryNameTextBox.Focus();
                    LibraryNameTextBox.SelectAll();
                }));

            return libraryNameDialogCompletion.Task;
        }
        private void LibraryDialogActionButton_Click(object sender, RoutedEventArgs e)
        {
            string name = LibraryNameTextBox.Text.Trim();
            string? error = string.IsNullOrWhiteSpace(name)
                ? "Please enter a name."
                : libraryNameDialogValidator?.Invoke(name);
            if (error != null)
            {
                LibraryDialogErrorText.Text = error;
                LibraryDialogErrorText.Visibility = Visibility.Visible;
                return;
            }

            CompleteLibraryNameDialog(name);
        }
        private void LibraryDialogCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CompleteLibraryNameDialog(null);
        }
        private void LibraryNameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                LibraryDialogActionButton_Click(sender, e);
            }
        }
        private void LibraryDialogOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CompleteLibraryNameDialog(null);
            }
        }
        private void CompleteLibraryNameDialog(string? name)
        {
            LibraryDialogOverlay.Visibility = Visibility.Collapsed;
            libraryNameDialogValidator = null;
            libraryNameDialogCompletion?.TrySetResult(name);
            libraryNameDialogCompletion = null;
        }
        private async Task DownloadWithYtDlp(string youtubeUrl, string destinationFolder)
        {
            string? ytDlpPath = ResolveYtDlpPath();
            if (ytDlpPath == null)
            {
                return;
            }

            bool isPlaylist = IsYouTubePlaylistUrl(youtubeUrl);
            if (isPlaylist)
            {
                PlaylistDownloadProgressBar.Value = 0;
                PlaylistDownloadProgressBar.Visibility = Visibility.Visible;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    WorkingDirectory = Path.GetDirectoryName(ytDlpPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("-P");
                startInfo.ArgumentList.Add(destinationFolder);
                startInfo.ArgumentList.Add("--embed-metadata");
                startInfo.ArgumentList.Add("--embed-thumbnail");
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("--audio-format");
                startInfo.ArgumentList.Add("mp3");
                if (isPlaylist)
                {
                    startInfo.ArgumentList.Add("--yes-playlist");
                    startInfo.ArgumentList.Add("--print");
                    startInfo.ArgumentList.Add("after_move:PLAYLIST_ITEM_DONE:%(playlist_count)s");
                }
                else
                {
                    startInfo.ArgumentList.Add("--no-playlist");
                }
                startInfo.ArgumentList.Add(youtubeUrl);

                using Process process = new Process { StartInfo = startInfo };
                process.Start();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                int completedSongs = 0;
                int? totalSongs = null;

                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    const string progressPrefix = "PLAYLIST_ITEM_DONE:";
                    if (isPlaylist && line.StartsWith(progressPrefix, StringComparison.Ordinal))
                    {
                        string countValue = line.Substring(progressPrefix.Length).Trim();
                        if (int.TryParse(countValue, out int parsedTotal) && parsedTotal > 0)
                        {
                            totalSongs = parsedTotal;
                            completedSongs++;
                            PlaylistDownloadProgressBar.Value = Math.Min(
                                100,
                                Math.Ceiling(completedSongs * 100d / totalSongs.Value));
                        }
                    }
                }

                await process.WaitForExitAsync();
                await errorTask;
                RefreshCurrentLibrary();

                if (process.ExitCode != 0)
                {
                    ShowDownloadStatus("Download finished with errors");
                }
                else if (isPlaylist)
                {
                    PlaylistDownloadProgressBar.Value = 100;
                    ShowDownloadStatus("Playlist downloaded");
                }
                else
                {
                    ShowDownloadStatus("Song downloaded");
                }
            }
            finally
            {
                if (isPlaylist)
                {
                    PlaylistDownloadProgressBar.Visibility = Visibility.Collapsed;
                }
            }
        }
        private void RestorePlaybackAfterRename(object? sender, EventArgs e)
        {
            player.MediaOpened -= RestorePlaybackAfterRename;

            player.Position = pendingPlaybackPosition;

            if (pendingWasPlaying)
            {
                player.Play();

                isPlaying = true;
                PlayPauseButton.Content = "⏸";

                timer.Start();
            }
            else
            {
                isPlaying = false;
                PlayPauseButton.Content = "▶";
                timer.Stop();
            }
        }
        private void CreateShuffleOrder(Song? currentSong = null)
        {
            playbackOrder = songs
                .OrderBy(song => Guid.NewGuid())
                .ToList();

            playbackOrderIndex = -1;

            // If shuffle starts while a song is already playing,
            // the current song should be first in the order.
            if (currentSong != null)
            {
                int currentIndex = playbackOrder.IndexOf(currentSong);

                if (currentIndex >= 0)
                {
                    playbackOrder.RemoveAt(currentIndex);
                }

                playbackOrder.Insert(0, currentSong);

                playbackOrderIndex = 0;
            }
        }
        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            string youtubeUrl = YouTubeUrlTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(youtubeUrl))
            {
                ShowDownloadStatus("Paste a YouTube URL");
                return;
            }

            bool isPlaylist = IsYouTubePlaylistUrl(youtubeUrl);
            if (isPlaylistModeEnabled && !isPlaylist)
            {
                ShowDownloadStatus("Enter a valid YouTube playlist URL");
                return;
            }
            if (currentLibraryFolder == null)
            {
                ShowDownloadStatus("Select a library first");
                return;
            }

            if (isPlaylistDownloadInProgress)
            {
                return;
            }

            if (isPlaylist)
            {
                ShowDownloadProgress("Downloading playlist...");
            }
            else
            {
                ShowDownloadProgress("Downloading song...");
            }

            isPlaylistDownloadInProgress = true;
            try
            {
                await DownloadWithYtDlp(
                    youtubeUrl,
                    currentLibraryFolder);

                YouTubeUrlTextBox.Clear();
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                PlaylistDownloadProgressBar.Visibility = Visibility.Collapsed;
                ShowDownloadStatus($"Download failed: {exception.Message}");
            }
            finally
            {
                isPlaylistDownloadInProgress = false;
            }
        }
        private async void YouTubeUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = YouTubeUrlTextBox.Text.Trim();

            youtubeSearchCancellation?.Cancel();
            youtubeSearchDebounceTimer.Stop();
            YouTubeSearchPopup.IsOpen = false;
            YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
            YouTubeSearchResults.ItemsSource = null;
            YouTubeSearchScrollViewer.ScrollToTop();
            if (!isAddingYouTubeResult)
            {
                await StopPreviewAsync(deleteTemporaryFile: true);
            }

            if (isPlaylistModeEnabled)
            {
                return;
            }

            if (query.Length < 2 || IsYouTubeUrl(query))
            {
                return;
            }

            if (!string.Equals(query, YouTubeUrlTextBox.Text.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            pendingYouTubeSearchQuery = query;
            youtubeSearchDebounceTimer.Start();
        }
        private void YouTubeUrlTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(ShowSearchResultsForCurrentQuery));
        }
        private void ShowSearchResultsForCurrentQuery()
        {
            string query = YouTubeUrlTextBox.Text.Trim();
            if (isPlaylistModeEnabled || query.Length < 2 || IsYouTubeUrl(query))
            {
                return;
            }

            if (YouTubeSearchResults.Items.Count > 0)
            {
                YouTubeSearchPopup.IsOpen = true;
                return;
            }

            pendingYouTubeSearchQuery = query;
            youtubeSearchDebounceTimer.Stop();
            youtubeSearchDebounceTimer.Start();
        }
        private async void YouTubeSearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            youtubeSearchDebounceTimer.Stop();

            if (pendingYouTubeSearchQuery is string query)
            {
                await SearchYouTubeAsync(query);
            }
        }
        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string query = YouTubeUrlTextBox.Text.Trim();

            if (isPlaylistModeEnabled)
            {
                if (!IsYouTubePlaylistUrl(query))
                {
                    ShowDownloadStatus("Enter a valid YouTube playlist URL");
                    return;
                }

                DownloadButton_Click(sender, e);
                return;
            }

            if (IsYouTubeUrl(query))
            {
                DownloadButton_Click(sender, e);
                return;
            }

            youtubeSearchDebounceTimer.Stop();
            await SearchYouTubeAsync(query);
        }
        private void YouTubeUrlTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            SearchButton_Click(sender, e);
        }
        private async void YouTubeSearchResult_PreviewDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button { Tag: YouTubeSearchResult result })
            {
                return;
            }

            e.Handled = true;
            if (ReferenceEquals(activePreviewResult, result) && result.IsPreviewing)
            {
                await StopPreviewAsync(deleteTemporaryFile: true);
                return;
            }

            await StopPreviewAsync(deleteTemporaryFile: true);
            previewPreparationTask = PrepareAndPlayPreviewAsync(result);
            await previewPreparationTask;
            previewPreparationTask = null;
        }
        private async void YouTubeSearchResult_AddClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: YouTubeSearchResult result })
            {
                youtubeSearchCancellation?.Cancel();
                youtubeSearchDebounceTimer.Stop();
                YouTubeSearchPopup.IsOpen = false;
                YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
                YouTubeSearchResults.ItemsSource = null;
                YouTubeSearchScrollViewer.ScrollToTop();

                isAddingYouTubeResult = true;
                YouTubeUrlTextBox.Clear();
                try
                {
                    await AddYouTubeResultAsync(result);
                }
                finally
                {
                    isAddingYouTubeResult = false;
                }
            }
        }
        private async Task SearchYouTubeAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ShowDownloadStatus("Enter a song title to search");
                return;
            }

            string? ytDlpPath = ResolveYtDlpPath();
            if (ytDlpPath == null)
            {
                return;
            }

            youtubeSearchCancellation?.Cancel();
            youtubeSearchCancellation?.Dispose();
            youtubeSearchCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = youtubeSearchCancellation.Token;

            ShowDownloadProgress("Searching YouTube...");
            YouTubeSearchLoadingPanel.Visibility = Visibility.Visible;
            YouTubeSearchPopup.IsOpen = true;

            try
            {
                var searchResult = await Task.Run(async () =>
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        WorkingDirectory = Path.GetDirectoryName(ytDlpPath)!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    startInfo.ArgumentList.Add("--dump-single-json");
                    startInfo.ArgumentList.Add("--flat-playlist");
                    startInfo.ArgumentList.Add("--skip-download");
                    startInfo.ArgumentList.Add("--no-warnings");
                    startInfo.ArgumentList.Add($"ytsearch4:{query}");

                    using var process = new Process { StartInfo = startInfo };
                    process.Start();

                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();

                    try
                    {
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }

                        await process.WaitForExitAsync().ConfigureAwait(false);
                        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
                        throw;
                    }

                    string output = await outputTask.ConfigureAwait(false);
                    string error = await errorTask.ConfigureAwait(false);

                    return (
                        ExitCode: process.ExitCode,
                        Error: error,
                        Results: process.ExitCode == 0
                            ? ParseYouTubeSearchResults(output)
                            : new List<YouTubeSearchResult>());
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested ||
                    !string.Equals(query, YouTubeUrlTextBox.Text.Trim(), StringComparison.Ordinal))
                {
                    return;
                }

                YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
                if (searchResult.ExitCode != 0)
                {
                    ShowDownloadStatus(
                        string.IsNullOrWhiteSpace(searchResult.Error)
                            ? "YouTube search failed"
                            : $"YouTube search failed: {searchResult.Error.Trim()}");
                    YouTubeSearchPopup.IsOpen = false;
                    return;
                }

                YouTubeSearchResults.ItemsSource = searchResult.Results;
                YouTubeSearchScrollViewer.ScrollToTop();
                YouTubeSearchPopup.IsOpen = searchResult.Results.Count > 0;
                if (searchResult.Results.Count > 0)
                {
                    _ = Dispatcher.BeginInvoke(
                        DispatcherPriority.Loaded,
                        new Action(YouTubeSearchScrollViewer.ScrollToTop));
                }

                if (searchResult.Results.Count == 0)
                {
                    ShowDownloadStatus("No search results found");
                }
                else
                {
                    DownloadStatusText.Visibility = Visibility.Collapsed;
                    downloadStatusTimer.Stop();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
                ShowDownloadStatus($"YouTube search failed: {exception.Message}");
                YouTubeSearchPopup.IsOpen = false;
            }
        }
        private static List<YouTubeSearchResult> ParseYouTubeSearchResults(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("entries", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Array)
            {
                return new List<YouTubeSearchResult>();
            }

            var results = new List<YouTubeSearchResult>();
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string? id = GetJsonString(entry, "id");
                string? title = GetJsonString(entry, "title");

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                string? thumbnail = GetJsonString(entry, "thumbnail");
                if (string.IsNullOrWhiteSpace(thumbnail) &&
                    entry.TryGetProperty("thumbnails", out JsonElement thumbnails) &&
                    thumbnails.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement thumbnailEntry in thumbnails.EnumerateArray())
                    {
                        thumbnail = GetJsonString(thumbnailEntry, "url") ?? thumbnail;
                    }
                }

                results.Add(new YouTubeSearchResult
                {
                    Title = title,
                    Channel = GetJsonString(entry, "channel") ??
                              GetJsonString(entry, "uploader") ??
                              "YouTube",
                    Url = $"https://www.youtube.com/watch?v={Uri.EscapeDataString(id)}",
                    ThumbnailUrl = thumbnail ??
                                   $"https://i.ytimg.com/vi/{Uri.EscapeDataString(id)}/hqdefault.jpg"
                });
            }

            return results.Take(4).ToList();
        }
        private static string? GetJsonString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        private static bool IsYouTubeUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                   IsYouTubeHost(uri.Host);
        }
        private static bool IsYouTubePlaylistUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !IsYouTubeHost(uri.Host))
            {
                return false;
            }

            foreach (string parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] keyValue = parameter.Split('=', 2);
                string key = Uri.UnescapeDataString(keyValue[0].Replace('+', ' '));
                if (key.Equals("list", StringComparison.OrdinalIgnoreCase) &&
                    keyValue.Length == 2 &&
                    !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(keyValue[1].Replace('+', ' '))))
                {
                    return true;
                }
            }

            return false;
        }
        private static bool IsYouTubeHost(string host)
        {
            return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
        }
        private async Task PrepareAndPlayPreviewAsync(YouTubeSearchResult result)
        {
            string? ytDlpPath = ResolveYtDlpPath();
            if (ytDlpPath == null)
            {
                return;
            }

            activePreviewResult = result;
            activePreviewDirectory = Path.Combine(
                Path.GetTempPath(),
                "AudiocalPreview",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(activePreviewDirectory);
            previewPreparationCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = previewPreparationCancellation.Token;
            isPreparingPreview = true;
            mainPlaybackWasPlayingBeforePreview = isPlaying;
            mainPlaybackPositionBeforePreview = player.Position;
            previewPausedMainPlayback = true;
            player.Pause();
            isPlaying = false;
            timer.Stop();
            PlayPauseButton.Content = "▶";
            ShowDownloadProgress($"Preparing preview of “{result.Title}”...");

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    WorkingDirectory = Path.GetDirectoryName(ytDlpPath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add("--no-playlist");
                startInfo.ArgumentList.Add("--no-warnings");
                startInfo.ArgumentList.Add("--embed-metadata");
                startInfo.ArgumentList.Add("--embed-thumbnail");
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add("--audio-format");
                startInfo.ArgumentList.Add("mp3");
                startInfo.ArgumentList.Add("-o");
                startInfo.ArgumentList.Add(Path.Combine(activePreviewDirectory, "%(id)s.%(ext)s"));
                startInfo.ArgumentList.Add(result.Url);

                using var process = new Process { StartInfo = startInfo };
                previewDownloadProcess = process;
                process.Start();

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);

                string error = await errorTask;
                await outputTask;
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error)
                            ? "Could not download audio for preview."
                            : error.Trim());
                }

                string? previewPath = Directory.GetFiles(activePreviewDirectory, "*.mp3")
                    .FirstOrDefault();
                if (previewPath == null)
                {
                    throw new FileNotFoundException("The preview file was not created.");
                }

                result.TemporaryAudioPath = previewPath;
                retainedPreviewResult = result;
                previewPlayer.Volume = player.Volume;
                previewPlayer.Open(new Uri(previewPath));
                previewPlayer.Play();
                result.IsPreviewing = true;
                timer.Start();
                SetNowPlayingText(result.Title, result.Channel);
                YouTubeSearchResults.Items.Refresh();
                ShowDownloadStatus($"Previewing: {result.Title}");
            }
            catch (OperationCanceledException)
            {
                await RestoreMainPlaybackAfterPreviewAsync();
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                await RestoreMainPlaybackAfterPreviewAsync();
                ShowDownloadStatus($"Preview failed: {exception.Message}");
                await DeletePreviewDirectoryAsync();
            }
            finally
            {
                isPreparingPreview = false;
                previewDownloadProcess = null;
                previewPreparationCancellation?.Dispose();
                previewPreparationCancellation = null;
            }
        }
        private async Task StopPreviewAsync(bool deleteTemporaryFile)
        {
            if (previewPreparationCancellation != null)
            {
                previewPreparationCancellation.Cancel();
                if (previewDownloadProcess is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                }

                if (previewPreparationTask != null)
                {
                    await previewPreparationTask;
                }
            }

            if (activePreviewResult != null)
            {
                activePreviewResult.IsPreviewing = false;
            }

            previewPlayer.Stop();
            previewPlayer.Close();
            await RestoreMainPlaybackAfterPreviewAsync();
            Song? displayedSong = currentPlayingSong ?? SongList.SelectedItem as Song;
            SetNowPlayingText(displayedSong?.Title, displayedSong?.Artist);
            YouTubeSearchResults.Items.Refresh();

            if (deleteTemporaryFile)
            {
                await DeletePreviewDirectoryAsync();
                YouTubeSearchResult? previewResultToClear = activePreviewResult ?? retainedPreviewResult;
                if (previewResultToClear != null)
                {
                    previewResultToClear.TemporaryAudioPath = null;
                }
                retainedPreviewResult = null;
            }
            else if (activePreviewResult?.TemporaryAudioPath != null)
            {
                retainedPreviewResult = activePreviewResult;
            }

            activePreviewResult = null;
            activePreviewDirectory = null;
        }
        private async void PreviewPlayer_MediaEnded(object? sender, EventArgs e)
        {
            await StopPreviewAsync(deleteTemporaryFile: false);
        }
        private Task RestoreMainPlaybackAfterPreviewAsync()
        {
            if (!previewPausedMainPlayback)
            {
                return Task.CompletedTask;
            }

            if (mainPlaybackWasPlayingBeforePreview)
            {
                player.Position = mainPlaybackPositionBeforePreview;
                player.Play();
                isPlaying = true;
                PlayPauseButton.Content = "⏸";
                timer.Start();
            }
            else
            {
                isPlaying = false;
                PlayPauseButton.Content = "▶";
            }

            mainPlaybackWasPlayingBeforePreview = false;
            previewPausedMainPlayback = false;
            return Task.CompletedTask;
        }
        private async Task DeletePreviewDirectoryAsync()
        {
            string? previewPath = (activePreviewResult ?? retainedPreviewResult)?.TemporaryAudioPath;
            string? directoryToDelete = activePreviewDirectory ??
                (previewPath == null ? null : Path.GetDirectoryName(previewPath));
            if (directoryToDelete != null && Directory.Exists(directoryToDelete))
            {
                try
                {
                    await Task.Run(() => Directory.Delete(directoryToDelete, recursive: true));
                }
                catch (IOException exception)
                {
                    ShowDownloadStatus($"Could not delete temporary audio file: {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    ShowDownloadStatus($"Could not delete temporary audio file: {exception.Message}");
                }
            }
        }
        private async Task AddYouTubeResultAsync(YouTubeSearchResult result)
        {
            if (currentLibraryFolder == null)
            {
                ShowDownloadStatus("Select a library first");
                return;
            }

            if (isPreparingPreview)
            {
                ShowDownloadStatus("Please wait while the preview is being prepared");
                return;
            }

            if (result.TemporaryAudioPath is string temporaryAudioPath &&
                File.Exists(temporaryAudioPath))
            {
                string? previewDirectory = activePreviewDirectory ??
                                           Path.GetDirectoryName(temporaryAudioPath);
                await StopPreviewAsync(deleteTemporaryFile: false);
                try
                {
                    string destination = GetAvailableDestinationPath(
                        currentLibraryFolder,
                        Path.GetFileName(temporaryAudioPath));
                    File.Move(temporaryAudioPath, destination);
                    result.TemporaryAudioPath = null;
                    if (ReferenceEquals(retainedPreviewResult, result))
                    {
                        retainedPreviewResult = null;
                    }
                    if (previewDirectory != null && Directory.Exists(previewDirectory))
                    {
                        await Task.Run(() => Directory.Delete(previewDirectory, recursive: true));
                    }
                    ShowDownloadStatus("Song added to library");
                    RefreshCurrentLibrary();
                }
                catch (IOException exception)
                {
                    ShowDownloadStatus($"Could not add the song: {exception.Message}");
                }
                catch (UnauthorizedAccessException exception)
                {
                    ShowDownloadStatus($"Could not add the song: {exception.Message}");
                }

                return;
            }

            if (activePreviewResult != null)
            {
                await StopPreviewAsync(deleteTemporaryFile: true);
            }

            ShowDownloadProgress("Downloading song...");
            try
            {
                await DownloadWithYtDlp(result.Url, currentLibraryFolder);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                ShowDownloadStatus($"Download failed: {exception.Message}");
            }
        }
        private static string GetAvailableDestinationPath(string folder, string fileName)
        {
            string destination = Path.Combine(folder, fileName);
            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;

            while (File.Exists(destination))
            {
                destination = Path.Combine(folder, $"{name} ({suffix++}){extension}");
            }

            return destination;
        }
        private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (isClosingAfterPreviewCleanup)
            {
                return;
            }

            e.Cancel = true;
            volumeSettingsSaveTimer.Stop();
            spectrumSettingsSaveTimer.Stop();
            StopThemeColorHoverPreview();
            audioSpectrumTimer.Stop();
            songAnalysisCancellation?.Cancel();
            CloseSpectrumAudioReader();
            SaveVolumeSettings();
            libraryNameDialogCompletion?.TrySetResult(null);
            youtubeSearchCancellation?.Cancel();
            youtubeSearchDebounceTimer.Stop();
            await StopPreviewAsync(deleteTemporaryFile: true);
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    YouTubeSearchPopup.IsOpen = false;
                    isClosingAfterPreviewCleanup = true;
                    Close();
                }));
        }
        private void DownloadStatusTimer_Tick(object? sender, EventArgs e)
        {
            DownloadStatusText.Visibility = Visibility.Collapsed;

            downloadStatusTimer.Stop();
        }
        private void ShowDownloadStatus(string message)
        {
            DownloadStatusText.Text = message;
            DownloadStatusText.Visibility = Visibility.Visible;

            downloadStatusTimer.Stop();
            downloadStatusTimer.Start();
        }
        private void ShowDownloadProgress(string message)
        {
            downloadStatusTimer.Stop();

            DownloadStatusText.Text = message;
            DownloadStatusText.Visibility = Visibility.Visible;
        }
        private void PlaylistModeButton_Click(object sender, RoutedEventArgs e)
        {
            isPlaylistModeEnabled = !isPlaylistModeEnabled;
            PlaylistModeButton.Background = isPlaylistModeEnabled
                ? new SolidColorBrush(Color.FromRgb(38, 58, 50))
                : Brushes.Transparent;
            PlaylistModeButton.ToolTip = isPlaylistModeEnabled
                ? "Playlist download mode: on"
                : "Playlist download mode: off";
            YouTubeUrlTextBox.ToolTip = isPlaylistModeEnabled
                ? "Paste a YouTube playlist URL and press Enter"
                : "Search for a song or paste a YouTube link";
            if (PlaylistModeButton.Content is System.Windows.Shapes.Path icon)
            {
                icon.Stroke = isPlaylistModeEnabled
                    ? new SolidColorBrush(Color.FromRgb(67, 217, 163))
                    : new SolidColorBrush(Color.FromRgb(145, 161, 154));
            }

            youtubeSearchCancellation?.Cancel();
            youtubeSearchDebounceTimer.Stop();
            YouTubeSearchPopup.IsOpen = false;
            YouTubeSearchResults.ItemsSource = null;
            YouTubeSearchLoadingPanel.Visibility = Visibility.Collapsed;
            DownloadStatusText.Visibility = Visibility.Collapsed;
            if (isPlaylistModeEnabled)
            {
                ShowDownloadStatus("Paste a YouTube playlist URL and press Enter");
            }
            else if (YouTubeUrlTextBox.Text.Trim().Length >= 2 &&
                     !IsYouTubeUrl(YouTubeUrlTextBox.Text.Trim()))
            {
                pendingYouTubeSearchQuery = YouTubeUrlTextBox.Text.Trim();
                youtubeSearchDebounceTimer.Start();
            }
        }

        private sealed class VolumeSettings
        {
            public double Volume { get; set; } = 100;
            public string? LibraryFolder { get; set; }
            public string? LastSongPath { get; set; }
            public string RootFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            public string? YtDlpPath { get; set; }
            public Dictionary<string, string> ThemeColors { get; set; } = new();
            public Dictionary<string, string> MiniPlayerThemeColors { get; set; } = new();
            public AudioSpectrumOptions AudioSpectrum { get; set; } = new();
            public bool PreAnalyzeAudioOnPlay { get; set; } = true;
        }

        private sealed class YouTubeSearchResult
        {
            public string Title { get; init; } = "";
            public string Channel { get; init; } = "";
            public string Url { get; init; } = "";
            public string ThumbnailUrl { get; init; } = "";
            public string? TemporaryAudioPath { get; set; }
            public bool IsPreviewing { get; set; }
        }
    }
}