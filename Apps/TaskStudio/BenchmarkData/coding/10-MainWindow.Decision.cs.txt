// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskStudio.Runtime;

namespace TaskStudio;

public sealed partial class MainWindow
{
    private bool IsDecision => TaskMode.SelectedIndex == 3;
    private bool decisionLoaded;
    private uint decisionMaximumOptions = uint.MaxValue;
    private CancellationTokenSource? decisionCancellation;
    public ObservableCollection<DecisionQuestionItem> DecisionQuestions { get; } = new();
    public ObservableCollection<DecisionResultItem> DecisionResults { get; } = new();

    private void InitializeDecision()
    {
        DecisionExample.ItemsSource = DecisionExamples.Names;
        DecisionExample.SelectedIndex = 0;
    }

    private void DecisionExample_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DecisionExample.SelectedIndex < 0) return;
        var (state, questions) = DecisionExamples.Get(DecisionExample.SelectedIndex);
        DecisionState.Text = state;
        DecisionQuestions.Clear();
        foreach (var question in questions) DecisionQuestions.Add(question);
        DecisionResults.Clear();
        DecisionResultsPanel.Visibility = Visibility.Collapsed;
    }

    private void AddQuestion_Click(object sender, RoutedEventArgs e) =>
        DecisionQuestions.Add(new DecisionQuestionItem { Name = $"question_{DecisionQuestions.Count + 1}" });

    private void RemoveQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && sender is Button { Tag: DecisionQuestionItem question }) DecisionQuestions.Remove(question);
    }

    private async void BrowseDecisionModel_Click(object sender, RoutedEventArgs e)
    {
        try { DecisionModelPath.Text = await PickFolderAsync() ?? DecisionModelPath.Text; }
        catch (Exception error) { ShowError(error); }
    }

    private async void DecisionLoad_Click(object sender, RoutedEventArgs e)
    {
        if (busy || decisionLoaded) return;
        SetBusy(true);
        SessionStatus.Text = "Preparing the decision model...";
        try
        {
            var capabilities = await runtime.LoadDecisionAsync(DecisionModelPath.Text.Trim(), RequireGpu());
            decisionLoaded = true;
            decisionMaximumOptions = capabilities.MaximumOptions;
            ShowActiveDevice(capabilities.Device);
            DecisionCapabilityTitle.Text = $"Decision session ready  /  {(capabilities.Calibrated ? "calibrated" : "uncalibrated")} probabilities";
            DecisionCapabilityDetail.Text = $"Up to {capabilities.MaximumOptions} options per question. " +
                (capabilities.Calibrated ? "Confidence is the calibrated probability of the answer." : "Confidence is not calibrated for this model.");
            ShowStatus("Decision session ready", $"CGC / {capabilities.Device.Name}", InfoBarSeverity.Success);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void DecisionUnload_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            await runtime.UnloadDecisionAsync();
            decisionLoaded = false;
            decisionMaximumOptions = uint.MaxValue;
            DecisionCapabilityTitle.Text = "Connect a typed-decision model to start.";
            DecisionCapabilityDetail.Text = "Choice picks a label, score picks an ordinal level, yes/no answers true or false. Each answer carries a probability for every option.";
            ShowStatus("Decision session released", "", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally { SetBusy(false); }
    }

    private async void Decide_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !decisionLoaded) return;
        SetBusy(true);
        using var cancellation = new CancellationTokenSource();
        decisionCancellation = cancellation;
        SessionStatus.Text = "Evaluating on your device...";
        try
        {
            var items = DecisionQuestions.ToArray();
            if (items.Length == 0) throw new ArgumentException("Add at least one question.");
            var requests = items.Select(item => item.ToQuestion(decisionMaximumOptions)).ToArray();
            var outcome = await runtime.DecideAsync(DecisionState.Text, requests, cancellation.Token);
            if (outcome.Canceled)
            {
                ShowStatus("Decision canceled", "", InfoBarSeverity.Informational);
                return;
            }
            DecisionResults.Clear();
            for (int i = 0; i < outcome.Answers.Count; i++)
                DecisionResults.Add(DecisionResultItem.Create(items[i], requests[i], outcome.Answers[i]));
            DecisionSummary.Text = $"{outcome.Answers.Count} answers  /  {outcome.InputTokens:N0} tokens  /  {outcome.Elapsed.TotalMilliseconds:F0} ms  /  CGC";
            DecisionResultsPanel.Visibility = Visibility.Visible;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                double top = DecisionResultsPanel.TransformToVisual((UIElement)DecisionWorkspace.Content)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                DecisionWorkspace.ChangeView(null, Math.Max(0, top - 16), null);
            });
            int escalations = outcome.Answers.Count(answer => !answer.Act);
            ShowStatus("Decision complete", escalations == 0 ? "Every answer recommends acting."
                : $"{escalations} answer(s) recommend escalating.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            decisionCancellation = null;
            SetBusy(false);
        }
    }

    private void UpdateDecisionControls()
    {
        DecisionModelPath.IsEnabled = DecisionBrowseButton.IsEnabled = !busy && !decisionLoaded;
        DecisionLoadButton.IsEnabled = !busy && !decisionLoaded && selectedGpu is not null;
        DecisionLoadButton.Visibility = decisionLoaded ? Visibility.Collapsed : Visibility.Visible;
        DecisionUnloadButton.Visibility = decisionLoaded ? Visibility.Visible : Visibility.Collapsed;
        DecisionUnloadButton.IsEnabled = !busy && decisionLoaded;
        DecideButton.IsEnabled = !busy && decisionLoaded;
        DecisionExample.IsEnabled = AddQuestionButton.IsEnabled = DecisionQuestionList.IsEnabled = DecisionState.IsEnabled = !busy;
        DecisionHint.Text = decisionLoaded
            ? "One evaluation per question. A longer request than before prepares a larger program once."
            : "Connect a model to decide.";
    }
}
