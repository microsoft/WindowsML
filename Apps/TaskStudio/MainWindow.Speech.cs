// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskStudio.Audio;
using TaskStudio.Runtime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TaskStudio;

public sealed partial class MainWindow
{
    private bool IsSpeech => TaskMode.SelectedIndex == 2;
    private bool speechLoaded;
    private bool supportsLiveSpeech;
    private SpeechAudio? audioClip;
    private MicrophoneCapture? microphone;
    private Task? captureOperation;
    private CancellationTokenSource? speechCancellation;
    private int speechRevision;

    private async void BrowseSpeechModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await PickFolderAsync();
            if (path is null) return;
            SpeechModelPath.Text = path;
            string tokenizer = System.IO.Path.Combine(path, "tokenizer_config.json");
            if (System.IO.File.Exists(tokenizer)) SpeechTokenizerPath.Text = tokenizer;
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void BrowseSpeechTokenizer_Click(object sender, RoutedEventArgs e)
    {
        try { SpeechTokenizerPath.Text = await PickFileAsync(".json") ?? SpeechTokenizerPath.Text; }
        catch (Exception error) { ShowError(error); }
    }

    private async void SpeechLoad_Click(object sender, RoutedEventArgs e)
    {
        if (busy || speechLoaded) return;
        SetBusy(true);
        SessionStatus.Text = "Preparing the speech model...";
        try
        {
            var capabilities = await runtime.LoadSpeechAsync(SpeechModelPath.Text, SpeechTokenizerPath.Text, SpeechLanguage.Text.Trim(), RequireGpu());
            speechLoaded = true;
            ShowActiveDevice(capabilities.Device);
            supportsLiveSpeech = capabilities.LiveInput;
            SpeechCapabilityTitle.Text = supportsLiveSpeech ? "Speech connected. Recorded + native live input." : "Speech connected. Recorded input available.";
            SpeechCapabilityDetail.Text = supportsLiveSpeech
                ? "The session exposes IWinMLAutomaticSpeechRecognitionLiveSession. Microphone chunks go directly to the Task, with bounded buffering."
                : "This session does not expose native live input. Record a clip or import audio, then transcribe it.";
            ShowStatus("Speech session ready", $"CGC / {capabilities.Device.Name} / " +
                (runtime.SpeechProgramCacheUsed ? "compiled programs reused across runs" : "this model compiles per session"), InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void SpeechUnload_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await runtime.UnloadSpeechAsync();
            speechLoaded = supportsLiveSpeech = false;
            SpeechCapabilityTitle.Text = "Capture ready. Connect a speech model to transcribe.";
            SpeechCapabilityDetail.Text = "Recording and file import do not require a model. Native live input is checked after the speech session is created.";
            ShowStatus("Speech session released", "", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private void SpeechInput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RecordedAudioPanel is not null) UpdateSpeechControls();
    }

    private async void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker();
            foreach (string extension in new[] { ".wav", ".mp3", ".m4a", ".flac", ".wma", ".aac" })
                picker.FileTypeFilter.Add(extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            audioClip = await Task.Run(() => SpeechAudio.Read(file.Path));
            AudioClipLabel.Text = $"{file.Name}  /  {audioClip.DurationSeconds:F1}s  /  16 kHz mono";
            ShowStatus("Audio ready", "", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void RecordAudio_Click(object sender, RoutedEventArgs e) => await CaptureAsync(false);
    private async void StartLive_Click(object sender, RoutedEventArgs e) => await CaptureAsync(true);

    private async Task CaptureAsync(bool live)
    {
        if (busy || (live && (!speechLoaded || !supportsLiveSpeech))) return;
        captureOperation = RunCaptureAsync(live);
        await captureOperation;
        captureOperation = null;
    }

    private async Task RunCaptureAsync(bool live)
    {
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        speechCancellation = cancellation;
        var channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(50)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        int revision = ++speechRevision;
        long sampleCount = 0;
        var meter = new Progress<(double Level, double Seconds)>(value =>
        {
            if (revision != speechRevision || closing) return;
            MicrophoneLevel.Value = value.Level;
            RecordingTime.Text = TimeSpan.FromSeconds(value.Seconds).ToString(@"m\:ss");
        });
        using var capture = new MicrophoneCapture(samples =>
        {
            if (live && !channel.Writer.TryWrite(samples))
                throw new InvalidOperationException("The recognizer cannot keep up with microphone audio. Capture stopped at the five-second queue limit; no audio was silently dropped.");
            sampleCount += samples.Length;
            double level = Math.Sqrt(samples.Average(sample => (double)sample * sample));
            ((IProgress<(double, double)>)meter).Report((Math.Min(1, level * 4), sampleCount / 16000d));
        });
        microphone = capture;
        try
        {
            capture.Start();
            CaptureMeter.Visibility = FinishCaptureButton.Visibility = CancelSpeechButton.Visibility = Visibility.Visible;
            FinishCaptureButton.Content = live ? "Finish live input" : "Finish recording";
            SessionStatus.Text = live ? "Listening / native live transcription" : "Recording a clip / finish when ready";
            Task<SpeechAudio> capturedAudio = CompleteAudioInputAsync(capture, channel.Writer);
            if (live)
            {
                SpeechTranscript.Text = "";
                Task<SpeechResult> recognition = runtime.TranscribeLiveAsync(channel.Reader, SpeechProgress(revision), cancellation.Token);
                await Task.WhenAny(capturedAudio, recognition);
                if (recognition.IsCompleted) capture.Stop();
                await Task.WhenAll(capturedAudio, recognition);
                var result = await recognition;
                SpeechTranscript.Text = result.Text;
                ShowStatus(result.Canceled ? "Live transcription stopped" : "Live transcription complete", "", InfoBarSeverity.Informational);
            }
            else
            {
                await capturedAudio;
                ShowStatus(cancellation.IsCancellationRequested ? "Recording discarded" : "Recording ready", "", InfoBarSeverity.Informational);
            }
            if (!cancellation.IsCancellationRequested)
            {
                audioClip = await capturedAudio;
                AudioClipLabel.Text = $"Microphone recording  /  {audioClip.DurationSeconds:F1}s  /  16 kHz mono";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowStatus("Speech operation canceled", "Partial text may be incomplete.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            speechRevision++;
            channel.Writer.TryComplete();
            microphone = null;
            speechCancellation = null;
            CaptureMeter.Visibility = FinishCaptureButton.Visibility = CancelSpeechButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private static async Task<SpeechAudio> CompleteAudioInputAsync(MicrophoneCapture capture, ChannelWriter<float[]> writer)
    {
        try
        {
            SpeechAudio result = await capture.Completion;
            writer.TryComplete();
            return result;
        }
        catch (Exception error)
        {
            writer.TryComplete(error);
            throw;
        }
    }

    private void FinishCapture_Click(object sender, RoutedEventArgs e)
    {
        microphone?.Stop();
        FinishCaptureButton.IsEnabled = false;
        SessionStatus.Text = "Finishing audio input...";
    }

    private void CancelSpeech_Click(object sender, RoutedEventArgs e)
    {
        speechCancellation?.Cancel();
        microphone?.Stop();
        CancelSpeechButton.IsEnabled = false;
        SessionStatus.Text = "Canceling speech operation...";
    }

    private async void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !speechLoaded || audioClip is null) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        speechCancellation = cancellation;
        int revision = ++speechRevision;
        CancelSpeechButton.Visibility = Visibility.Visible;
        CancelSpeechButton.IsEnabled = true;
        SpeechTranscript.Text = "";
        SessionStatus.Text = "Transcribing audio on your device...";
        try
        {
            var result = await runtime.TranscribeAsync(audioClip.Samples, SpeechProgress(revision), cancellation.Token);
            SpeechTranscript.Text = result.Text;
            ShowStatus(result.Canceled ? "Transcription stopped" : "Transcription complete", "", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            speechRevision++;
            speechCancellation = null;
            CancelSpeechButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private IProgress<string> SpeechProgress(int revision)
    {
        var text = new StringBuilder();
        return new Progress<string>(fragment =>
        {
            if (revision != speechRevision || closing) return;
            text.Append(fragment);
            SpeechTranscript.Text = text.ToString();
        });
    }

    private async void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        if (busy || audioClip is null) return;
        SetBusy(true);
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "speech-recording" };
            picker.FileTypeChoices.Add("Wave audio", new List<string> { ".wav" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await Task.Run(() => audioClip.Save(file.Path));
                ShowStatus("Audio saved", "", InfoBarSeverity.Informational);
            }
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private void CopyTranscript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(SpeechTranscript.Text);
            Clipboard.SetContent(data);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void UpdateSpeechControls()
    {
        bool live = SpeechInputMode.SelectedIndex == 1;
        RecordedAudioPanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        LiveAudioPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        SpeechModelPath.IsEnabled = SpeechTokenizerPath.IsEnabled = SpeechLanguage.IsEnabled = !busy && !speechLoaded;
        SpeechBrowseModelButton.IsEnabled = SpeechBrowseTokenizerButton.IsEnabled = !busy && !speechLoaded;
        SpeechLoadButton.IsEnabled = !busy && !speechLoaded && selectedGpu is not null;
        SpeechLoadButton.Visibility = speechLoaded ? Visibility.Collapsed : Visibility.Visible;
        SpeechUnloadButton.Visibility = speechLoaded ? Visibility.Visible : Visibility.Collapsed;
        SpeechUnloadButton.IsEnabled = !busy && speechLoaded;
        SpeechInputMode.IsEnabled = ImportAudioButton.IsEnabled = RecordAudioButton.IsEnabled = !busy;
        SaveAudioButton.IsEnabled = !busy && audioClip is not null;
        StartLiveButton.IsEnabled = !busy && speechLoaded && supportsLiveSpeech;
        TranscribeButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        TranscribeButton.IsEnabled = !busy && speechLoaded && audioClip is not null;
        CopyTranscriptButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(SpeechTranscript.Text);
        FinishCaptureButton.IsEnabled = CancelSpeechButton.IsEnabled = !closing;
    }
}
