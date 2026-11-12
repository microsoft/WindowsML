// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.ObjectModel;
using System.Text;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TaskStudio.Runtime;
using Windows.System;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TaskStudio;

public sealed partial class MainWindow : Window
{
    private readonly TaskRuntime runtime = new();
    private CancellationTokenSource? generation;
    public ObservableCollection<StudioMessage> Messages { get; } = new();
    private bool loaded;
    private bool busy;
    private bool closing;
    private int generationId;
    private bool hasError;
    private GenerationDefaults generationDefaults = GenerationDefaults.TaskDefaults;
    private bool IsText => TaskMode.SelectedIndex is 0 or 1;

    public MainWindow()
    {
        InitializeComponent();
        InitializeDecision();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StudioTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 230, 237, 243);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 127, 140, 156);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 40, 50, 62);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 920));
        AppWindow.Closing += OnClosing;
        Shell.SizeChanged += (_, args) =>
        {
            SidebarColumn.Width = new GridLength(args.NewSize.Width < 1100 ? 276 : 308);
            WelcomeTitle.FontSize = args.NewSize.Width < 1100 ? 26 : 32;
            bool compact = args.NewSize.Height < 800;
            WelcomeIcon.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            WelcomeEyebrow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            WelcomeHintRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            WelcomeStack.Spacing = compact ? 14 : 22;
            WelcomePanel.Padding = compact ? new Thickness(24, 16, 24, 16) : new Thickness(36, 32, 36, 32);
        };
        Shell.Loaded += async (_, _) => await InitializeGpuAsync();
    }

    private async Task<string?> PickFileAsync(string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }

    // A model folder typed into the path box gets the tokenizer that Browse would choose.
    private static void UseFolderTokenizerIfEmpty(TextBox modelPath, TextBox tokenizerPath)
    {
        string folder = modelPath.Text.Trim();
        if (tokenizerPath.Text.Trim().Length > 0 || !System.IO.Directory.Exists(folder)) return;
        string tokenizer = System.IO.Path.Combine(folder, "tokenizer_config.json");
        if (System.IO.File.Exists(tokenizer)) tokenizerPath.Text = tokenizer;
    }

    private async void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            string? path = await PickFolderAsync();
            if (path is null) return;
            ModelPath.Text = path;
            string tokenizer = System.IO.Path.Combine(path, "tokenizer_config.json");
            if (System.IO.File.Exists(tokenizer)) TokenizerPath.Text = tokenizer;
            await LoadGenerationDefaultsAsync(path);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void BrowseGguf_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            string? path = await PickFileAsync(".gguf");
            if (path is null) return;
            ModelPath.Text = path;
            // The GGUF carries its own tokenizer; leave this empty unless overriding it.
            TokenizerPath.Text = "";
            await LoadGenerationDefaultsAsync(path);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void BrowseTokenizer_Click(object sender, RoutedEventArgs e)
    {
        try { TokenizerPath.Text = await PickFileAsync(".json") ?? TokenizerPath.Text; }
        catch (Exception error) { ShowError(error); }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        SessionStatus.Text = "Preparing your model...";
        try
        {
            // Null keeps the model's default sequence length.
            uint? contextTokens = (ContextLength.SelectedItem as ComboBoxItem)?.Tag is string tag ? uint.Parse(tag, CultureInfo.InvariantCulture) : null;
            UseFolderTokenizerIfEmpty(ModelPath, TokenizerPath);
            await LoadGenerationDefaultsAsync(ModelPath.Text.Trim());
            var loadTimer = System.Diagnostics.Stopwatch.StartNew();
            var speculation = SelectedSpeculation();
            var device = await runtime.LoadAsync(ModelPath.Text.Trim(), TokenizerPath.Text.Trim(), contextTokens, RequireGpu(), speculation);
            lastLoadTime = loadTimer.Elapsed;
            loaded = true;
            ShowActiveDevice(device);
            bool gguf = TaskRuntime.IsGgufFile(ModelPath.Text.Trim());
            LoadedModelName.Text = System.IO.Path.GetFileName(ModelPath.Text.Trim().TrimEnd('\\'));
            ClearOutput();
            string context = contextTokens is uint tokens ? $"{tokens:N0}-token context" : "model default context";
            loadedContext = contextTokens is uint size ? $"{size:N0} tokens" : "Model default";
            UpdatePerfSession();
            ShowSpeculativeSupport();
            ShowStatus("Task sessions ready", $"CGC / {device.Name}{(gguf ? " / GGUF" : "")} / {context} / " +
                (speculation.Mode == SpeculativeMode.Off ? "" : $"speculative decoding: {TaskRuntime.Describe(speculation.Mode)} / ") +
                (runtime.TextProgramCacheUsed ? "compiled programs reused across runs (weights from model files)" : "compiled per session") +
                ". Chat uses the model's structured conversation formatter.", InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void Unload_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await runtime.UnloadAsync();
            loaded = false;
            ClearOutput();
            UpdatePerfSession();
            ShowSpeculativeSupport();
            ShowStatus("Sessions released", "Select a model to start again.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !IsText || !loaded || string.IsNullOrWhiteSpace(Prompt.Text)) return;
        using var cancellation = new CancellationTokenSource();
        generation = cancellation;
        int currentGeneration = ++generationId;
        SetBusy(true);
        StopButton.IsEnabled = true;
        StopButton.Visibility = Visibility.Visible;
        SessionStatus.Text = "Generating on your device...";
        StudioMessage? response = null;
        try
        {
            var settings = ReadGenerationSettings();
            uint[] stops = ReadStopTokenIds();
            bool chat = TaskMode.SelectedIndex == 0;
            string prompt = Prompt.Text;
            if (!chat) Messages.Clear();
            Messages.Add(new StudioMessage(true, prompt));
            response = new StudioMessage(false, "Thinking...") { Detail = $"CGC / {RequireGpu().Name}" };
            Messages.Add(response);
            WelcomeScroll.Visibility = Visibility.Collapsed;
            OutputScroll.Visibility = Visibility.Visible;
            var answer = new StringBuilder();
            // Progress posts to the UI context; native sessions never leave their worker thread.
            var progress = new Progress<GenerationUpdate>(update =>
            {
                if (update.IsReasoning || currentGeneration != generationId) return;
                answer.Append(update.Text);
                response.Text = answer.ToString();
                ScrollToLatest();
            });
            var result = await runtime.GenerateAsync(chat, prompt, SystemPrompt.Text,
                settings, stops, progress, cancellation.Token);
            generationId++;
            response.Text = string.IsNullOrEmpty(result.Text)
                ? (result.Canceled ? "Generation stopped." : "The model returned no text.") : result.Text;
            response.Detail = result.Canceled
                ? "Stopped / This turn was not saved to model history"
                : $"{result.PromptTokens:N0} prompt tokens  /  {result.Elapsed.TotalSeconds:F2}s  /  {FriendlyFinishReason(result.FinishReason)}" +
                  (result.Timing is { DecodeTokensPerSecond: double rate } ? $"  /  {rate:F1} tok/s" : "") +
                  (result.Speculation is { Active: true } speculative ? $"  /  {speculative.Describe()}" : "");
            if (!result.Canceled) RecordPerf(chat ? "Chat" : "Completion", result);
            ResultDetails.Text = "Generated answers can be inaccurate. Review important information.";
            ScrollToLatest();
            if (chat && !result.Canceled) Prompt.Text = "";
            ShowStatus(result.Canceled ? "Generation stopped" : "Generation complete",
                result.Canceled ? "The interrupted turn was not saved to chat history." : "Output came from the Runtime Task API.",
                InfoBarSeverity.Informational);
        }
        catch (Exception error)
        {
            if (response is not null)
            {
                if (response.Text == "Thinking...") response.Text = "The response could not be generated.";
                response.Detail = "Failed / This turn was not saved to model history";
            }
            ShowError(error);
        }
        finally
        {
            generationId++;
            generation = null;
            if (!closing) SetBusy(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => generation?.Cancel();

    private GenerationSettings ReadGenerationSettings()
    {
        static uint Whole(Slider input, string name)
        {
            double value = input.Value;
            if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 0 || value > uint.MaxValue)
                throw new ArgumentException($"{name} must be a whole number.");
            return checked((uint)value);
        }
        if (!ulong.TryParse(SamplingSeed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
            throw new ArgumentException("Seed must be an unsigned 64-bit integer (0 through 18446744073709551615).");
        var settings = new GenerationSettings(Whole(MaxTokens, "Maximum new tokens"), (float)Temperature.Value,
            Whole(TopK, "Top-k"), (float)TopP.Value, (float)MinP.Value, (float)RepetitionPenalty.Value,
            Whole(RepetitionWindow, "Repetition window"), (float)PresencePenalty.Value, (float)FrequencyPenalty.Value, seed);
        settings.Validate();
        return settings;
    }

    private uint[] ReadStopTokenIds()
    {
        return StopTokenIds.Text.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
                ? id : throw new ArgumentException($"Invalid stop-token ID '{value}'. Use unsigned 32-bit integers."))
            .Distinct().ToArray();
    }

    private void ResetGeneration_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ApplyGenerationDefaults();
    }

    private string FormatSampling(double value) => value.ToString("0.######", CultureInfo.CurrentCulture);

    private async Task LoadGenerationDefaultsAsync(string path)
    {
        GenerationDefaultsSource.Text = "Reading model generation defaults...";
        try
        {
            generationDefaults = await Task.Run(() => GenerationDefaults.Load(path));
            ApplyGenerationDefaults();
        }
        catch
        {
            GenerationDefaultsSource.Text = "Model generation defaults could not be loaded.";
            throw;
        }
    }

    private void ApplyGenerationDefaults()
    {
        static void Set(Slider slider, double value, double minimum, double maximum)
        {
            slider.Minimum = Math.Min(minimum, value);
            slider.Maximum = Math.Max(maximum, value);
            slider.Value = value;
        }
        var settings = generationDefaults.Settings;
        Set(MaxTokens, settings.MaxTokens, 1, GenerationSettings.MaxNewTokensLimit);
        Set(Temperature, settings.Temperature, 0, 2);
        Set(TopK, settings.TopK, 0, 200);
        Set(TopP, settings.TopP, 0.01, 1);
        Set(MinP, settings.MinP, 0, 1);
        Set(RepetitionPenalty, settings.RepetitionPenalty, 0.01, 2);
        Set(RepetitionWindow, settings.RepetitionWindow, 0, 2048);
        Set(PresencePenalty, settings.PresencePenalty, -2, 2);
        Set(FrequencyPenalty, settings.FrequencyPenalty, -2, 2);
        SamplingSeed.Text = settings.Seed.ToString(CultureInfo.InvariantCulture);
        StopTokenIds.Text = string.Join(", ", generationDefaults.StopTokenIds);
        GenerationDefaultsSource.Text = generationDefaults.Source;
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (IsPerf)
        {
            ClearPerf();
            UpdatePerfControls();
            ShowStatus("Perf history cleared", "", InfoBarSeverity.Informational);
            return;
        }
        if (IsDecision)
        {
            DecisionResults.Clear();
            DecisionResultsPanel.Visibility = Visibility.Collapsed;
            ShowStatus(decisionLoaded ? "Decision session ready" : "No decision model connected", "", InfoBarSeverity.Informational);
            return;
        }
        if (IsSpeech)
        {
            SpeechTranscript.Text = "";
            audioClip = null;
            AudioClipLabel.Text = "No audio selected";
            UpdateSpeechControls();
            ShowStatus(speechLoaded ? "Speech session ready" : "Capture ready", "", InfoBarSeverity.Informational);
            return;
        }
        SetBusy(true);
        try
        {
            await runtime.ResetAsync();
            ClearOutput();
            ShowStatus(loaded ? "Ready to explore" : "No model connected",
                "A fresh conversation, running locally.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SystemPrompt is null) return;
        UpdateMode();
        if (IsPerf)
        {
            SetBusy(false);
            UpdatePerfSession();
            ShowStatus(loaded ? "Ready to benchmark" : "No model connected", "", InfoBarSeverity.Informational);
            return;
        }
        if (IsDecision)
        {
            SetBusy(false);
            ShowStatus(decisionLoaded ? "Decision session ready" : "No decision model connected", "", InfoBarSeverity.Informational);
            return;
        }
        if (IsSpeech)
        {
            SetBusy(false);
            ShowStatus(speechLoaded ? "Speech session ready" : "Capture ready", "", InfoBarSeverity.Informational);
            return;
        }
        SetBusy(true);
        try
        {
            await runtime.ResetAsync();
            ClearOutput();
            ShowStatus(loaded ? "Ready to explore" : "No model connected", "", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private void ClearOutput()
    {
        Messages.Clear();
        OutputScroll.Visibility = Visibility.Collapsed;
        WelcomeScroll.Visibility = Visibility.Visible;
        ResultDetails.Text = "Generated answers can be inaccurate. Review important information.";
    }

    private void SetBusy(bool value)
    {
        value |= closing;
        busy = value;
        BusyRing.IsActive = value;
        BusyRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.IsEnabled = !value && !loaded;
        LoadButton.IsEnabled = !value && !loaded && selectedGpu is not null;
        DeviceSelector.IsEnabled = !value && !loaded && !speechLoaded && !decisionLoaded && DeviceSelector.Items.Count > 0;
        if (!loaded && !speechLoaded && !decisionLoaded && selectedGpu is not null) ShowSelectedDevice();
        // Perf benchmarks the text session, so it keeps the text model setup in the sidebar.
        bool textSidebar = IsText || IsPerf;
        SetupPanel.Visibility = textSidebar && !loaded ? Visibility.Visible : Visibility.Collapsed;
        LoadedModelCard.Visibility = textSidebar && loaded ? Visibility.Visible : Visibility.Collapsed;
        UnloadButton.Visibility = textSidebar && loaded ? Visibility.Visible : Visibility.Collapsed;
        UnloadButton.IsEnabled = !value && loaded;
        SendButton.IsEnabled = !value && loaded;
        ClearButton.IsEnabled = !value;
        TaskMode.IsEnabled = !value;
        SamplingPanel.IsEnabled = !value;
        SamplingPanel.Visibility = IsText ? Visibility.Visible : Visibility.Collapsed;
        SystemPrompt.IsEnabled = !value && TaskMode.SelectedIndex == 0;
        Prompt.IsEnabled = !value;
        StopButton.IsEnabled = false;
        StopButton.Visibility = Visibility.Collapsed;
        WelcomeHint.Text = loaded ? "Pick a starting point, or write your own prompt." : "Connect a model in the sidebar to get started.";
        ComposerHint.Text = loaded ? "Ctrl + Enter to send" : "Connect a model to begin";
        UpdateSpeechControls();
        UpdateDecisionControls();
        UpdatePerfControls();
        UpdateStatusDot();
    }

    private void ShowError(Exception error)
    {
        // The app wraps some Runtime failures with a clearer message; show the Runtime's code, not the wrapper's.
        var code = $"0x{(error.InnerException ?? error).HResult:X8}";
        // A bare HRESULT's system message already ends with its code.
        string message = error.Message.Contains(code, StringComparison.OrdinalIgnoreCase) ? error.Message : $"{error.Message} ({code})";
        ShowStatus("Task operation failed", message, InfoBarSeverity.Error);
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning;
        SessionStatus.Text = title;
        hasError = severity == InfoBarSeverity.Error;
        UpdateStatusDot();
    }

    private void UpdateStatusDot()
    {
        StatusDot.Fill = new SolidColorBrush(hasError ? ColorHelper.FromArgb(255, 248, 81, 73)
            : busy ? ColorHelper.FromArgb(255, 121, 180, 255)
            : (IsDecision ? decisionLoaded : IsSpeech ? speechLoaded : loaded) ? ColorHelper.FromArgb(255, 63, 185, 80)
            : ColorHelper.FromArgb(255, 127, 140, 156));
    }

    private void UpdateMode()
    {
        bool chat = TaskMode.SelectedIndex == 0;
        TextOutput.Visibility = IsText ? Visibility.Visible : Visibility.Collapsed;
        ComposerPanel.Visibility = IsText ? Visibility.Visible : Visibility.Collapsed;
        SpeechWorkspace.Visibility = IsSpeech ? Visibility.Visible : Visibility.Collapsed;
        SpeechSetupPanel.Visibility = IsSpeech ? Visibility.Visible : Visibility.Collapsed;
        DecisionWorkspace.Visibility = IsDecision ? Visibility.Visible : Visibility.Collapsed;
        DecisionSetupPanel.Visibility = IsDecision ? Visibility.Visible : Visibility.Collapsed;
        PerfWorkspace.Visibility = IsPerf ? Visibility.Visible : Visibility.Collapsed;
        BackendLabel.Text = IsSpeech ? "ASR / CGC / GPU" : IsDecision ? "Decision / CGC / GPU" : IsPerf ? "Perf / app-measured / CGC" : "CGC / GPU";
        InstructionsPanel.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
        SendLabel.Text = chat ? "Send message" : "Generate";
        Prompt.PlaceholderText = chat ? "What's on your mind?" : "Write a prompt to continue...";
        WelcomeTitle.Text = chat ? "Big ideas. Local intelligence." : "A prompt. A world of possibility.";
        WelcomeDescription.Text = chat
            ? "A space to think, build, and explore with your models.\nPowered by Windows ML Tasks. Running on your device."
            : "Explore raw text generation, one token at a time.\nYour prompt goes straight to the Text Generation Task.";
        TaskFamilyLabel.Text = IsSpeech ? "Automatic Speech Recognition" : IsDecision ? "Typed Decision" : IsPerf ? "Text Generation performance" : chat ? "Chat Completion" : "Text Generation";
    }

    private void Temperature_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TemperatureValue is not null) TemperatureValue.Text = e.NewValue.ToString("F2");
    }

    private void Starter_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: string prompt }) return;
        Prompt.Text = prompt;
        Prompt.Focus(FocusState.Programmatic);
    }

    private void Prompt_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter &&
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            e.Handled = true;
            Send_Click(SendButton, new RoutedEventArgs());
        }
    }

    private void ScrollToLatest() => DispatcherQueue.TryEnqueue(() =>
        OutputScroll.ChangeView(null, OutputScroll.ScrollableHeight, null, true));

    private static string FriendlyFinishReason(string reason) =>
        reason[(reason.LastIndexOf("_REASON_", StringComparison.Ordinal) + "_REASON_".Length)..]
            .Replace('_', ' ').ToLowerInvariant();

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (closing) return;
        closing = true;
        generation?.Cancel();
        speechCancellation?.Cancel();
        decisionCancellation?.Cancel();
        benchmarkCancellation?.Cancel();
        SetBusy(true);
        try
        {
            microphone?.Stop();
            if (captureOperation is not null) await captureOperation;
            await runtime.DisposeAsync();
            AppWindow.Closing -= OnClosing;
            Close();
        }
        catch (Exception error)
        {
            closing = false;
            ShowError(error);
            SetBusy(false);
        }
    }
}
