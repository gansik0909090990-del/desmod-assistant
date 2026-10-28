namespace DesmodAssistant.Models;

public sealed class AssistantSettings
{
    public string WakeWord { get; set; } = "помощник";
    public int MicrophoneDeviceNumber { get; set; } = 0;
    public bool ListenContinuously { get; set; } = true;
    public bool SpeakResponses { get; set; } = true;
    public string VoiceName { get; set; } = "";
    public int VoiceVolume { get; set; } = 85;
    public int VoiceRate { get; set; } = 0;
    public string TtsProvider { get; set; } = "Windows";
    public string NeuralVoice { get; set; } = "alloy";
    public string Theme { get; set; } = "Dark";
    public string BackgroundPath { get; set; } = "";
    public bool ConfirmSensitiveActions { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public string AiEndpoint { get; set; } = "https://api.openai.com/v1/responses";
    public string AiModel { get; set; } = "gpt-4.1-mini";
    public string AiApiKey { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    public string SupportBackendUrl { get; set; } = "";
}
