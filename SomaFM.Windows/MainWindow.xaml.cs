using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using VisualizerRectangle = System.Windows.Shapes.Rectangle;

namespace SomaFM.Windows;

public partial class MainWindow : Window
{
    private const string StateFileName = "somafm-state.json";
    private const string StationsStateFileName = "somafm-stations.json";
    private static readonly ColorPalette[] ColorPalettes =
    [
        new("SomaFM", "#F08A62", "#2B3034", ["#F08A62", "#ECA36D", "#D7B56E", "#9DAD79", "#78B29A", "#65B9B5"]),
        new("Ocean", "#54B7D1", "#24404A", ["#54B7D1", "#5BC3BE", "#69CDA0", "#8BD17A", "#AEDC72", "#D3E177"]),
        new("Violet", "#B18AE0", "#3C304A", ["#B18AE0", "#C58BE0", "#D88FD0", "#E99DB3", "#F0B18F", "#F3CC7E"])
    ];

    private enum StationSortOrder
    {
        Alphabetical,
        MostListened
    }

    private sealed record ColorPalette(string Name, string Accent, string Border, string[] VisualizerColors);

    private readonly SomaFmClient _client = new();
    private readonly StreamPlayer _player;
    private readonly VisualizerRectangle[] _visualizerBars;
    private readonly DispatcherTimer _metadataTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _listenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _trayIconImage;
    private readonly Dictionary<string, long> _totalListenSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteStationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Controls.MenuItem> _paletteMenuItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolStripMenuItem> _trayPaletteMenuItems = new(StringComparer.Ordinal);
    private readonly Dictionary<StationSortOrder, System.Windows.Controls.MenuItem> _stationSortMenuItems = [];
    private readonly Dictionary<StationSortOrder, ToolStripMenuItem> _trayStationSortMenuItems = [];
    private readonly string _statePath;
    private readonly string _stationsPath;
    private ToolStripMenuItem _trayVisualizerMenuItem = null!;
    private ToolStripMenuItem _trayStartupPlaybackMenuItem = null!;
    private System.Windows.Controls.MenuItem _startupPlaybackMenuItem = null!;
    private IReadOnlyList<Station> _stations = [];
    private Station[] _stationPickerItems = [];
    private Station[] _visibleStations = [];
    private Station? _currentStation;
    private string? _lastStationId;
    private StationSortOrder _stationSortOrder;
    private string _colorPaletteName = "SomaFM";
    private bool _visualizerEnabled = true;
    private const int MinVisualizerBars = 5;
    private const int MaxVisualizerBars = 24;
    private const int DefaultVisualizerBars = 12;
    private const double VisualizerBarUnit = 6;
    private const double ChromeWidth = 22;
    private const double RequiredWidth = 46;
    private const double MinStationTextWidth = 118;
    private int _activeBarCount = DefaultVisualizerBars;
    private double _barWidth;
    private bool _rememberPlacement = true;
    private bool _placeSaved;
    private double _placeRelX;
    private double _placeRelY;
    private string? _placeScreen;
    private double _savedVolume = 80;
    private readonly DispatcherTimer _volumePersistTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private System.Windows.Controls.MenuItem _rememberPlacementMenuItem = null!;
    private ToolStripMenuItem _trayRememberPlacementMenuItem = null!;
    private bool _playLastStationOnStartup;
    private bool _notifyOnTrackChange;
    private bool _showSessionPlaytime;
    private readonly Dictionary<string, long> _sessionListenSeconds = new(StringComparer.OrdinalIgnoreCase);
    private string _lastTrackText = "";
    private System.Windows.Controls.MenuItem _notifyMenuItem = null!;
    private System.Windows.Controls.MenuItem _sessionTimeMenuItem = null!;
    private ToolStripMenuItem _trayNotifyMenuItem = null!;
    private ToolStripMenuItem _traySessionTimeMenuItem = null!;
    private bool _isPlaying;
    private bool _allowClose;
    private DateTime _lastListenCheckpoint = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomaFM-Windows");
        _statePath = System.IO.Path.Combine(appDataPath, StateFileName);
        _stationsPath = System.IO.Path.Combine(appDataPath, StationsStateFileName);
        LoadPersistentState();

        _player = new StreamPlayer { Volume = (float)(VolumeSlider.Value / 100) };
        _player.LevelsChanged += AudioOutput_LevelsChanged;
        _visualizerBars = CreateVisualizerBars();
        _player.PlayingChanged += playing => Dispatcher.BeginInvoke(() => SetPlaying(playing));
        _metadataTimer.Tick += async (_, _) => await RefreshTrackAsync();
        _listenTimer.Tick += (_, _) => AccumulateListenTime();
        _trayIconImage = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "SomaFM",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show SomaFM", null, (_, _) => ShowWindow());
        menu.Items.Add("Play / Pause", null, (_, _) => TogglePlayback());
        menu.Items.Add(new ToolStripSeparator());
        _trayVisualizerMenuItem = new ToolStripMenuItem("Show visualizer") { CheckOnClick = true };
        _trayVisualizerMenuItem.Click += (_, _) => SetVisualizerEnabled(_trayVisualizerMenuItem.Checked);
        menu.Items.Add(_trayVisualizerMenuItem);
        _trayStartupPlaybackMenuItem = new ToolStripMenuItem("Play last station on startup") { CheckOnClick = true };
        _trayStartupPlaybackMenuItem.Click += (_, _) => SetPlayLastStationOnStartup(_trayStartupPlaybackMenuItem.Checked);
        menu.Items.Add(_trayStartupPlaybackMenuItem);
        _trayNotifyMenuItem = new ToolStripMenuItem("Notify on track change") { CheckOnClick = true };
        _trayNotifyMenuItem.Click += (_, _) => SetNotifyOnTrackChange(_trayNotifyMenuItem.Checked);
        menu.Items.Add(_trayNotifyMenuItem);
        _trayRememberPlacementMenuItem = new ToolStripMenuItem("Remember last size and location") { CheckOnClick = true };
        _trayRememberPlacementMenuItem.Click += (_, _) => SetRememberPlacement(_trayRememberPlacementMenuItem.Checked);
        menu.Items.Add(_trayRememberPlacementMenuItem);
        _traySessionTimeMenuItem = new ToolStripMenuItem("Playtime: this session (off = all time)") { CheckOnClick = true };
        _traySessionTimeMenuItem.Click += (_, _) => SetShowSessionPlaytime(_traySessionTimeMenuItem.Checked);
        menu.Items.Add(_traySessionTimeMenuItem);
        var traySortMenu = new ToolStripMenuItem("Favourites station sorting");
        AddTraySortItem(traySortMenu, StationSortOrder.Alphabetical, "Alphabetical (A-Z)");
        AddTraySortItem(traySortMenu, StationSortOrder.MostListened, "Most listened");
        menu.Items.Add(traySortMenu);
        var trayPaletteMenu = new ToolStripMenuItem("Color palette");
        foreach (var palette in ColorPalettes)
        {
            var item = new ToolStripMenuItem(palette.Name) { CheckOnClick = true };
            item.Click += (_, _) => SetColorPalette(palette.Name);
            _trayPaletteMenuItems.Add(palette.Name, item);
            trayPaletteMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(trayPaletteMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(VersionLabel) { Enabled = false });
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        ConfigureOptionsMenu();
        MinWidth = ChromeWidth + RequiredWidth + MinStationTextWidth;
        var textDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        textDescriptor.AddValueChanged(NowPlayingStation, (_, _) => UpdateMaxWidth());
        textDescriptor.AddValueChanged(NowPlayingTrack, (_, _) => UpdateMaxWidth());
        UpdateMaxWidth();
        var defaultWidth = MinWidth + 288 + VisualizerWidth(DefaultVisualizerBars) + 130;
        Width = Math.Clamp(_rememberPlacement && _barWidth > 0 ? _barWidth : defaultWidth, MinWidth, MaxWidth);
        _rememberPlacementMenuItem.IsChecked = _rememberPlacement;
        _trayRememberPlacementMenuItem.Checked = _rememberPlacement;
        VolumeSlider.Value = _savedVolume;
        _volumePersistTimer.Tick += (_, _) =>
        {
            _volumePersistTimer.Stop();
            PersistState();
        };
        SetColorPalette(_colorPaletteName, persist: false);
        SetVisualizerEnabled(_visualizerEnabled, persist: false);
        SetPlayLastStationOnStartup(_playLastStationOnStartup, persist: false);
        SetNotifyOnTrackChange(_notifyOnTrackChange, persist: false);
        SetShowSessionPlaytime(_showSessionPlaytime, persist: false);
        UpdateStationSortMenuChecks();
        UpdateFavoriteButton();
        UpdateListeningTimeDisplay();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("SomaFM.Windows.Assets.somafm.ico")
            ?? throw new InvalidOperationException("The SomaFM tray icon resource is missing.");
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    private void ConfigureOptionsMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        _startupPlaybackMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "Play last station on startup",
            IsCheckable = true
        };
        _startupPlaybackMenuItem.Click += (_, _) => SetPlayLastStationOnStartup(_startupPlaybackMenuItem.IsChecked);
        menu.Items.Add(_startupPlaybackMenuItem);

        _notifyMenuItem = new System.Windows.Controls.MenuItem { Header = "Notify on track change", IsCheckable = true };
        _notifyMenuItem.Click += (_, _) => SetNotifyOnTrackChange(_notifyMenuItem.IsChecked);
        menu.Items.Add(_notifyMenuItem);

        _rememberPlacementMenuItem = new System.Windows.Controls.MenuItem { Header = "Remember last size and location", IsCheckable = true };
        _rememberPlacementMenuItem.Click += (_, _) => SetRememberPlacement(_rememberPlacementMenuItem.IsChecked);
        menu.Items.Add(_rememberPlacementMenuItem);

        _sessionTimeMenuItem = new System.Windows.Controls.MenuItem { Header = "Playtime: this session (off = all time)", IsCheckable = true };
        _sessionTimeMenuItem.Click += (_, _) => SetShowSessionPlaytime(_sessionTimeMenuItem.IsChecked);
        menu.Items.Add(_sessionTimeMenuItem);

        var sortMenu = new System.Windows.Controls.MenuItem { Header = "Favourites station sorting" };
        AddWindowSortItem(sortMenu, StationSortOrder.Alphabetical, "Alphabetical (A-Z)");
        AddWindowSortItem(sortMenu, StationSortOrder.MostListened, "Most listened");
        menu.Items.Add(sortMenu);

        var visualizerItem = new System.Windows.Controls.MenuItem
        {
            Header = "Show visualizer",
            IsCheckable = true
        };
        visualizerItem.Click += (_, _) => SetVisualizerEnabled(visualizerItem.IsChecked);
        menu.Items.Add(visualizerItem);

        var paletteMenu = new System.Windows.Controls.MenuItem { Header = "Color palette" };
        foreach (var palette in ColorPalettes)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = palette.Name,
                IsCheckable = true
            };
            item.Click += (_, _) => SetColorPalette(palette.Name);
            _paletteMenuItems.Add(palette.Name, item);
            paletteMenu.Items.Add(item);
        }
        menu.Items.Add(paletteMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = VersionLabel, IsEnabled = false });
        OptionsButton.ContextMenu = menu;
    }

    private static string VersionLabel =>
        $"SomaFM v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    private void AddWindowSortItem(System.Windows.Controls.MenuItem parent, StationSortOrder order, string label)
    {
        var item = new System.Windows.Controls.MenuItem { Header = label, IsCheckable = true };
        item.Click += (_, _) => SetStationSortOrder(order);
        _stationSortMenuItems.Add(order, item);
        parent.Items.Add(item);
    }

    private void AddTraySortItem(ToolStripMenuItem parent, StationSortOrder order, string label)
    {
        var item = new ToolStripMenuItem(label) { CheckOnClick = true };
        item.Click += (_, _) => SetStationSortOrder(order);
        _trayStationSortMenuItems.Add(order, item);
        parent.DropDownItems.Add(item);
    }

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = OptionsButton.ContextMenu!;
        menu.PlacementTarget = OptionsButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SetPlayLastStationOnStartup(bool enabled, bool persist = true)
    {
        _playLastStationOnStartup = enabled;
        _startupPlaybackMenuItem.IsChecked = enabled;
        _trayStartupPlaybackMenuItem.Checked = enabled;
        if (persist)
            PersistState();
    }

    private void SetNotifyOnTrackChange(bool enabled, bool persist = true)
    {
        _notifyOnTrackChange = enabled;
        _notifyMenuItem.IsChecked = enabled;
        _trayNotifyMenuItem.Checked = enabled;
        if (persist)
            PersistState();
    }

    private void SetShowSessionPlaytime(bool enabled, bool persist = true)
    {
        _showSessionPlaytime = enabled;
        _sessionTimeMenuItem.IsChecked = enabled;
        _traySessionTimeMenuItem.Checked = enabled;
        ListeningTimeLabel.ToolTip = "Click to switch between session and total playtime";
        UpdateListeningTimeDisplay();
        if (persist)
            PersistState();
    }

    private void ListeningTimeLabel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetShowSessionPlaytime(!_showSessionPlaytime);
    }

    private void SetStationSortOrder(StationSortOrder order)
    {
        _stationSortOrder = order;
        UpdateStationSortMenuChecks();
        UpdateStationPickerOrder();
        PersistState();
    }

    private void UpdateStationSortMenuChecks()
    {
        foreach (var (order, item) in _stationSortMenuItems)
            item.IsChecked = order == _stationSortOrder;
        foreach (var (order, item) in _trayStationSortMenuItems)
            item.Checked = order == _stationSortOrder;
    }

    private void SetVisualizerEnabled(bool enabled, bool persist = true)
    {
        _visualizerEnabled = enabled;
        if (IsLoaded)
            UpdateMaxWidth();
        ApplyLayout();
        _trayVisualizerMenuItem.Checked = enabled;
        foreach (var item in OptionsButton.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (item.Header as string == "Show visualizer")
                item.IsChecked = enabled;
        }
        if (persist)
            PersistState();
    }

    private void SetColorPalette(string name, bool persist = true)
    {
        var palette = ColorPalettes.First(item => item.Name == name);
        _colorPaletteName = palette.Name;
        var brushConverter = new BrushConverter();
        var accentBrush = (System.Windows.Media.Brush)brushConverter.ConvertFromString(palette.Accent)!;
        AccentLogoText.Foreground = accentBrush;
        BarSurface.BorderBrush = (System.Windows.Media.Brush)brushConverter.ConvertFromString(palette.Border)!;
        System.Windows.Application.Current.Resources["AccentBrush"] = accentBrush;
        for (var index = 0; index < _visualizerBars.Length; index++)
            _visualizerBars[index].Fill = (System.Windows.Media.Brush)brushConverter.ConvertFromString(
                palette.VisualizerColors[index % palette.VisualizerColors.Length])!;

        foreach (var (paletteName, item) in _paletteMenuItems)
            item.IsChecked = paletteName == palette.Name;
        foreach (var (paletteName, item) in _trayPaletteMenuItems)
            item.Checked = paletteName == palette.Name;
        if (persist)
            PersistState();
    }

    private async Task LoadStationsAsync()
    {
        var cachedStations = LoadCachedStations();
        if (cachedStations.Count > 0)
        {
            _stations = cachedStations;
            UpdateStationPickerOrder();
            TryStartLastStation();
            if (string.IsNullOrEmpty(NowPlayingTrack.Text) || NowPlayingTrack.Text == "Live radio")
                NowPlayingTrack.Text = "Choose a station";
        }
        else
        {
            NowPlayingTrack.Text = "Loading stations…";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var freshStations = await _client.GetStationsAsync();
                if (freshStations.Count == 0)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (_currentStation is not null && freshStations.All(station => station.Id != _currentStation.Id))
                        freshStations = freshStations.Append(_currentStation).ToArray();

                    _stations = freshStations;
                    SaveCachedStations(_stations);
                    UpdateStationPickerOrder();
                    TryStartLastStation();
                    if (NowPlayingTrack.Text is "Loading stations…" or "Station catalog unavailable")
                        NowPlayingTrack.Text = "Choose a station";
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_stations.Count == 0)
                    {
                        NowPlayingTrack.Text = "Station catalog unavailable";
                        _trayIcon.Text = "SomaFM - Catalog unavailable";
                    }
                });
            }
        });
    }

    private void TryStartLastStation()
    {
        if (!_playLastStationOnStartup || _currentStation is not null || string.IsNullOrEmpty(_lastStationId))
            return;

        var station = _stations.FirstOrDefault(item =>
            item.Id.Equals(_lastStationId, StringComparison.OrdinalIgnoreCase));
        if (station is not null)
            StartStation(station);
    }

    private IReadOnlyList<Station> LoadCachedStations()
    {
        if (!File.Exists(_stationsPath))
            return [];

        try
        {
            var json = File.ReadAllText(_stationsPath);
            var stations = JsonSerializer.Deserialize<List<Station>>(json);
            return stations is { Count: > 0 } ? stations : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void SaveCachedStations(IEnumerable<Station> stations)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_stationsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(stations.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stationsPath, json);
        }
        catch (Exception)
        {
        }
    }

    private VisualizerRectangle[] CreateVisualizerBars()
    {
        var colors = ColorPalettes.First(palette => palette.Name == _colorPaletteName).VisualizerColors;
        return Enumerable.Range(0, MaxVisualizerBars)
            .Select(index => new VisualizerRectangle
            {
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                    .ConvertFromString(colors[index % colors.Length])!,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(1, 0, 1, 0)
            })
            .ToArray();
    }

    private void StationButton_Click(object sender, RoutedEventArgs e)
    {
        if (StationPopup.IsOpen)
        {
            StationPopup.IsOpen = false;
            return;
        }

        StationSearchBox.Text = "";
        UpdateStationPickerOrder();
        StationPopup.IsOpen = true;
    }

    private void StationPopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StationSearchBox.Focus();
            System.Windows.Input.Keyboard.Focus(StationSearchBox);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void StationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStationPickerOrder();

    private void StationSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            StationPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && _visibleStations.FirstOrDefault() is { } station)
        {
            StationPopup.IsOpen = false;
            if (station.Id != _currentStation?.Id)
                StartStation(station);
            e.Handled = true;
        }
    }

    private void StationItem_Click(object sender, RoutedEventArgs e)
    {
        StationPopup.IsOpen = false;
        if (((FrameworkElement)sender).DataContext is Station station && station.Id != _currentStation?.Id)
            StartStation(station);
    }

    private void StationItem_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is not Station station)
            return;

        if (!_favoriteStationIds.Remove(station.Id))
            _favoriteStationIds.Add(station.Id);

        UpdateStationPickerOrder();
        UpdateFavoriteButton();
        PersistState();
    }

    private void StationPopup_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    private void StationPopup_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;


    private void StartStation(Station station)
    {
        try
        {
            _metadataTimer.Stop();
            _currentStation = station;
            _lastStationId = station.Id;
            _lastListenCheckpoint = DateTime.UtcNow;
            UpdateFavoriteButton();
            UpdateListeningTimeDisplay();
            PersistState();
            NowPlayingStation.Text = station.StreamInfo.Length == 0
                ? station.Title
                : $"{station.Title}  ·  {station.StreamInfo}";
            StationButton.ToolTip = station.StreamInfo.Length == 0
                ? station.Title
                : $"{station.Title} ({station.StreamInfo})\nClick to choose a station";
            _trayIcon.Text = TrayText(station.StreamInfo.Length == 0 ? station.Title : $"{station.Title} ({station.StreamInfo})");
            NowPlayingTrack.Text = "Connecting…";
            _lastTrackText = "";
            NowPlayingArtwork.Source = string.IsNullOrEmpty(station.ImageUrl)
                ? null
                : new BitmapImage(new Uri(station.ImageUrl));
            _player.Play(station.StreamUrl);
            _metadataTimer.Start();
            _ = RefreshTrackAsync();
        }
        catch (Exception error)
        {
            NowPlayingTrack.Text = $"Could not start playback: {error.Message}";
        }
    }

    private async Task RefreshTrackAsync()
    {
        var station = _currentStation;
        if (station is null)
            return;
        try
        {
            var (title, artist) = await _client.GetTrackAsync(station.Id);
            if (_currentStation?.Id != station.Id || title.Length == 0)
                return;
            var text = artist.Length == 0 ? title : $"{artist} - {title}";
            NowPlayingTrack.Text = text;
            _trayIcon.Text = TrayText(station.StreamInfo.Length == 0 ? station.Title : $"{station.Title} ({station.StreamInfo})");
            if (_notifyOnTrackChange && text != _lastTrackText)
                _trayIcon.ShowBalloonTip(4000, station.Title, text, ToolTipIcon.None);
            _lastTrackText = text;
        }
        catch (HttpRequestException)
        {
        }
    }

    private void SetPlaying(bool playing)
    {
        if (playing)
        {
            _lastListenCheckpoint = DateTime.UtcNow;
            _listenTimer.Start();
        }
        else
        {
            AccumulateListenTime();
            _listenTimer.Stop();
        }

        _isPlaying = playing;
        StationButton.Opacity = playing || _currentStation is null ? 1 : 0.55;
    }

    private static string TrayText(string stationTitle)
    {
        var text = $"SomaFM - {stationTitle}";
        return text.Length <= 63 ? text : text[..63];
    }

    private void BarSurface_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        TogglePlayback();
    }

    private void TogglePlayback()
    {
        if (_currentStation is null)
        {
            if (_stationPickerItems.FirstOrDefault() is { } station)
                StartStation(station);
            return;
        }
        if (_isPlaying)
            _player.Stop();
        else
            StartStation(_currentStation);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
        {
            _player.Volume = (float)(e.NewValue / 100);
            _savedVolume = e.NewValue;
            _volumePersistTimer.Stop();
            _volumePersistTimer.Start();
        }
    }

    private void AudioOutput_LevelsChanged(double[] levels)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var count = _activeBarCount;
            for (var index = 0; index < count; index++)
            {
                var start = index * levels.Length / count;
                var end = Math.Max(start + 1, (index + 1) * levels.Length / count);
                var level = 0.0;
                for (var band = start; band < end && band < levels.Length; band++)
                    level = Math.Max(level, levels[band]);
                _visualizerBars[index].Height = Math.Max(2, level * 14);
            }
        });
    }

    private void VisualizerPanel_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        var index = Array.FindIndex(ColorPalettes, palette => palette.Name == _colorPaletteName);
        SetColorPalette(ColorPalettes[(index + 1) % ColorPalettes.Length].Name);
    }

    private void UpdateMaxWidth()
    {
        NowPlayingStation.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        NowPlayingTrack.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = Math.Max(NowPlayingStation.DesiredSize.Width, NowPlayingTrack.DesiredSize.Width);
        var full = ChromeWidth + RequiredWidth + 8 + 288 + textWidth
            + (_visualizerEnabled ? VisualizerWidth(MaxVisualizerBars) : 0);
        MaxWidth = Math.Max(MinWidth, Math.Min(full, SystemParameters.WorkArea.Width));
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var budget = Width - ChromeWidth - RequiredWidth - MinStationTextWidth;
        var optional = new List<(UIElement Element, double Cost)>
        {
            (FavoriteButton, 32),
            (OptionsButton, 24),
            (VolumeSlider, 96),
            (ArtworkBorder, 32),
            (ListeningTimeLabel, 58),
            (LogoPanel, 46)
        };
        if (_visualizerEnabled)
            optional.Add((VisualizerPanel, VisualizerWidth(MinVisualizerBars)));

        var fitting = true;
        foreach (var (element, cost) in optional)
        {
            fitting = fitting && budget >= cost;
            element.Visibility = fitting ? Visibility.Visible : Visibility.Collapsed;
            if (fitting)
                budget -= cost;
        }

        if (_visualizerEnabled)
        {
            if (VisualizerPanel.Visibility != Visibility.Visible)
            {
                _activeBarCount = MinVisualizerBars;
                return;
            }

            _activeBarCount = Math.Clamp(MinVisualizerBars + (int)(budget / 24), MinVisualizerBars, MaxVisualizerBars);
            VisualizerBars.Children.Clear();
            for (var index = 0; index < _activeBarCount; index++)
                VisualizerBars.Children.Add(_visualizerBars[index]);
            VisualizerBars.Columns = _activeBarCount;
            VisualizerBars.Width = _activeBarCount * VisualizerBarUnit;
        }
        else
        {
            VisualizerPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static double VisualizerWidth(int bars) => bars * VisualizerBarUnit + 14;

    private void ResizeBar(double newWidth, bool anchorRight)
    {
        newWidth = Math.Clamp(newWidth, MinWidth, MaxWidth);
        var change = newWidth - Width;
        if (change == 0)
            return;
        if (!anchorRight)
            Left -= change;
        Width = newWidth;
    }

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBar(Width + e.HorizontalChange, anchorRight: true);

    private void LeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e) => ResizeBar(Width - e.HorizontalChange, anchorRight: false);

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        SnapToScreenEdge();
        PersistState();
    }

    private static (double Left, double Top, double Right, double Bottom) GetAreaDips(Screen screen, DpiScale dpi)
    {
        var area = screen.WorkingArea;
        return (area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Right / dpi.DpiScaleX, area.Bottom / dpi.DpiScaleY);
    }

    private void SavePlacement()
    {
        if (!_rememberPlacement || !IsLoaded)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new System.Drawing.Point(
            (int)((Left + ActualWidth / 2) * dpi.DpiScaleX),
            (int)((Top + ActualHeight / 2) * dpi.DpiScaleY));
        var screen = Screen.FromPoint(center);
        var (left, top, right, bottom) = GetAreaDips(screen, dpi);
        var rangeX = right - left - ActualWidth;
        var rangeY = bottom - top - ActualHeight;
        _barWidth = Width;
        _placeScreen = screen.DeviceName;
        _placeRelX = rangeX > 0 ? Math.Clamp((Left - left) / rangeX, 0, 1) : 0.5;
        _placeRelY = rangeY > 0 ? Math.Clamp((Top - top) / rangeY, 0, 1) : 0;
        _placeSaved = true;
        PersistState();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private void RestorePlacement()
    {
        var screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == _placeScreen) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var area = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPx = ActualWidth * dpi.DpiScaleX;
        var heightPx = ActualHeight * dpi.DpiScaleY;
        var x = area.Left + _placeRelX * Math.Max(0, area.Width - widthPx);
        var y = area.Top + _placeRelY * Math.Max(0, area.Height - heightPx);
        // Position in physical pixels so a monitor with a different DPI doesn't skew the result.
        const uint noSizeNoZOrderNoActivate = 0x0001 | 0x0004 | 0x0010;
        SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, IntPtr.Zero, (int)x, (int)y, 0, 0, noSizeNoZOrderNoActivate);
        // The window may have been rescaled for the new monitor; make sure it is fully visible.
        Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Loaded);
    }

    private void KeepOnScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new System.Drawing.Point(
            (int)((Left + ActualWidth / 2) * dpi.DpiScaleX),
            (int)((Top + ActualHeight / 2) * dpi.DpiScaleY));
        var (left, top, right, bottom) = GetAreaDips(Screen.FromPoint(center), dpi);
        Left = Math.Max(left, Math.Min(Left, right - ActualWidth));
        Top = Math.Max(top, Math.Min(Top, bottom - ActualHeight));
    }

    private void SetRememberPlacement(bool enabled, bool persist = true)
    {
        _rememberPlacement = enabled;
        _rememberPlacementMenuItem.IsChecked = enabled;
        _trayRememberPlacementMenuItem.Checked = enabled;
        if (enabled)
            SavePlacement();
        else if (persist)
            PersistState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_rememberPlacement && _placeSaved)
        {
            RestorePlacement();
        }
        else
        {
            Left = Math.Max(0, (SystemParameters.WorkArea.Width - ActualWidth) / 2);
            Top = SystemParameters.WorkArea.Top;
        }

        await LoadStationsAsync();
    }

    private System.Windows.Point? _pendingDragStart;

    private void BarSurface_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _pendingDragStart = null;
        if (e.OriginalSource is not DependencyObject source
            || PresentationSource.FromVisual(source as Visual ?? BarSurface) != PresentationSource.FromVisual(this))
            return;

        for (DependencyObject? element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is Slider or System.Windows.Controls.TextBox or ScrollViewer)
                return;
        }

        _pendingDragStart = e.GetPosition(this);
    }

    private void BarSurface_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
            return;

        e.Handled = true;
        StationPopup.IsOpen = false;
        CenterHorizontally();
    }

    private void CenterHorizontally()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new System.Drawing.Point(
            (int)((Left + ActualWidth / 2) * dpi.DpiScaleX),
            (int)((Top + ActualHeight / 2) * dpi.DpiScaleY));
        var area = Screen.FromPoint(center).WorkingArea;
        Left = (area.Left + area.Width / 2.0) / dpi.DpiScaleX - ActualWidth / 2;
        SavePlacement();
    }

    private void BarSurface_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pendingDragStart is not { } start)
            return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _pendingDragStart = null;
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pendingDragStart = null;
        System.Windows.Input.Mouse.Capture(null);
        try
        {
            DragMove();
            SnapToScreenEdge();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void BarSurface_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => _pendingDragStart = null;

    private void BarSurface_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
            SnapToScreenEdge();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase or Slider or System.Windows.Controls.ComboBox or System.Windows.Controls.TextBox or PasswordBox or ToggleButton or ScrollViewer)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void SnapToScreenEdge()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = new System.Drawing.Point(
            (int)((Left + ActualWidth / 2) * dpi.DpiScaleX),
            (int)((Top + ActualHeight / 2) * dpi.DpiScaleY));
        var area = Screen.FromPoint(center).WorkingArea;
        double left = area.Left / dpi.DpiScaleX, right = area.Right / dpi.DpiScaleX;
        double top = area.Top / dpi.DpiScaleY, bottom = area.Bottom / dpi.DpiScaleY;
        const double snapDistance = 44;

        if (Math.Abs(Left - left) <= snapDistance)
            Left = left;
        else if (Math.Abs(Left + ActualWidth - right) <= snapDistance)
            Left = right - ActualWidth;
        if (Math.Abs(Top - top) <= snapDistance)
            Top = top;
        else if (Math.Abs(Top + ActualHeight - bottom) <= snapDistance)
            Top = bottom - ActualHeight;

        Left = Math.Max(left, Math.Min(Left, right - ActualWidth));
        Top = Math.Max(top, Math.Min(Top, bottom - ActualHeight));
        SavePlacement();
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStation is null)
            return;

        if (_favoriteStationIds.Contains(_currentStation.Id))
            _favoriteStationIds.Remove(_currentStation.Id);
        else
            _favoriteStationIds.Add(_currentStation.Id);

        UpdateStationPickerOrder();
        UpdateFavoriteButton();
        PersistState();
    }

    private void UpdateFavoriteButton()
    {
        if (_currentStation is null)
        {
            FavoriteButton.Content = "☆";
            FavoriteButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(215, 219, 222));
            return;
        }

        var isFavorite = _favoriteStationIds.Contains(_currentStation.Id);
        FavoriteButton.Content = isFavorite ? "★" : "☆";
        FavoriteButton.Foreground = new SolidColorBrush(isFavorite
            ? System.Windows.Media.Color.FromRgb(255, 196, 84)
            : System.Windows.Media.Color.FromRgb(215, 219, 222));
    }

    private void UpdateListeningTimeDisplay()
    {
        if (_currentStation is null)
        {
            ListeningTimeLabel.Text = "00:00:00";
            return;
        }

        var totalSeconds = (_showSessionPlaytime ? _sessionListenSeconds : _totalListenSeconds)
            .GetValueOrDefault(_currentStation.Id, 0L);
        ListeningTimeLabel.Text = FormatDuration(totalSeconds);
    }

    private static string FormatDuration(long totalSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private void AccumulateListenTime()
    {
        if (_currentStation is null || !_isPlaying)
            return;

        var now = DateTime.UtcNow;
        var elapsedSeconds = Math.Max(0, (long)(now - _lastListenCheckpoint).TotalSeconds);
        if (elapsedSeconds <= 0)
            return;

        _lastListenCheckpoint = now;
        var total = _totalListenSeconds.GetValueOrDefault(_currentStation.Id, 0L) + elapsedSeconds;
        _totalListenSeconds[_currentStation.Id] = total;
        _sessionListenSeconds[_currentStation.Id] = _sessionListenSeconds.GetValueOrDefault(_currentStation.Id, 0L) + elapsedSeconds;
        UpdateListeningTimeDisplay();
        if (_stationSortOrder == StationSortOrder.MostListened)
            UpdateStationPickerOrder();
        PersistState();
    }

    private void LoadPersistentState()
    {
        if (!File.Exists(_statePath))
            return;

        try
        {
            var json = File.ReadAllText(_statePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("favorites", out var favorites) && favorites.ValueKind == JsonValueKind.Array)
            {
                foreach (var favorite in favorites.EnumerateArray())
                {
                    var value = favorite.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        _favoriteStationIds.Add(value);
                }
            }

            if (root.TryGetProperty("listenSeconds", out var listenSeconds) && listenSeconds.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in listenSeconds.EnumerateObject())
                {
                    if (long.TryParse(entry.Value.GetString(), out var seconds))
                        _totalListenSeconds[entry.Name] = seconds;
                }
            }

            if (root.TryGetProperty("stationSort", out var stationSort)
                && stationSort.ValueKind == JsonValueKind.String
                && stationSort.GetString() == nameof(StationSortOrder.MostListened))
                _stationSortOrder = StationSortOrder.MostListened;

            if (root.TryGetProperty("lastStation", out var lastStation)
                && lastStation.ValueKind == JsonValueKind.String)
                _lastStationId = lastStation.GetString();

            if (root.TryGetProperty("playLastStationOnStartup", out var playLastStationOnStartup)
                && playLastStationOnStartup.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _playLastStationOnStartup = playLastStationOnStartup.GetBoolean();

            if (root.TryGetProperty("notifyOnTrackChange", out var notify)
                && notify.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _notifyOnTrackChange = notify.GetBoolean();

            if (root.TryGetProperty("showSessionPlaytime", out var sessionTime)
                && sessionTime.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _showSessionPlaytime = sessionTime.GetBoolean();

            if (root.TryGetProperty("barWidth", out var barWidth) && barWidth.ValueKind == JsonValueKind.Number)
                _barWidth = barWidth.GetDouble();

            if (root.TryGetProperty("rememberPlacement", out var remember)
                && remember.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _rememberPlacement = remember.GetBoolean();

            if (root.TryGetProperty("placeRelX", out var relX) && relX.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("placeRelY", out var relY) && relY.ValueKind == JsonValueKind.Number)
            {
                _placeRelX = Math.Clamp(relX.GetDouble(), 0, 1);
                _placeRelY = Math.Clamp(relY.GetDouble(), 0, 1);
                _placeSaved = true;
            }

            if (root.TryGetProperty("placeScreen", out var placeScreen) && placeScreen.ValueKind == JsonValueKind.String)
                _placeScreen = placeScreen.GetString();

            if (root.TryGetProperty("volume", out var volume) && volume.ValueKind == JsonValueKind.Number)
                _savedVolume = Math.Clamp(volume.GetDouble(), 0, 100);

            if (root.TryGetProperty("colorPalette", out var colorPalette)
                && colorPalette.ValueKind == JsonValueKind.String
                && ColorPalettes.Any(palette => palette.Name == colorPalette.GetString()))
                _colorPaletteName = colorPalette.GetString()!;

            if (root.TryGetProperty("visualizerEnabled", out var visualizerEnabled)
                && visualizerEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _visualizerEnabled = visualizerEnabled.GetBoolean();
        }
        catch (Exception)
        {
            // Ignore corrupted persisted state; the app should continue without it.
        }
    }

    private void PersistState()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var payload = new
            {
                favorites = _favoriteStationIds.OrderBy(id => id).ToArray(),
                listenSeconds = _totalListenSeconds.ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                stationSort = _stationSortOrder.ToString(),
                colorPalette = _colorPaletteName,
                visualizerEnabled = _visualizerEnabled,
                lastStation = _lastStationId,
                playLastStationOnStartup = _playLastStationOnStartup,
                notifyOnTrackChange = _notifyOnTrackChange,
                showSessionPlaytime = _showSessionPlaytime,
                barWidth = _barWidth,
                rememberPlacement = _rememberPlacement,
                placeRelX = _placeRelX,
                placeRelY = _placeRelY,
                placeScreen = _placeScreen,
                volume = _savedVolume
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch (Exception)
        {
            // Ignore persistence failures and keep the app responsive.
        }
    }

    private void UpdateStationPickerOrder()
    {
        foreach (var station in _stations)
        {
            station.IsFavorite = _favoriteStationIds.Contains(station.Id);
            var seconds = _totalListenSeconds.GetValueOrDefault(station.Id, 0L);
            station.PlayTime = seconds > 0 ? FormatDuration(seconds) : "";
        }

        var orderedStations = _stations
            .OrderBy(station => station.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _stationPickerItems = orderedStations;
        var query = StationSearchBox.Text.Trim();
        var matches = query.Length == 0
            ? orderedStations
            : orderedStations.Where(station => station.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
                .Concat(orderedStations.Where(station =>
                    !station.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)
                    && station.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .ToArray();
        var favorites = (_stationSortOrder == StationSortOrder.MostListened
            ? matches.Where(station => station.IsFavorite)
                .OrderByDescending(station => _totalListenSeconds.GetValueOrDefault(station.Id, 0L))
            : matches.Where(station => station.IsFavorite)).ToArray();
        var others = matches.Where(station => !station.IsFavorite).ToArray();
        _visibleStations = favorites.Concat(others).ToArray();

        FavoritesList.ItemsSource = favorites;
        OtherStationsList.ItemsSource = others;
        FavoritesHeader.Visibility = favorites.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesList.Visibility = FavoritesHeader.Visibility;
        OtherStationsHeader.Visibility = others.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void ExitApplication_Click(object sender, RoutedEventArgs e) => ExitApplication();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        AccumulateListenTime();
        _listenTimer.Stop();
        _metadataTimer.Stop();
        PersistState();

        if (_allowClose)
        {
            _trayIcon.Visible = false;
            _player.Dispose();
            _trayIcon.Dispose();
            _trayIconImage.Dispose();
            return;
        }
        e.Cancel = true;
        Hide();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }
}