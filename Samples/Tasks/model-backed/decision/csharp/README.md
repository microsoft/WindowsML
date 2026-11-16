<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Typed decisions in C#

This is the C# version of the [typed decisions sample](../README.md). It takes
the same arguments, prints the same output and returns the same exit codes as
the C++ sample.

> [!NOTE]
> This route needs a GPU target, as the C++ sample does. The output below is
> from the C++ sample's README, with the capability and input token lines that
> both samples also print.

The sample loads a decision model folder with `WinMLRuntime.LoadModelFromPath`,
creates a GPU execution target with the performance preference, and calls
`CreateDecisionTaskFromModel` with no tokenizer, so the task creates the
model's own tokenizer from the folder. With `--program-cache` it passes
`DecisionModelOptions`. It prints the task's capabilities, asks one choice
question about the state with `Decider.DecideAsync`, closes the session, and
then reads the result: each option's probability, the selected option, its
confidence, the recommendation and the number of input tokens. Ctrl+C during
the decision cancels a `CancellationToken`, which cancels the decision through
the Task's cancellation source.

```text
Maximum options: <count>; calibrated probabilities: <yes or no>
State: I was charged twice for my subscription.
Question: Which team should handle this support ticket?
billing: 0.9793
technical: 0.0207
Selected: billing; confidence: 0.9793; recommendation: act
Input tokens: <count>
```

The sample explains the same errors as the C++ sample: a model or target that
Windows ML cannot run for decisions (`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`
from `CreateDecisionTaskFromModel`), a package without the decision Task
(`E_NOINTERFACE`), and missing files. On a device without a GPU, creating the
GPU target fails with `HRESULT_FROM_WIN32(ERROR_NOT_FOUND)`.

## How the sample calls the Runtime

The sample calls the Runtime and Task APIs through the C# sample wrapper in
[`Runtime/shared/csharp/Runtime`](../../../../Runtime/shared/csharp/Runtime/),
which gives their COM-style C API an idiomatic C# surface. The sample code has
no pointers or `HRESULT` checks. The wrapper is sample code, not a supported
API; the [shared README](../../../../Runtime/shared/csharp/README.md) explains
why it exists and what it covers.

- Every object is `IDisposable`. `tasks.Decision` returns the decision task
  factory, which the C++ sample gets with `QueryInterface`.
- A failure throws a .NET exception that keeps the `HRESULT`: for example,
  `NotSupportedException` for `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. The
  sample catches the exception type where the C++ sample checks a returned
  `HRESULT`, and prints `ex.HResult` where the C++ sample prints the
  `HRESULT`.
- The request is plain C#: a `DecisionRequest` with a state and
  `DecisionQuestion` values whose `DecisionOption` records hold each label and
  description. The wrapper copies them into the native request for the call.
- A decision session belongs to the thread that created it. `Decider` keeps
  the session on a dedicated thread, and `DecideAsync` accepts a
  `CancellationToken`. `CloseAsync` closes the session and reports a failure
  to close, as the C++ sample does. The `DecisionResult` is immutable and stays
  readable after the session closes.
- Numbers print with the invariant culture, so `F4` matches the C++ sample's
  `%.4f` on any system locale.
- The project imports
  [`WinMLSamples.CSharp.props`](../../../../Runtime/shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share and which compiles the wrapper, and builds into
  `Samples\Tasks\out`.

## Requirements

- The .NET 8 SDK or later.
- The `Microsoft.Windows.AI.MachineLearning` version pinned in
  `Samples\Directory.Packages.props`. See
  [Package requirements](../../README.md#package-requirements).
- A GPU. A CPU target returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample decision-csharp -PackageOnly

.\run_decision.ps1 -Language csharp
.\run_decision.ps1 -Language csharp -State "The app closes every time I open the settings page."
.\run_decision.ps1 -Language csharp -ProgramCache .\models\program-cache -CacheWeights
```

`dotnet build model-backed\decision\csharp\task-decision-csharp.csproj -c Release -p:Platform=x64`
builds the same project. Use `-Platform ARM64` (or `-p:Platform=ARM64`) on an
Arm64 device. The executable is
`out\<platform>\<configuration>\task-decision-csharp\task-decision-csharp.exe`:

```text
task-decision-csharp.exe <model-directory> [state] [--program-cache <directory> [--cache-weights]]
```
