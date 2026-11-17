// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TaskStudio;

public sealed partial class MainWindow
{
    // The model list for the Windows ML release this app builds against.
    private const string ModelsListUrl = "https://github.com/microsoft/WindowsML/blob/release/2.9-experimental/Apps/TaskStudio/MODELS.md";

    private async void ModelHelp_Click(object sender, RoutedEventArgs e)
    {
        var body = new StackPanel { Spacing = 14, MaxWidth = 560 };
        void Section(string glyph, string title, string text)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
            header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            body.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children = { header, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.85 } }
            });
        }
        Section("\uE8B7", "Folder: Hugging Face safetensors",
            "Pick the model folder with the folder button. It must contain config.json and model.safetensors (or an index and shards) as published. " +
            "The tokenizer defaults to that folder's tokenizer_config.json. " +
            "generation_config.json, when present, supplies the sampling defaults. Examples: Qwen/Qwen3-0.6B, Qwen/Qwen2.5-0.5B-Instruct.");
        Section("\uE8E5", "File: GGUF (Chat and Completion)",
            "Pick a single .gguf file with the file button. A file loads when the Runtime supports its architecture and tensor types, " +
            "such as F16, BF16, Q8_0, Q4_0 or Q4_K_M. Leave the tokenizer empty to use the tokenizer and chat template stored in the file; " +
            "if the Runtime cannot read it, set the model's tokenizer_config.json instead. Sampling starts from the Task defaults. " +
            "Example: Llama-3.2-1B-Instruct-Q4_0.gguf.");
        Section("\uE7BA", "Model support depends on the Runtime",
            "The Runtime decides which architectures run on the CGC backend; unsupported files fail with \"not supported\" (0x80070032) rather than " +
            "running incorrectly. See the list of checkpoints known to work, with links, below.");
        Section("\uE720", "Speech and Decide",
            "Speech takes a safetensors ASR model folder (openai/whisper-tiny, nvidia/nemotron-3.5-asr-streaming-0.6b) with its tokenizer_config.json. " +
            "Decide takes a typed-decision folder, such as convaiinnovations/laya.");
        body.Children.Add(new TextBlock { Text = "DOWNLOAD WITH THE HUGGING FACE CLI", Style = (Style)Application.Current.Resources["SectionLabel"], Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x11, 0x17, 0x1E)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12),
            Child = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 12, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
                Text = "pip install -U \"huggingface_hub[cli]\"\n\n" +
                       "# Safetensors model -> a FOLDER\n" +
                       "hf download Qwen/Qwen3-0.6B --local-dir C:\\models\\Qwen3-0.6B\n\n" +
                       "# A single GGUF FILE\n" +
                       "hf download bartowski/Llama-3.2-1B-Instruct-GGUF Llama-3.2-1B-Instruct-Q4_0.gguf --local-dir C:\\models\n" +
                       "hf download ggml-org/gemma-3-1b-it-GGUF gemma-3-1b-it-f16.gguf --local-dir C:\\models"
            }
        });
        var models = new Hyperlink { NavigateUri = new Uri(ModelsListUrl) };
        models.Inlines.Add(new Run { Text = "Models known to work (MODELS.md)" });
        var modelsLine = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        modelsLine.Inlines.Add(models);
        modelsLine.Inlines.Add(new Run { Text = ": Hugging Face links for checkpoints tested in this app." });
        body.Children.Add(modelsLine);
        var link = new Hyperlink { NavigateUri = new Uri("https://huggingface.co/docs/huggingface_hub/guides/cli") };
        link.Inlines.Add(new Run { Text = "Hugging Face CLI docs" });
        var docs = new TextBlock { FontSize = 12, Opacity = 0.85, TextWrapping = TextWrapping.Wrap };
        docs.Inlines.Add(new Run { Text = "Gated models need hf auth login first. See the " });
        docs.Inlines.Add(link);
        docs.Inlines.Add(new Run { Text = "." });
        body.Children.Add(docs);

        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "What models can I load?",
            Content = new ScrollViewer { Content = body, MaxHeight = 560 },
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close
        };
        try { await dialog.ShowAsync(); }
        catch (Exception error) { ShowError(error); }
    }
}
