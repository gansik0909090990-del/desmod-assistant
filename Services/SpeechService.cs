using System.Text.Json;
using System.IO;
using DesmodAssistant.Models;
using NAudio.Wave;
using Vosk;

namespace DesmodAssistant.Services;

public sealed record MicrophoneOption(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

public sealed class SpeechService : IDisposable
{
    private AssistantSettings _settings = new();
    private WaveInEvent? _waveIn;
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private bool _running;
    private readonly object _recognizerLock = new();

    public event Action<string, float>? Heard;
    public event Action<string>? PartialHeard;
    public event Action<string>? StatusChanged;
    public event Action<double>? AudioLevelChanged;

    public IReadOnlyList<MicrophoneOption> GetMicrophones()
    {
        var result = new List<MicrophoneOption>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            result.Add(new MicrophoneOption(index, capabilities.ProductName));
        }
        return result;
    }

    public void Configure(AssistantSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        if (_running) return;
        try
        {
            var modelPath = FindModelPath();
            if (modelPath is null)
            {
                StatusChanged?.Invoke("Не найдена офлайн-модель русского языка");
                return;
            }

            Vosk.Vosk.SetLogLevel(-1);
            _model ??= new Model(modelPath);
            _recognizer = new VoskRecognizer(_model, 16000f);
            _recognizer.SetWords(true);

            var microphones = GetMicrophones();
            if (microphones.Count == 0)
            {
                StatusChanged?.Invoke("Windows не видит ни одного микрофона");
                return;
            }

            var selected = microphones.Any(m => m.DeviceNumber == _settings.MicrophoneDeviceNumber)
                ? _settings.MicrophoneDeviceNumber
                : microphones[0].DeviceNumber;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = selected,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 80,
                NumberOfBuffers = 3
            };
            _waveIn.DataAvailable += OnAudioData;
            _waveIn.RecordingStopped += (_, args) =>
            {
                _running = false;
                if (args.Exception is not null) StatusChanged?.Invoke("Ошибка микрофона: " + args.Exception.Message);
            };
            _waveIn.StartRecording();
            _running = true;
            var micName = microphones.First(m => m.DeviceNumber == selected).Name;
            StatusChanged?.Invoke("Слушаю · " + micName);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Микрофон недоступен: " + ex.Message);
            Stop();
        }
    }

    public void Stop()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnAudioData;
            _waveIn.StopRecording();
            _waveIn.Dispose();
            _waveIn = null;
        }
        lock (_recognizerLock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        _running = false;
        StatusChanged?.Invoke("Прослушивание выключено");
    }

    private void OnAudioData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        double peak = 0;
        for (var index = 0; index + 1 < e.BytesRecorded; index += 2)
        {
            var sample = BitConverter.ToInt16(e.Buffer, index) / 32768.0;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        AudioLevelChanged?.Invoke(peak);

        lock (_recognizerLock)
        {
            var recognizer = _recognizer;
            if (recognizer is null) return;
            if (recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                var text = ReadJsonValue(recognizer.Result(), "text");
                if (!string.IsNullOrWhiteSpace(text)) Heard?.Invoke(text, 1f);
            }
            else
            {
                var partial = ReadJsonValue(recognizer.PartialResult(), "partial");
                PartialHeard?.Invoke(partial);
            }
        }
    }

    private static string ReadJsonValue(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private static string? FindModelPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "SpeechModels", "vosk-model-small-ru-0.22"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesmodAssistant", "Models", "vosk-model-small-ru-0.22"),
            Path.Combine(Environment.CurrentDirectory, "SpeechModels", "vosk-model-small-ru-0.22")
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    public void Dispose()
    {
        Stop();
        _model?.Dispose();
    }
}
