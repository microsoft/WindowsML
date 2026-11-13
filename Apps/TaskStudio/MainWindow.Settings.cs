// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskStudio.Runtime;

namespace TaskStudio;

public sealed partial class MainWindow
{
    private static (int Files, long Bytes) ProgramCacheSize()
    {
        var directory = new DirectoryInfo(TaskRuntime.CompiledProgramDirectory);
        if (!directory.Exists) return (0, 0);
        var files = directory.EnumerateFiles("*", SearchOption.AllDirectories).ToArray();
        return (files.Length, files.Sum(file => file.Length));
    }

    private static string DescribeProgramCache()
    {
        var (files, bytes) = ProgramCacheSize();
        return files == 0 ? "Empty" : $"{files:N0} files  /  {bytes / (1024.0 * 1024.0):N1} MB";
    }

    private static string RuntimeVersion()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "WinMLRuntimeCore.dll");
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown";
        }
        catch (IOException) { return "unknown"; }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool anyLoaded = loaded || speechLoaded || decisionLoaded;
        var size = new TextBlock { Text = DescribeProgramCache(), FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 13 };
        var result = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var delete = new Button
        {
            Content = "Delete program cache",
            IsEnabled = !anyLoaded
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteProgramCacheButton");
        var open = new Button { Content = "Open folder" };
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(TaskRuntime.CompiledProgramDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{TaskRuntime.CompiledProgramDirectory}\"") { UseShellExecute = true });
            }
            catch (Exception error) { ShowError(error); }
        };
        delete.Click += (_, _) =>
        {
            try
            {
                // Only this app's cache directory is removed; model files are never touched.
                if (Directory.Exists(TaskRuntime.CompiledProgramDirectory))
                    Directory.Delete(TaskRuntime.CompiledProgramDirectory, recursive: true);
                result.Text = "Program cache deleted. The next load of each model compiles its programs again.";
                ShowStatus("Program cache deleted", "", InfoBarSeverity.Informational);
            }
            catch (Exception error)
            {
                result.Text = $"Could not delete every file: {error.Message}";
            }
            result.Visibility = Visibility.Visible;
            size.Text = DescribeProgramCache();
        };

        var cache = new StackPanel { Spacing = 12 };
        cache.Children.Add(new TextBlock { Text = "COMPILED PROGRAM CACHE", Style = (Style)Application.Current.Resources["SectionLabel"] });
        cache.Children.Add(new TextBlock
        {
            Text = "Text, speech and decision sessions reuse compiled programs from this folder across runs. It keeps programs only, as IR " +
                   "(input.mlir, program.oir); weights always load from the model's own files. Deleting it is safe: the next load compiles again.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.85
        });
        cache.Children.Add(new TextBlock
        {
            Text = TaskRuntime.CompiledProgramDirectory, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code,Consolas"), FontSize = 12
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Size:", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(size);
        cache.Children.Add(row);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(delete);
        buttons.Children.Add(open);
        cache.Children.Add(buttons);
        if (anyLoaded)
            cache.Children.Add(new TextBlock
            {
                Text = "Release all text, speech and decision sessions to delete the cache.",
                TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7
            });
        cache.Children.Add(result);

        var body = new StackPanel { Spacing = 14, MaxWidth = 520, MinWidth = 440 };
        body.Children.Add(new TextBlock { Text = "ABOUT", Style = (Style)Application.Current.Resources["SectionLabel"] });
        body.Children.Add(new TextBlock
        {
            Text = $"Windows ML Runtime {RuntimeVersion()}\nGPU: {selectedGpu?.Name ?? "none selected"} / CGC",
            TextWrapping = TextWrapping.Wrap, FontSize = 13
        });
        var advanced = new Expander
        {
            Header = "Advanced",
            Content = cache,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(advanced, "SettingsAdvanced");
        body.Children.Add(advanced);

        var dialog = new ContentDialog
        {
            XamlRoot = Shell.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Settings",
            Content = body,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close
        };
        try { await dialog.ShowAsync(); }
        catch (Exception error) { ShowError(error); }
    }
}
