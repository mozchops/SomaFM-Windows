using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SomaFM.Windows;

public sealed class StreamPlayer : IDisposable
{
    private const int SampleRate = 44_100;
    private const int ChannelCount = 2;
    private const int SampleCount = 1024;
    private const int BandCount = 24;
    private static readonly WaveFormat TargetFormat = new(SampleRate, 16, ChannelCount);

    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _output;
    private readonly float[] _analysisSamples = new float[SampleCount];
    private readonly double[] _window = new double[SampleCount];
    private readonly double[] _smoothedLevels = new double[BandCount];
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private MediaFoundationReader? _activeReader;
    private int _writeIndex;
    private int _samplesFilled;
    private long _lastPublishTicks;
    private bool _disposed;

    public event Action<double[]>? LevelsChanged;
    public event Action<bool>? PlayingChanged;
    public event Action<string>? StatusChanged;

    public StreamPlayer()
    {
        _buffer = new BufferedWaveProvider(TargetFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(8),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.Init(new AnalyzingProvider(_buffer, Analyze));
        _output.Play();
        for (var index = 0; index < SampleCount; index++)
            _window[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (SampleCount - 1));
    }

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0f, 1f);
    }

    public void Play(string url)
    {
        if (_disposed)
            return;
        StopInternal();
        var source = new CancellationTokenSource();
        lock (_sync)
            _cancellation = source;
        _ = Task.Run(() => ReadLoop(url, source.Token));
    }

    public void Stop()
    {
        StopInternal();
        PlayingChanged?.Invoke(false);
    }

    private void StopInternal()
    {
        CancellationTokenSource? source;
        MediaFoundationReader? reader;
        lock (_sync)
        {
            source = _cancellation;
            reader = _activeReader;
            _cancellation = null;
            _activeReader = null;
        }

        source?.Cancel();
        try
        {
            reader?.Dispose();
        }
        catch
        {
        }
        _buffer.ClearBuffer();
    }

    private void ReadLoop(string url, CancellationToken token)
    {
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            Report(attempt == 0 ? "Connecting…" : "Reconnecting…", token);
            try
            {
                using var reader = new MediaFoundationReader(url);
                lock (_sync)
                {
                    if (token.IsCancellationRequested)
                        return;
                    _activeReader = reader;
                }

                IWaveProvider source = reader;
                MediaFoundationResampler? resampler = null;
                if (!reader.WaveFormat.Equals(TargetFormat))
                {
                    resampler = new MediaFoundationResampler(reader, TargetFormat) { ResamplerQuality = 60 };
                    source = resampler;
                }

                try
                {
                    var chunk = new byte[TargetFormat.AverageBytesPerSecond / 5];
                    var started = false;
                    while (!token.IsCancellationRequested)
                    {
                        while (_buffer.BufferedDuration > TimeSpan.FromSeconds(3) && !token.IsCancellationRequested)
                            Thread.Sleep(50);

                        var read = source.Read(chunk, 0, chunk.Length);
                        if (read == 0 || token.IsCancellationRequested)
                            break;
                        _buffer.AddSamples(chunk, 0, read);
                        if (!started)
                        {
                            started = true;
                            attempt = 0;
                            if (!token.IsCancellationRequested)
                                PlayingChanged?.Invoke(true);
                        }
                    }
                }
                finally
                {
                    resampler?.Dispose();
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeReader, reader))
                            _activeReader = null;
                    }
                }
            }
            catch when (token.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }

            if (token.IsCancellationRequested)
                return;

            PlayingChanged?.Invoke(false);
            attempt++;
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(2 * attempt, 10)));
        }
    }

    private void Report(string status, CancellationToken token)
    {
        if (!token.IsCancellationRequested)
            StatusChanged?.Invoke(status);
    }

    private void Analyze(byte[] data, int offset, int count)
    {
        var samples = MemoryMarshal.Cast<byte, short>(data.AsSpan(offset, count - count % (ChannelCount * sizeof(short))));
        var frameCount = samples.Length / ChannelCount;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var mono = (samples[frame * ChannelCount] + samples[frame * ChannelCount + 1]) / 65_536.0;
            _analysisSamples[_writeIndex] = (float)mono;
            _writeIndex = (_writeIndex + 1) % SampleCount;
            _samplesFilled = Math.Min(SampleCount, _samplesFilled + 1);
        }
        if (_samplesFilled < SampleCount)
            return;

        var ticks = Environment.TickCount64;
        if (ticks - _lastPublishTicks < 40)
            return;
        _lastPublishTicks = ticks;

        var levels = new double[BandCount];
        const double minFrequency = 55;
        const double maxFrequency = 16_000;
        var bandRatio = Math.Pow(maxFrequency / minFrequency, 1.0 / BandCount);
        for (var band = 0; band < BandCount; band++)
        {
            var lower = minFrequency * Math.Pow(bandRatio, band);
            var upper = lower * bandRatio;
            var peak = 0.0;
            for (var probe = 0; probe < 3; probe++)
            {
                var frequency = lower * Math.Pow(upper / lower, (probe + 0.5) / 3.0);
                var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
                var previous = 0.0;
                var previousPrevious = 0.0;
                for (var index = 0; index < SampleCount; index++)
                {
                    var sample = _analysisSamples[(_writeIndex + index) % SampleCount] * _window[index];
                    var current = sample + coefficient * previous - previousPrevious;
                    previousPrevious = previous;
                    previous = current;
                }
                var power = Math.Max(0, previous * previous + previousPrevious * previousPrevious
                    - coefficient * previous * previousPrevious);
                peak = Math.Max(peak, 2 * Math.Sqrt(power) / SampleCount);
            }
            var decibels = 20 * Math.Log10(Math.Max(peak, 0.000_001));
            var level = Math.Clamp((decibels + 58) / 58, 0, 1);
            _smoothedLevels[band] = Math.Max(level, _smoothedLevels[band] * 0.76);
            levels[band] = _smoothedLevels[band];
        }

        LevelsChanged?.Invoke(levels);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopInternal();
        _output.Stop();
        _output.Dispose();
    }

    private sealed class AnalyzingProvider(IWaveProvider inner, Action<byte[], int, int> analyze) : IWaveProvider
    {
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
                analyze(buffer, offset, read);
            return read;
        }
    }
}
