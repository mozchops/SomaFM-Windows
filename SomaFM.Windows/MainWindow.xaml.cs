using LibVLCSharp.Shared;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;

namespace SomaFM.Windows;

public partial class MainWindow : Window
{
    private readonly SomaFmClient _client = new();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _currentMedia;
    private readonly DispatcherTimer _metadataTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly NotifyIcon _trayIcon;
    private IReadOnlyList<Station> _stations = [];
    private Station? _currentStation;
    private bool _isPlaying;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Core.Initialize();
        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
        _player.Volume = (int)VolumeSlider.Value;
        _player.Playing += (_, _) => Dispatcher.Invoke(() => SetPlaying(true));
        _player.Paused += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _player.Stopped += (_, _) => Dispatcher.Invoke(() => SetPlaying(false));
        _metadataTimer.Tick += async (_, _) => await RefreshTrackAsync();
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
        Loaded += async (_, _) => await LoadStationsAsync();
    }

    private async Task LoadStationsAsync()
    {
        StatusText.Text = "Loading SomaFM stations…";
        MessageText.Text = "";
        try
        {
            _stations = await _client.GetStationsAsync();
            UpdateStationList();
            StatusText.Text = $"{_stations.Count} stations";
            if (_stations.Count == 0)
                MessageText.Text = "No stations are available right now.";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            StatusText.Text = "Station catalog unavailable";
            MessageText.Text = error.Message;
        }
    }

    private void UpdateStationList()
    {
        var query = SearchBox.Text.Trim();
        StationList.ItemsSource = _stations.Where(station =>
            query.Length == 0
            || station.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || station.Genre.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || station.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    private void StationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationList.SelectedItem is Station station)
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
            NowPlayingStation.Text = station.Title;
            NowPlayingTrack.Text = "Connecting…";
            _trayIcon.Text = $"SomaFM - {station.Title}";
            _currentMedia = new Media(_libVlc, new Uri(station.StreamUrl));
            _player.Play(_currentMedia);
            _metadataTimer.Start();
            _ = RefreshTrackAsync();
            MessageText.Text = "";
        }
        catch (Exception error)
        {
            MessageText.Text = $"Could not start playback: {error.Message}";
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
        _isPlaying = playing;
        PlayButton.Content = playing ? "Pause" : "Play";
        PlayButton.IsEnabled = _currentStation is not null;
        StopButton.IsEnabled = _currentStation is not null;
    }

    private void TogglePlayback()
    {
        if (_currentStation is null)
            return;
        if (_currentMedia is null)
            StartStation(_currentStation);
        else if (_isPlaying)
            _player.Pause();
        else
            _player.Play();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _metadataTimer.Stop();
        _player.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
        NowPlayingTrack.Text = "Stopped";
        SetPlaying(false);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadStationsAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStationList();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
            _player.Volume = (int)e.NewValue;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _trayIcon.Visible = false;
            _metadataTimer.Stop();
            _player.Stop();
            _player.Dispose();
            _currentMedia?.Dispose();
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