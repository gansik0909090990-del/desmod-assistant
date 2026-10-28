using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesmodAssistant.Models;
using DesmodAssistant.Services;
using Microsoft.Win32;

namespace DesmodAssistant;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly SpeechService _speechService = new();
    private readonly CommandService _commandService = new();
    private readonly SupportService _supportService = new();
    private readonly GoogleAuthService _googleAuthService = new();
    private AssistantSettings _settings;
    private UserSession? _currentUser;
    private DispatcherTimer? _introTimer;
    private int _introIndex;
    private int _signatureIndex;
    private bool _typingSignature;
    private bool _listening;
    private Func<CommandResult>? _pendingConfirmation;
    private string _diagnosticStatus = "";
    private double _diagnosticMaxLevel;
    private string _diagnosticPartial = "";
    private const string IntroText = "DESMOD ASSISTANT";
    private const string SignatureText = "by Desmod.G · создан управлять, а не ждать";

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        Loaded += OnLoaded;
        Closed += (_, _) => _speechService.Dispose();
        _speechService.Heard += OnSpeechHeard;
        _speechService.PartialHeard += text => { _diagnosticPartial = text; OnPartialSpeechHeard(text); };
        _speechService.AudioLevelChanged += level => { _diagnosticMaxLevel = Math.Max(_diagnosticMaxLevel, level); Dispatcher.Invoke(() => MicrophoneLevel.Value = level); };
        _speechService.StatusChanged += status => Dispatcher.Invoke(() =>
        {
            _diagnosticStatus = status;
            TopStatusText.Text = status;
            ListeningTitle.Text = status.StartsWith("Слушаю") ? "Слушаю вас" : status;
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsToUi();
        ApplyTheme(false);
        ApplyBackground();
        if (ScheduleSpeechDiagnosticIfRequested()) return;
        if (SchedulePreviewCaptureIfRequested()) return;
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)));
        StartIntroAnimation();
    }

    private bool ScheduleSpeechDiagnosticIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        var flagIndex = Array.FindIndex(args, value => value.Equals("--test-microphone", StringComparison.OrdinalIgnoreCase));
        if (flagIndex < 0 || flagIndex + 1 >= args.Length) return false;
        var outputPath = args[flagIndex + 1];
        IntroOverlay.Visibility = Visibility.Collapsed;
        RootGrid.Opacity = 1;
        Dispatcher.BeginInvoke(async () =>
        {
            StartListening();
            await Task.Delay(6000);
            var report = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = _diagnosticStatus,
                microphoneCount = _speechService.GetMicrophones().Count,
                maximumAudioLevel = _diagnosticMaxLevel,
                partialText = _diagnosticPartial
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(outputPath, report);
            Close();
        }, DispatcherPriority.ApplicationIdle);
        return true;
    }

    private bool SchedulePreviewCaptureIfRequested()
    {
        var args = Environment.GetCommandLineArgs();
        var flagIndex = Array.FindIndex(args, value => value.Equals("--capture-preview", StringComparison.OrdinalIgnoreCase));
        if (flagIndex < 0 || flagIndex + 1 >= args.Length) return false;
        var outputPath = args[flagIndex + 1];
        var tabIndex = Array.FindIndex(args, value => value.Equals("--preview-tab", StringComparison.OrdinalIgnoreCase));
        if (tabIndex >= 0 && tabIndex + 1 < args.Length)
            MainTabs.SelectedIndex = args[tabIndex + 1].ToLowerInvariant() switch { "commands" => 1, "support" => 2, "settings" => 3, _ => 0 };
        IntroOverlay.Visibility = Visibility.Collapsed;
        RootGrid.Opacity = 1;
        Dispatcher.BeginInvoke(async () =>
        {
            if (args.Any(value => value.Equals("--open-microphone-dropdown", StringComparison.OrdinalIgnoreCase)))
                MicrophoneCombo.IsDropDownOpen = true;
            await Task.Delay(600);
            UpdateLayout();
            var width = Math.Max(1, (int)ActualWidth);
            var height = Math.Max(1, (int)ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(RootGrid);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory);
            using var stream = File.Create(outputPath);
            encoder.Save(stream);
            Close();
        }, DispatcherPriority.ApplicationIdle);
        return true;
    }

    private void StartIntroAnimation()
    {
        _introTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(72) };
        _introTimer.Tick += (_, _) =>
        {
            if (!_typingSignature && _introIndex < IntroText.Length)
            {
                IntroTitle.Text += IntroText[_introIndex++];
                return;
            }

            _typingSignature = true;
            _introTimer.Interval = TimeSpan.FromMilliseconds(38);
            if (_signatureIndex < SignatureText.Length)
            {
                IntroSignature.Text += SignatureText[_signatureIndex++];
                return;
            }

            _introTimer.Stop();
            ContinueButton.IsEnabled = true;
            ContinueButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(550)));
        };
        _introTimer.Start();
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550));
        fade.Completed += (_, _) =>
        {
            IntroOverlay.Visibility = Visibility.Collapsed;
            if (_settings.ListenContinuously) StartListening();
        };
        IntroOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void StartListening()
    {
        _speechService.Configure(_settings);
        _speechService.Start();
        _listening = true;
        AssistantOrb.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void StopListening()
    {
        _speechService.Stop();
        _listening = false;
        AssistantOrb.BeginAnimation(OpacityProperty, null);
        AssistantOrb.Opacity = 1;
    }

    private void OnSpeechHeard(string text, float confidence)
    {
        Dispatcher.Invoke(async () =>
        {
            TranscriptText.Text = $"Распознано: «{text}» · уверенность {confidence:P0}";
            CommandInput.Text = text;
            CommandInput.CaretIndex = CommandInput.Text.Length;
            var wakeWord = _settings.WakeWord.Trim();
            var index = text.IndexOf(wakeWord, StringComparison.OrdinalIgnoreCase);
            var command = index >= 0
                ? text.Remove(index, wakeWord.Length).Trim(' ', ',', '.', '!', '?', ':', '-')
                : text.Trim(' ', ',', '.', '!', '?', ':', '-');
            if (string.IsNullOrWhiteSpace(command))
            {
                ListeningTitle.Text = "Слушаю команду";
                _speechService.Speak("Слушаю");
                return;
            }

            await ExecuteCommandAsync(command);
        });
    }

    private void OnPartialSpeechHeard(string text)
    {
        Dispatcher.Invoke(() =>
        {
            CommandInput.Text = text;
            CommandInput.CaretIndex = CommandInput.Text.Length;
            TranscriptText.Text = string.IsNullOrWhiteSpace(text) ? "Слушаю…" : "Слышу: «" + text + "»";
        });
    }

    private async Task ExecuteCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        ListeningTitle.Text = "Выполняю";
        TranscriptText.Text = "Команда: " + command;
        CommandInput.Text = "";
        var result = await _commandService.ExecuteAsync(command, _settings);

        if (result.RequiresConfirmation && _settings.ConfirmSensitiveActions)
        {
            _pendingConfirmation = result.ConfirmedAction;
            ConfirmMessage.Text = result.Message;
            ConfirmOverlay.Visibility = Visibility.Visible;
            ListeningTitle.Text = "Ожидаю подтверждение";
            return;
        }

        if (result.RequiresConfirmation && result.ConfirmedAction is not null)
            result = result.ConfirmedAction();

        ShowResult(result);
    }

    private void ShowResult(CommandResult result)
    {
        ListeningTitle.Text = result.Success ? "Готово" : "Не получилось";
        TranscriptText.Text = result.Message;
        TopStatusText.Text = result.Success ? "Команда выполнена" : "Нужна помощь";
        _speechService.Speak(result.Message);
    }

    private void ExecuteCommand_OnClick(object sender, RoutedEventArgs e) => _ = ExecuteCommandAsync(CommandInput.Text);

    private void CommandInput_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = ExecuteCommandAsync(CommandInput.Text);
    }

    private void HomeNav_OnClick(object sender, RoutedEventArgs e) => Navigate(0, HomeNav);
    private void CommandsNav_OnClick(object sender, RoutedEventArgs e) => Navigate(1, CommandsNav);
    private void SupportNav_OnClick(object sender, RoutedEventArgs e) { Navigate(2, SupportNav); RefreshTickets(); }
    private void SettingsNav_OnClick(object sender, RoutedEventArgs e) => Navigate(3, SettingsNav);

    private void Navigate(int index, System.Windows.Controls.Button activeButton)
    {
        MainTabs.SelectedIndex = index;
        HomeNav.Tag = CommandsNav.Tag = SupportNav.Tag = SettingsNav.Tag = null;
        activeButton.Tag = "Active";
    }

    private void LoadSettingsToUi()
    {
        WakeWordInput.Text = _settings.WakeWord;
        WakeWordPreview.Text = $"«{_settings.WakeWord}»";
        ContinuousListeningCheck.IsChecked = _settings.ListenContinuously;
        SpeakResponsesCheck.IsChecked = _settings.SpeakResponses;
        ConfirmSensitiveCheck.IsChecked = _settings.ConfirmSensitiveActions;
        LaunchAtStartupCheck.IsChecked = _settings.LaunchAtStartup;
        VolumeSlider.Value = _settings.VoiceVolume;
        RateSlider.Value = _settings.VoiceRate;
        BackgroundPathInput.Text = _settings.BackgroundPath;
        EndpointInput.Text = _settings.AiEndpoint;
        ModelInput.Text = _settings.AiModel;
        ApiKeyInput.Password = _settings.AiApiKey;
        GoogleClientIdInput.Text = _settings.GoogleClientId;
        SupportBackendInput.Text = _settings.SupportBackendUrl;
        VoiceCombo.ItemsSource = _speechService.GetVoices();
        VoiceCombo.SelectedItem = string.IsNullOrWhiteSpace(_settings.VoiceName) ? VoiceCombo.Items.Cast<object>().FirstOrDefault() : _settings.VoiceName;
        TtsProviderCombo.SelectedIndex = _settings.TtsProvider == "Neural" ? 1 : 0;
        NeuralVoiceCombo.SelectedIndex = _settings.NeuralVoice switch { "nova" => 1, "shimmer" => 2, "onyx" => 3, _ => 0 };
        ThemeCombo.SelectedIndex = _settings.Theme == "Light" ? 1 : 0;
        var microphones = _speechService.GetMicrophones();
        MicrophoneCombo.ItemsSource = microphones;
        MicrophoneCombo.SelectedItem = microphones.FirstOrDefault(m => m.DeviceNumber == _settings.MicrophoneDeviceNumber) ?? microphones.FirstOrDefault();
        _speechService.Configure(_settings);
    }

    private void SaveSettings_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.WakeWord = string.IsNullOrWhiteSpace(WakeWordInput.Text) ? "помощник" : WakeWordInput.Text.Trim().ToLowerInvariant();
        _settings.ListenContinuously = ContinuousListeningCheck.IsChecked == true;
        _settings.SpeakResponses = SpeakResponsesCheck.IsChecked == true;
        _settings.ConfirmSensitiveActions = ConfirmSensitiveCheck.IsChecked == true;
        _settings.LaunchAtStartup = LaunchAtStartupCheck.IsChecked == true;
        _settings.VoiceName = VoiceCombo.SelectedItem?.ToString() ?? "";
        _settings.TtsProvider = (TtsProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Windows";
        _settings.NeuralVoice = (NeuralVoiceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "alloy";
        _settings.MicrophoneDeviceNumber = (MicrophoneCombo.SelectedItem as MicrophoneOption)?.DeviceNumber ?? 0;
        _settings.VoiceVolume = (int)VolumeSlider.Value;
        _settings.VoiceRate = (int)RateSlider.Value;
        _settings.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Dark";
        _settings.BackgroundPath = BackgroundPathInput.Text;
        _settings.AiEndpoint = EndpointInput.Text.Trim();
        _settings.AiModel = ModelInput.Text.Trim();
        _settings.AiApiKey = ApiKeyInput.Password;
        _settings.GoogleClientId = GoogleClientIdInput.Text.Trim();
        _settings.SupportBackendUrl = SupportBackendInput.Text.Trim();
        _settingsService.Save(_settings);
        ConfigureStartup();
        WakeWordPreview.Text = $"«{_settings.WakeWord}»";
        _speechService.Configure(_settings);
        ApplyTheme(true);
        ApplyBackground();
        if (_settings.ListenContinuously && !_listening) StartListening();
        if (!_settings.ListenContinuously && _listening) StopListening();
        TopStatusText.Text = "Настройки сохранены";
    }

    private void ConfigureStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (_settings.LaunchAtStartup)
                key?.SetValue("DesmodAssistant", $"\"{Environment.ProcessPath}\"");
            else
                key?.DeleteValue("DesmodAssistant", false);
        }
        catch (Exception ex)
        {
            TopStatusText.Text = "Автозапуск не сохранён: " + ex.Message;
        }
    }

    private void ApplyTheme(bool animate)
    {
        void Apply()
        {
            var light = _settings.Theme == "Light";
            Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#EEF2F8" : "#0A0D16"));
            ThemeOverlay.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#D9E8EEF7" : "#E80A0D16"));
            System.Windows.Application.Current.Resources["TextBrush"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#172033" : "#F5F7FF"));
            System.Windows.Application.Current.Resources["MutedBrush"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#526078" : "#AAB3C5"));
            System.Windows.Application.Current.Resources["PanelBrush"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(light ? "#E8FFFFFF" : "#D9121724"));
        }

        if (!animate) { Apply(); return; }
        var fadeOut = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(220));
        fadeOut.Completed += (_, _) =>
        {
            Apply();
            ThemeOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(420)));
        };
        ThemeOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void ChooseBackground_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Фоны|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.wmv|Все файлы|*.*"
        };
        if (dialog.ShowDialog() == true) BackgroundPathInput.Text = dialog.FileName;
    }

    private void TestMicrophone_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.MicrophoneDeviceNumber = (MicrophoneCombo.SelectedItem as MicrophoneOption)?.DeviceNumber ?? 0;
        _speechService.Configure(_settings);
        if (_listening) StopListening();
        StartListening();
        TranscriptText.Text = "Говорите — текст должен появляться в строке ввода";
        Navigate(0, HomeNav);
    }

    private void PreviewVoice_OnClick(object sender, RoutedEventArgs e)
    {
        _settings.VoiceName = VoiceCombo.SelectedItem?.ToString() ?? "";
        _settings.TtsProvider = (TtsProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Windows";
        _settings.NeuralVoice = (NeuralVoiceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "alloy";
        _settings.VoiceVolume = (int)VolumeSlider.Value;
        _settings.VoiceRate = (int)RateSlider.Value;
        _speechService.Configure(_settings);
        _speechService.PreviewVoice();
    }

    private async void GoogleLogin_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.GoogleClientId))
            {
                AccountTitle.Text = "Требуется настройка Google";
                AccountDetails.Text = "Открыл страницу создания OAuth Client ID. После создания вставьте его в настройках приложения.";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
                Navigate(3, SettingsNav);
                GoogleClientIdInput.Focus();
                return;
            }
            GoogleLoginButton.IsEnabled = false;
            AccountTitle.Text = "Ожидаю вход Google…";
            _currentUser = await _googleAuthService.SignInAsync(_settings.GoogleClientId, _supportService.ResolveRole);
            AccountTitle.Text = _currentUser.Name;
            AccountDetails.Text = $"{_currentUser.Email} · роль: {_currentUser.Role}";
            GoogleLoginButton.Content = "Сменить аккаунт";
            AdminPanel.Visibility = _currentUser.CanModerate ? Visibility.Visible : Visibility.Collapsed;
            RefreshTickets();
        }
        catch (Exception ex)
        {
            AccountTitle.Text = "Вход не выполнен";
            AccountDetails.Text = ex.Message;
        }
        finally { GoogleLoginButton.IsEnabled = true; }
    }

    private void SubmitTicket_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentUser is null) { AccountDetails.Text = "Сначала войдите через Google"; return; }
        if (string.IsNullOrWhiteSpace(TicketSubjectInput.Text) || string.IsNullOrWhiteSpace(TicketDescriptionInput.Text))
        { AccountDetails.Text = "Заполните тему и описание проблемы"; return; }
        _supportService.CreateTicket(_currentUser, TicketSubjectInput.Text, TicketDescriptionInput.Text);
        TicketSubjectInput.Text = TicketDescriptionInput.Text = "";
        AccountDetails.Text = "Обращение создано";
        RefreshTickets();
    }

    private void GrantModerator_OnClick(object sender, RoutedEventArgs e) => GrantRole("moderator");
    private void GrantAdmin_OnClick(object sender, RoutedEventArgs e) => GrantRole("admin");

    private void GrantRole(string role)
    {
        try
        {
            if (_currentUser is null) throw new InvalidOperationException("Сначала войдите");
            _supportService.GrantRole(_currentUser, RoleEmailInput.Text, role);
            AccountDetails.Text = role == "admin" ? "Роль администратора выдана" : "Роль модератора выдана";
            RoleEmailInput.Text = "";
        }
        catch (Exception ex) { AccountDetails.Text = ex.Message; }
    }

    private void ReplyTicket_OnClick(object sender, RoutedEventArgs e) => UpdateSelectedTicket("В работе");
    private void CloseTicket_OnClick(object sender, RoutedEventArgs e) => UpdateSelectedTicket("Закрыто");

    private void UpdateSelectedTicket(string status)
    {
        try
        {
            if (_currentUser is null) throw new InvalidOperationException("Сначала войдите");
            if (TicketsList.SelectedItem is not SupportTicket ticket) throw new InvalidOperationException("Выберите обращение в списке");
            _supportService.UpdateTicket(_currentUser, ticket.Id, status, ModeratorReplyInput.Text);
            ModeratorReplyInput.Text = "";
            RefreshTickets();
        }
        catch (Exception ex) { AccountDetails.Text = ex.Message; }
    }

    private void RefreshTickets()
    {
        if (_currentUser is null) { TicketsList.ItemsSource = null; return; }
        TicketsList.ItemsSource = _supportService.GetTickets(_currentUser);
    }

    private void ApplyBackground()
    {
        BackgroundImage.Source = null;
        BackgroundVideo.Stop();
        BackgroundVideo.Source = null;
        var path = _settings.BackgroundPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".mp4" or ".wmv")
        {
            BackgroundVideo.Source = new Uri(path);
            BackgroundVideo.Play();
        }
        else
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            BackgroundImage.Source = image;
        }
    }

    private void BackgroundVideo_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        BackgroundVideo.Position = TimeSpan.Zero;
        BackgroundVideo.Play();
    }

    private void CancelConfirm_OnClick(object sender, RoutedEventArgs e)
    {
        _pendingConfirmation = null;
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        ShowResult(new CommandResult(false, "Действие отменено"));
    }

    private void AcceptConfirm_OnClick(object sender, RoutedEventArgs e)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var action = _pendingConfirmation;
        _pendingConfirmation = null;
        if (action is not null) ShowResult(action());
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}
