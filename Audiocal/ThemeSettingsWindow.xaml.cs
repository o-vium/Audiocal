using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Data;
using System.Text.Json.Serialization;

namespace Audiocal
{
    public partial class ThemeSettingsWindow : Window
    {
        private const double ColorOptionRowHeight = 36;
        private const double NonListWindowHeight = 142;
        private const double ProfileListHeight = 300;
        private bool isProfileListVisible;
        private bool isUpdatingPalettes;

        public static IReadOnlyDictionary<string, string> DefaultColors { get; } =
            new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "#0D1110",
                ["PrimaryTextBrush"] = "#F1F5F3",
                ["PanelBrush"] = "#171E1B",
                ["PanelRaisedBrush"] = "#202925",
                ["TopBarBrush"] = "#1B2320",
                ["SongListBrush"] = "#141A18",
                ["AccentBrush"] = "#43D9A3",
                ["TextMutedBrush"] = "#91A19A",
                ["ControlBackgroundBrush"] = "#26312C",
                ["ControlBorderBrush"] = "#35423B",
                ["HoverBrush"] = "#304039",
                ["AudioSpectrumBrush"] = "#43D9A3",
                ["AudioSpectrumRedBrush"] = "#FF0000",
                ["AudioSpectrumBlueBrush"] = "#0000FF",
                ["AudioSpectrumIconBrush"] = "#43D9A3"
            };

        public ObservableCollection<ThemeColorOption> StandardColors { get; } = new();
        public ObservableCollection<ThemeColorOption> MiniPlayerColors { get; } = new();
        public ObservableCollection<ThemeColorProfile> Profiles { get; }

        public event Action<bool, string, Color>? ThemeColorChanged;
        public event Action<bool>? PaletteSelectionChanged;
        public event Action<bool, string?>? ColorNameHoverChanged;
        public event Action? ProfileAddRequested;
        public event Action<ThemeColorProfile>? ProfileSelected;
        public event Action<ThemeColorProfile>? ProfileNameChanged;
        public event Action<ThemeColorProfile>? ProfileDeleteRequested;
        public bool IsMiniPlayerPaletteSelected => PaletteTabControl.SelectedIndex == 1;

        public ThemeSettingsWindow(
            IReadOnlyDictionary<string, string> standardColors,
            IReadOnlyDictionary<string, string> miniPlayerColors,
            bool initiallySelectedMiniPlayerPalette,
            IEnumerable<ThemeColorProfile> profiles)
        {
            InitializeComponent();
            Profiles = new ObservableCollection<ThemeColorProfile>(profiles);
            DataContext = this;
            SetActiveProfile(Profiles.FirstOrDefault(profile => profile.IsActive));
            Resources["PanelBrush"] = CreateBrush(standardColors, "PanelBrush");
            Resources["PanelRaisedBrush"] = CreateBrush(standardColors, "PanelRaisedBrush");
            Resources["AccentBrush"] = CreateBrush(standardColors, "AccentBrush");
            Resources["PrimaryTextBrush"] = CreateBrush(standardColors, "PrimaryTextBrush");
            Resources["TextMutedBrush"] = CreateBrush(standardColors, "TextMutedBrush");
            Resources["ControlBorderBrush"] = CreateBrush(standardColors, "ControlBorderBrush");

            var names = new Dictionary<string, string>
            {
                ["WindowBackgroundBrush"] = "Window background",
                ["PrimaryTextBrush"] = "Main text",
                ["PanelBrush"] = "Panels",
                ["PanelRaisedBrush"] = "Raised panels",
                ["TopBarBrush"] = "Header",
                ["SongListBrush"] = "Song list",
                ["AccentBrush"] = "Accent",
                ["TextMutedBrush"] = "Muted text",
                ["ControlBackgroundBrush"] = "Buttons",
                ["ControlBorderBrush"] = "Borders",
                ["HoverBrush"] = "Button hover"
            };

            PopulateColors(StandardColors, standardColors, names, isMiniPlayer: false);
            var miniPlayerNames = new Dictionary<string, string>(names)
            {
                ["AudioSpectrumBrush"] = "Audio spectrum circle",
                ["AudioSpectrumRedBrush"] = "Audio spectrum red circle",
                ["AudioSpectrumBlueBrush"] = "Audio spectrum blue circle",
                ["AudioSpectrumIconBrush"] = "Spectrum toggle icon"
            };
            miniPlayerNames.Remove("PanelRaisedBrush");
            miniPlayerNames.Remove("TopBarBrush");
            miniPlayerNames.Remove("SongListBrush");
            miniPlayerNames.Remove("ControlBorderBrush");
            miniPlayerNames.Remove("ControlBackgroundBrush");
            PopulateColors(MiniPlayerColors, miniPlayerColors, miniPlayerNames, isMiniPlayer: true);
            PaletteTabControl.SelectedIndex = initiallySelectedMiniPlayerPalette ? 1 : 0;
            UpdateWindowHeight();
        }

        private void PaletteTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ReferenceEquals(sender, PaletteTabControl))
            {
                UpdateWindowHeight();
                PaletteSelectionChanged?.Invoke(IsMiniPlayerPaletteSelected);
            }
        }

        private void UpdateWindowHeight()
        {
            if (isProfileListVisible)
            {
                Height = NonListWindowHeight + ProfileListHeight;
                MinHeight = Height;
                MaxHeight = Height;
                return;
            }

            int colorCount = IsMiniPlayerPaletteSelected
                ? MiniPlayerColors.Count
                : StandardColors.Count;
            Height = NonListWindowHeight + colorCount * ColorOptionRowHeight;
            MinHeight = Height;
            MaxHeight = Height;
        }

        private void ColorName_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is TextBlock { DataContext: ThemeColorOption option })
            {
                ColorNameHoverChanged?.Invoke(IsMiniPlayerPaletteSelected, option.Key);
            }
        }

        private void ColorName_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is TextBlock { DataContext: ThemeColorOption option })
            {
                ColorNameHoverChanged?.Invoke(IsMiniPlayerPaletteSelected, null);
            }
        }

        private void PopulateColors(
            ObservableCollection<ThemeColorOption> colors,
            IReadOnlyDictionary<string, string> palette,
            IReadOnlyDictionary<string, string> names,
            bool isMiniPlayer)
        {
            foreach ((string key, string name) in names)
            {
                string value = palette.TryGetValue(key, out string? savedColor)
                    ? savedColor
                    : DefaultColors[key];
                var option = new ThemeColorOption(key, name, value);
                option.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ThemeColorOption.Hex) &&
                        TryParseColor(option.Hex, out Color color))
                    {
                        option.PreviewBrush = new SolidColorBrush(color);
                        if (!isUpdatingPalettes)
                        {
                            ThemeColorChanged?.Invoke(isMiniPlayer, key, color);
                        }
                    }
                };
                colors.Add(option);
            }
        }

        public string GetNextProfileName()
        {
            int profileNumber = 1;
            string name;
            do
            {
                name = $"Profile {profileNumber++}";
            }
            while (Profiles.Any(profile =>
                string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)));

            return name;
        }

        public void ShowProfileList()
        {
            isProfileListVisible = true;
            PaletteTabControl.Visibility = Visibility.Collapsed;
            ProfilePanel.Visibility = Visibility.Visible;
            ProfileButton.Content = "Colors";
            UpdateWindowHeight();
        }

        public void SetActiveProfile(ThemeColorProfile? activeProfile)
        {
            foreach (ThemeColorProfile profile in Profiles)
            {
                profile.IsActive = ReferenceEquals(profile, activeProfile);
            }

            ActiveProfileText.Text = activeProfile == null
                ? "Active profile: None"
                : $"Active profile: {activeProfile.Name}";
        }

        public void SetPalettes(
            IReadOnlyDictionary<string, string> standardColors,
            IReadOnlyDictionary<string, string> miniPlayerColors)
        {
            isUpdatingPalettes = true;
            try
            {
                UpdatePalette(StandardColors, standardColors);
                UpdatePalette(MiniPlayerColors, miniPlayerColors);
            }
            finally
            {
                isUpdatingPalettes = false;
            }
        }

        private static void UpdatePalette(
            IEnumerable<ThemeColorOption> options,
            IReadOnlyDictionary<string, string> palette)
        {
            foreach (ThemeColorOption option in options)
            {
                if (palette.TryGetValue(option.Key, out string? color))
                {
                    option.Hex = color;
                }
            }
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            isProfileListVisible = !isProfileListVisible;
            PaletteTabControl.Visibility = isProfileListVisible
                ? Visibility.Collapsed
                : Visibility.Visible;
            ProfilePanel.Visibility = isProfileListVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
            ProfileButton.Content = isProfileListVisible ? "Colors" : "Profile";
            UpdateWindowHeight();
        }

        private void AddProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ProfileAddRequested?.Invoke();
        }

        private void UseProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ThemeColorProfile profile)
            {
                ProfileSelected?.Invoke(profile);
            }
        }

        private void RenameProfileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetProfileFromMenuItem(sender) is not ThemeColorProfile profile)
            {
                return;
            }

            profile.IsRenaming = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (FindVisualChild<TextBox>(
                        (DependencyObject?)ProfileItemsControl?.ItemContainerGenerator
                            .ContainerFromItem(profile)) is TextBox textBox)
                {
                    textBox.Focus();
                    textBox.SelectAll();
                }
            }));
        }

        private void DeleteProfileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GetProfileFromMenuItem(sender) is ThemeColorProfile profile)
            {
                ProfileDeleteRequested?.Invoke(profile);
            }
        }

        private void ProfileName_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox textBox ||
                textBox.DataContext is not ThemeColorProfile profile)
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = GetNextProfileName();
            }

            profile.IsRenaming = false;
            ProfileNameChanged?.Invoke(profile);
        }

        private void ProfileName_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox textBox ||
                textBox.DataContext is not ThemeColorProfile profile)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                profile.IsRenaming = false;
                ProfileNameChanged?.Invoke(profile);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                profile.IsRenaming = false;
                e.Handled = true;
            }
        }

        private static ThemeColorProfile? GetProfileFromMenuItem(object sender)
        {
            return (sender as MenuItem)?.Parent is ContextMenu contextMenu
                ? (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as ThemeColorProfile
                : null;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent)
            where T : DependencyObject
        {
            if (parent == null)
            {
                return null;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                {
                    return result;
                }

                if (FindVisualChild<T>(child) is T descendant)
                {
                    return descendant;
                }
            }

            return null;
        }

        private static SolidColorBrush CreateBrush(
            IReadOnlyDictionary<string, string> colors,
            string key)
        {
            string value = colors.TryGetValue(key, out string? color)
                ? color
                : DefaultColors[key];
            return new SolidColorBrush(ParseColor(value));
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        public static bool TryParseColor(string? value, out Color color)
        {
            color = default;
            if (string.IsNullOrEmpty(value) ||
                (value.Length != 7 && value.Length != 9) ||
                value[0] != '#')
            {
                return false;
            }

            bool hasAlpha = value.Length == 9;
            int channelOffset = hasAlpha ? 3 : 1;
            if ((hasAlpha &&
                 !byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, null, out _)) ||
                !byte.TryParse(value.AsSpan(channelOffset, 2), NumberStyles.HexNumber, null, out byte red) ||
                !byte.TryParse(value.AsSpan(channelOffset + 2, 2), NumberStyles.HexNumber, null, out byte green) ||
                !byte.TryParse(value.AsSpan(channelOffset + 4, 2), NumberStyles.HexNumber, null, out byte blue))
            {
                return false;
            }

            byte alpha = 255;
            if (hasAlpha)
            {
                byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, null, out alpha);
            }

            color = Color.FromArgb(alpha, red, green, blue);
            return true;
        }

        private static Color ParseColor(string value)
        {
            if (TryParseColor(value, out Color color))
            {
                return color;
            }

            return System.Windows.Media.Colors.Black;
        }
    }

    public sealed class AudioSpectrumOptions : INotifyPropertyChanged
    {
        private double sensitivity = 2.2;
        private double motionSpeed = 1.4;
        private double bassExpansion = 1.2;
        private double circleSize = 100;
        private double underlayCircleSize = 100;

        public double Sensitivity
        {
            get => sensitivity;
            set => SetValue(ref sensitivity, Math.Clamp(value, 0.5, 4));
        }

        public double MotionSpeed
        {
            get => motionSpeed;
            set => SetValue(ref motionSpeed, Math.Clamp(value, 0.2, 3));
        }

        public double BassExpansion
        {
            get => bassExpansion;
            set => SetValue(ref bassExpansion, Math.Clamp(value, 0, 3));
        }

        public double CircleSize
        {
            get => circleSize;
            set => SetValue(ref circleSize, Math.Clamp(value, 50, 180));
        }

        public double UnderlayCircleSize
        {
            get => underlayCircleSize;
            set => SetValue(ref underlayCircleSize, Math.Clamp(value, 50, 180));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetValue(ref double field, double value, [CallerMemberName] string? propertyName = null)
        {
            if (Math.Abs(field - value) < 0.001)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class ThemeColorProfile : INotifyPropertyChanged
    {
        private string name = "";
        private bool isActive;
        private bool isRenaming;

        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name
        {
            get => name;
            set
            {
                if (name == value)
                {
                    return;
                }

                name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public Dictionary<string, string> StandardColors { get; set; } = new();
        public Dictionary<string, string> MiniPlayerColors { get; set; } = new();

        [JsonIgnore]
        public bool IsActive
        {
            get => isActive;
            set
            {
                if (isActive == value)
                {
                    return;
                }

                isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        [JsonIgnore]
        public bool IsRenaming
        {
            get => isRenaming;
            set
            {
                if (isRenaming == value)
                {
                    return;
                }

                isRenaming = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ThemeColorProfile()
        {
        }

        public ThemeColorProfile(
            string name,
            Dictionary<string, string> standardColors,
            Dictionary<string, string> miniPlayerColors)
        {
            Name = name;
            StandardColors = standardColors;
            MiniPlayerColors = miniPlayerColors;
        }
    }

    public sealed class ThemeColorOption : INotifyPropertyChanged
    {
        private string hex;
        private Brush previewBrush;
        private int red;
        private int green;
        private int blue;
        private int alpha = 255;

        public string Key { get; }
        public string Name { get; }
        public bool HasOpacity => Key is
            "AudioSpectrumBrush" or
            "AudioSpectrumRedBrush" or
            "AudioSpectrumBlueBrush";

        public string Hex
        {
            get => hex;
            set
            {
                if (hex == value)
                {
                    return;
                }

                hex = value;
                OnPropertyChanged();
                if (ThemeSettingsWindow.TryParseColor(value, out Color color))
                {
                    SetChannel(ref red, color.R, nameof(Red));
                    SetChannel(ref green, color.G, nameof(Green));
                    SetChannel(ref blue, color.B, nameof(Blue));
                    SetChannel(ref alpha, color.A, nameof(Alpha));
                }
            }
        }

        public int Red
        {
            get => red;
            set => SetRgbChannel(ref red, value, nameof(Red));
        }

        public int Green
        {
            get => green;
            set => SetRgbChannel(ref green, value, nameof(Green));
        }

        public int Blue
        {
            get => blue;
            set => SetRgbChannel(ref blue, value, nameof(Blue));
        }

        public int Alpha
        {
            get => alpha;
            set
            {
                value = Math.Clamp(value, 0, 255);
                if (alpha == value)
                {
                    return;
                }

                alpha = value;
                OnPropertyChanged();
                UpdateHex();
            }
        }

        public Brush PreviewBrush
        {
            get => previewBrush;
            set
            {
                previewBrush = value;
                OnPropertyChanged();
            }
        }

        public ThemeColorOption(string key, string name, string hex)
        {
            Key = key;
            Name = name;
            this.hex = hex;
            if (ThemeSettingsWindow.TryParseColor(hex, out Color initialColor))
            {
                red = initialColor.R;
                green = initialColor.G;
                blue = initialColor.B;
                alpha = initialColor.A;
            }
            previewBrush = new SolidColorBrush(
                ThemeSettingsWindow.TryParseColor(hex, out Color color)
                    ? color
                    : System.Windows.Media.Colors.Black);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetRgbChannel(ref int channel, int value, string propertyName)
        {
            value = Math.Clamp(value, 0, 255);
            if (channel == value)
            {
                return;
            }

            channel = value;
            OnPropertyChanged(propertyName);
            UpdateHex();
        }

        private void UpdateHex()
        {
            string newHex = HasOpacity && alpha < 255
                ? $"#{alpha:X2}{red:X2}{green:X2}{blue:X2}"
                : $"#{red:X2}{green:X2}{blue:X2}";
            if (hex != newHex)
            {
                hex = newHex;
                OnPropertyChanged(nameof(Hex));
            }
        }

        private void SetChannel(ref int channel, int value, string propertyName)
        {
            if (channel != value)
            {
                channel = value;
                OnPropertyChanged(propertyName);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
