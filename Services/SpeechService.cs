using System.Speech.Synthesis;
using System.Text.Json;
using System.IO;
using System.Net.Http;
using System.Text;
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
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly HttpClient _http = new();
    private AssistantSettings _settings = new();
    private WaveInEvent? _waveIn;
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private bool _running;
    private readonly object _recognizerLock = new();
    private WaveOutEvent? _voiceOutput;
    private Mp3FileReader? _voiceReader;
    private MemoryStream? _voiceStream;

    public event Action<string, float>? Heard;
    public event Action<string>? PartialHeard;
    public event Action<string>? StatusChanged;
    public event Action<double>? AudioLevelChanged;

    public IReadOnlyList<string> GetVoices() => _synthesizer.GetInstalledVoices()
        .Where(v => v.Enabled)
        .Select(v => v.VoiceInfo.Name)
        .ToList();

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
        _synthesizer.Volume = Math.Clamp(settings.VoiceVolume, 0, 100);
        _synthesizer.Rate = Math.Clamp(settings.VoiceRate, -10, 10);
        if (!string.IsNullOrWhiteSpace(settings.VoiceName) && GetVoices().Contains(settings.VoiceName))
            _synthesizer.SelectVoice(settings.VoiceName);
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

    public void Speak(string text)
    {
        if (!_settings.SpeakResponses || string.IsNullOrWhiteSpace(text)) return;
        if (_settings.TtsProvider == "Neural" && !string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            _ = SpeakNeuralAsync(text);
            return;
        }
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.SpeakAsync(text);
    }

    private async Task SpeakNeuralAsync(string text)
    {
        try
        {
            var endpoint = _settings.AiEndpoint.Replace("/responses", "/audio/speech", StringComparison.OrdinalIgnoreCase);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.AiApiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "gpt-4o-mini-tts",
                voice = _settings.NeuralVoice,
                input = text,
                format = "mp3"
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var audio = await response.Content.ReadAsByteArrayAsync();
            _voiceOutput?.Stop();
            _voiceOutput?.Dispose();
            _voiceReader?.Dispose();
            _voiceStream?.Dispose();
            _voiceStream = new MemoryStream(audio);
            _voiceReader = new Mp3FileReader(_voiceStream);
            _voiceOutput = new WaveOutEvent { Volume = Math.Clamp(_settings.VoiceVolume / 100f, 0, 1) };
            _voiceOutput.Init(_voiceReader);
            _voiceOutput.Play();
        }
        catch
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.SpeakAsync(text);
        }
    }

    public void PreviewVoice()
    {
        Speak("Привет. Я Desmod Assistant. Этот голос можно изменить в настройках.");
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
        _voiceOutput?.Dispose();
        _voiceReader?.Dispose();
        _voiceStream?.Dispose();
        _http.Dispose();
        _synthesizer.Dispose();
    }
}
