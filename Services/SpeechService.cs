using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using DesmodAssistant.Models;

namespace DesmodAssistant.Services;

public sealed class SpeechService : IDisposable
{
    private SpeechRecognitionEngine? _recognizer;
    private readonly SpeechSynthesizer _synthesizer = new();
    private AssistantSettings _settings = new();
    private bool _running;

    public event Action<string, float>? Heard;
    public event Action<string>? StatusChanged;

    public IReadOnlyList<string> GetVoices() => _synthesizer.GetInstalledVoices()
        .Where(v => v.Enabled)
        .Select(v => v.VoiceInfo.Name)
        .ToList();

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
            var recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.Name.Equals("ru-RU", StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault();

            if (recognizer is null)
            {
                StatusChanged?.Invoke("В Windows не установлен языковой пакет распознавания речи");
                return;
            }

            _recognizer = new SpeechRecognitionEngine(recognizer);
            _recognizer.SetInputToDefaultAudioDevice();
            _recognizer.LoadGrammar(new DictationGrammar());
            _recognizer.SpeechRecognized += OnSpeechRecognized;
            _recognizer.RecognizeCompleted += (_, _) => _running = false;
            _recognizer.RecognizeAsync(RecognizeMode.Multiple);
            _running = true;
            StatusChanged?.Invoke($"Слушаю · {recognizer.Culture.DisplayName}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Микрофон недоступен: " + ex.Message);
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _recognizer?.RecognizeAsyncCancel();
        _running = false;
        StatusChanged?.Invoke("Прослушивание выключено");
    }

    public void Speak(string text)
    {
        if (!_settings.SpeakResponses || string.IsNullOrWhiteSpace(text)) return;
        _synthesizer.SpeakAsyncCancelAll();
        _synthesizer.SpeakAsync(text);
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (e.Result.Confidence < 0.35f) return;
        Heard?.Invoke(e.Result.Text, e.Result.Confidence);
    }

    public void Dispose()
    {
        Stop();
        _recognizer?.Dispose();
        _synthesizer.Dispose();
    }
}
