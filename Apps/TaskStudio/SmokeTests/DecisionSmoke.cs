// Copyright (C) Microsoft Corporation. All rights reserved.

using TaskStudio.Runtime;

internal static class DecisionSmoke
{
    // The Runtime system1-decision sample's quickstart request.
    private const string State = "Hi, we were billed twice for March. Please refund the duplicate today or we will cancel our plan.";

    private static DecisionQuestion[] Questions(float? departmentThreshold = null) =>
    [
        new(DecisionKind.Choice, "Which department should handle this?",
            [new("billing", "invoices, payments, refunds"), new("technical", "bugs, outages, system errors"), new("other", "everything else")],
            departmentThreshold),
        new(DecisionKind.Score, "How urgent is this?", [new(null, "not urgent"), new(null, "soon"), new(null, "blocking")]),
        new(DecisionKind.YesNo, "Does the user threaten to cancel or leave?", [])
    ];

    public static async Task<int> RunAsync(string model)
    {
        await using var runtime = new TaskRuntime();
        var device = (await runtime.GetGpuDevicesAsync()).First();
        var capabilities = await runtime.LoadDecisionAsync(model, device);
        Console.WriteLine($"Decision GPU: {capabilities.Device.Name} ({capabilities.Device.Identity}), max options {capabilities.MaximumOptions}, calibrated {capabilities.Calibrated}.");
        if (capabilities.Device.Identity != device.Identity) throw new InvalidOperationException("The decision session did not use the selected GPU.");

        var outcome = await runtime.DecideAsync(State, Questions(), CancellationToken.None);
        string[] names = ["department", "urgency", "churn_risk"];
        for (int i = 0; i < outcome.Answers.Count; i++)
        {
            var answer = outcome.Answers[i];
            Console.WriteLine($"[{names[i]}] {answer.Kind} -> {answer.Selected}  p=[{string.Join(", ", answer.Probabilities.Select(p => p.ToString("F4")))}]  " +
                $"conf {answer.Confidence:F4} dist {answer.DistributionConfidence:F4} expected {answer.ExpectedScore:F4} act {answer.ActProbability:F4} -> {(answer.Act ? "ACT" : "ESCALATE")}");
            if (Math.Abs(answer.Probabilities.Sum() - 1) > 1e-3) throw new InvalidOperationException("Probabilities do not sum to one.");
        }
        Console.WriteLine($"Input tokens: {outcome.InputTokens}, {outcome.Elapsed.TotalMilliseconds:F1} ms");
        if (outcome.Canceled || outcome.Answers.Count != 3) throw new InvalidOperationException("Expected three answers.");
        if (outcome.Answers[0].Selected != 0) throw new InvalidOperationException("Expected the billing department.");
        if (outcome.Answers[1].Selected != 2) throw new InvalidOperationException("Expected the blocking urgency level.");
        if (outcome.Answers[2].Selected != 1) throw new InvalidOperationException("Expected churn risk to be true.");
        Console.WriteLine("PASS: quickstart decisions on the GPU match the Runtime sample's reference answers.");

        var strict = await runtime.DecideAsync(State, Questions(0.999f), CancellationToken.None);
        var lenient = await runtime.DecideAsync(State, Questions(0.5f), CancellationToken.None);
        if (strict.Answers[0].Act || !lenient.Answers[0].Act) throw new InvalidOperationException("The act threshold did not gate the recommendation.");
        Console.WriteLine("PASS: act threshold gates ACT/ESCALATE.");

        try
        {
            await runtime.DecideAsync(State, [new(DecisionKind.Choice, "Pick one", [new("a", null), new("a", null)])], CancellationToken.None);
            throw new InvalidOperationException("A duplicate choice label was accepted.");
        }
        catch (ArgumentException error) when (error.HResult == unchecked((int)0x80070057)) { }
        Console.WriteLine("PASS: invalid request rejected with E_INVALIDARG.");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            if (!(await runtime.DecideAsync(State, Questions(), canceled.Token)).Canceled)
                throw new InvalidOperationException("Pre-canceled decision was not canceled.");
        }
        Console.WriteLine("PASS: cancellation.");

        await runtime.UnloadDecisionAsync();
        await runtime.LoadDecisionAsync(model, device);
        var reloaded = await runtime.DecideAsync(State, Questions(), CancellationToken.None);
        if (reloaded.Answers[0].Selected != 0) throw new InvalidOperationException("Reloaded session returned a different answer.");
        Console.WriteLine("PASS: unload/reload.");
        return 0;
    }
}
