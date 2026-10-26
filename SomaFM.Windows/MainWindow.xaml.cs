using LibVLCSharp.Shared;
using VLCMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
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
    private readonly SomaFmClient _client = new();
    private readonly LibVLC _libVlc;
    private readonly VLCMediaPlayer _player;
    private readonly AudioVisualizerOutput _audioOutput;
    private readonly VisualizerRectangle[] _visualizerBars;
    private readonly DispatcherTimer _metadataTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _listenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NotifyIcon _trayIcon;
    private readonly Dictionary<string, long> _totalListenSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteStationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath;
    private readonly string _stationsPath;
    private Media? _currentMedia;
    private IReadOnlyList<Station> _stations = [];
    private Station? _currentStation;
    private bool _isPlaying;
    private bool _isUpdatingStationPicker;
    private bool _allowClose;
    private DateTime _lastListenCheckpoint = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomaFM-Windows");
        _statePath = System.IO.Path.Combine(appDataPath, StateFileName);
        _stationsPath = System.IO.Path.Combine(appDataPath, StationsStateFileName);
        LoadPersistentState();

        Core.Initialize();
        _libVlc = new LibVLC();
        _player = new VLCMediaPlayer(_libVlc);
        _player.Volume = (int)VolumeSlider.Value;
        _audioOutput = new AudioVisualizerOutput();
        _audioOutput.LevelsChanged += AudioOutput_LevelsChanged;
        _player.SetAudioFormat("S16N", 44_100, 2);
        _player.SetAudioCallbacks(
            _audioOutput.PlayCallback,
            _audioOutput.PauseCallback,
            _audioOutput.ResumeCallback,
            _audioOutput.FlushCallback,
            _audioOutput.DrainCallback);
        _visualizerBars = CreateVisualizerBars();
        foreach (var bar in _visualizerBars)
            VisualizerBars.Children.Add(bar);
        _player.Playing += (_, _) => Dispatcher.Invoke(() => SetPlaying(true));
        _player.Paused += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _player.Stopped += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _metadataTimer.Tick += async (_, _) => await RefreshTrackAsync();
        _listenTimer.Tick += (_, _) => AccumulateListenTime();
        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "SomaFM",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show SomaFM", null, (_, _) => ShowWindow());
        menu.Items.Add("Play / Pause", null, (_, _) => TogglePlayback());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        UpdateFavoriteButton();
        UpdateListeningTimeDisplay();
    }

    private async Task LoadStationsAsync()
    {
        var cachedStations = LoadCachedStations();
        if (cachedStations.Count > 0)
        {
            _stations = cachedStations;
            UpdateStationPickerOrder();
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
        var colors = new[] { "#F08A62", "#ECA36D", "#D7B56E", "#9DAD79", "#78B29A", "#65B9B5" };
        return Enumerable.Range(0, 12)
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

    private void StationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingStationPicker && StationPicker.SelectedItem is Station station && station.Id != _currentStation?.Id)
            StartStation(station);
    }

    private void StartStation(Station station)
    {
        try
        {
            _metadataTimer.Stop();
            _player.Stop();
            _currentMedia?.Dispose();
            _currentStation = station;
            _lastListenCheckpoint = DateTime.UtcNow;
            UpdateFavoriteButton();
            UpdateListeningTimeDisplay();
            NowPlayingStation.Text = station.Title;
            NowPlayingTrack.Text = "Connecting…";
            _trayIcon.Text = $"SomaFM - {station.Title}";
            NowPlayingArtwork.Source = string.IsNullOrEmpty(station.ImageUrl)
                ? null
                : new BitmapImage(new Uri(station.ImageUrl));
            _currentMedia = new Media(_libVlc, new Uri(station.StreamUrl));
            _player.Play(_currentMedia);
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
            NowPlayingTrack.Text = artist.Length == 0 ? title : $"{artist} - {title}";
            _trayIcon.Text = $"SomaFM - {station.Title}";
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
        PlayButton.Content = playing ? "Pause" : "Play";
        PlayButton.IsEnabled = _currentStation is not null;
    }

    private void TogglePlayback()
    {
        if (_currentStation is null)
        {
            if (StationPicker.SelectedItem is Station station)
                StartStation(station);
            return;
        }
        if (_currentMedia is null)
            StartStation(_currentStation);
        else if (_isPlaying)
            _player.Pause();
        else
            _player.Play();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
            _player.Volume = (int)e.NewValue;
    }

    private void AudioOutput_LevelsChanged(double[] levels)
    {
        Dispatcher.BeginInvoke(() =>
        {
            for (var index = 0; index < _visualizerBars.Length; index++)
                _visualizerBars[index].Height = Math.Max(2, levels[index] * 34);
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Width = Math.Max(760, Math.Min(980, SystemParameters.WorkArea.Width - 20));
        Left = Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
        Top = SystemParameters.WorkArea.Top;
        await LoadStationsAsync();
    }

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
        var center = new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + ActualHeight / 2));
        var bounds = Screen.FromPoint(center).WorkingArea;
        const double snapDistance = 44;
        if (Math.Abs(Left - bounds.Left) <= snapDistance)
            Left = bounds.Left;
        else if (Math.Abs(Left + ActualWidth - bounds.Right) <= snapDistance)
            Left = bounds.Right - ActualWidth;
        if (Math.Abs(Top - bounds.Top) <= snapDistance)
            Top = bounds.Top;
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

        var totalSeconds = _totalListenSeconds.GetValueOrDefault(_currentStation.Id, 0L);
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
        UpdateListeningTimeDisplay();
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
                listenSeconds = _totalListenSeconds.ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.OrdinalIgnoreCase)
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
        var orderedStations = _stations
            .OrderByDescending(station => _favoriteStationIds.Contains(station.Id))
            .ThenBy(station => station.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _isUpdatingStationPicker = true;
        try
        {
            StationPicker.ItemsSource = orderedStations;
            if (_currentStation is not null)
                StationPicker.SelectedItem = orderedStations.FirstOrDefault(station => station.Id == _currentStation.Id);
        }
        finally
        {
            _isUpdatingStationPicker = false;
        }
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        AccumulateListenTime();
        _listenTimer.Stop();
        _metadataTimer.Stop();
        PersistState();

        if (_allowClose)
        {
            _trayIcon.Visible = false;
            _player.Stop();
            _player.Dispose();
            _currentMedia?.Dispose();
            _audioOutput.Dispose();
            _libVlc.Dispose();
            _trayIcon.Dispose();
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