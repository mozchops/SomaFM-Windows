using LibVLCSharp.Shared;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SomaFM.Windows;

public sealed class AudioVisualizerOutput : IDisposable
{
    private const int ChannelCount = 2;
    private const int SampleCount = 1024;
    private const int BandCount = 12;
    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _output;
    private readonly float[] _analysisSamples = new float[SampleCount];
    private readonly double[] _window = new double[SampleCount];
    private readonly double[] _smoothedLevels = new double[BandCount];
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCallback;
    private int _writeIndex;
    private int _samplesFilled;
    private long _lastPublishTicks;
    private bool _disposed;

    public event Action<double[]>? LevelsChanged;

    public MediaPlayer.LibVLCAudioPlayCb PlayCallback => _playCallback;
    public MediaPlayer.LibVLCAudioPauseCb PauseCallback => _pauseCallback;
    public MediaPlayer.LibVLCAudioResumeCb ResumeCallback => _resumeCallback;
    public MediaPlayer.LibVLCAudioFlushCb FlushCallback => _flushCallback;
    public MediaPlayer.LibVLCAudioDrainCb DrainCallback => _drainCallback;

    public AudioVisualizerOutput()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(44_100, 16, ChannelCount))
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        _output = new WaveOutEvent();
        _output.Init(_buffer);
        _output.Play();
        _playCallback = PlayAudio;
        _pauseCallback = PauseAudio;
        _resumeCallback = ResumeAudio;
        _flushCallback = FlushAudio;
        _drainCallback = DrainAudio;
        for (var index = 0; index < SampleCount; index++)
            _window[index] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (SampleCount - 1));
    }

    private void PlayAudio(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_disposed || samples == IntPtr.Zero || count == 0)
            return;
        try
        {
            var sampleLength = checked((int)count * ChannelCount);
            var interleaved = new short[sampleLength];
            Marshal.Copy(samples, interleaved, 0, sampleLength);
            var bytes = new byte[sampleLength * sizeof(short)];
            Buffer.BlockCopy(interleaved, 0, bytes, 0, bytes.Length);
            _buffer.AddSamples(bytes, 0, bytes.Length);
            Analyze(interleaved, (int)count);
        }
        catch
        {
        }
    }

    private void Analyze(short[] samples, int frameCount)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            var mono = (samples[frame * ChannelCount] + samples[frame * ChannelCount + 1]) / 65_536.0;
            _analysisSamples[_writeIndex] = (float)mono;
            _writeIndex = (_writeIndex + 1) % SampleCount;
            _samplesFilled = Math.Min(SampleCount, _samplesFilled + 1);
        }
        if (_samplesFilled < SampleCount)
            return;

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
                var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / 44_100);
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

        var ticks = Environment.TickCount64;
        if (ticks - _lastPublishTicks < 40)
            return;
        _lastPublishTicks = ticks;
        LevelsChanged?.Invoke(levels);
    }

    private void PauseAudio(IntPtr data, long pts) => _output.Pause();

    private void ResumeAudio(IntPtr data, long pts) => _output.Play();

    private void FlushAudio(IntPtr data, long pts)
    {
        _buffer.ClearBuffer();
        _output.Play();
    }

    private void DrainAudio(IntPtr data)
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _output.Stop();
        _output.Dispose();
    }
}