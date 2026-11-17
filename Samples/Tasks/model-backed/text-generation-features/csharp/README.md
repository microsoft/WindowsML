<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation features in C#

This C# sample exercises the optional interfaces of the
[text generation Task](../../../../../docs/api-reference/IWinMLTextGenerationTask.md),
one short scenario each, on a GGUF file that the llama.cpp model loader loads
for the CPU. It has no C++ counterpart; the
[text generation sample](../../text-generation/csharp/) shows the basic
generation loop. Because it touches every optional interface, the sample also
serves as the run-time check for those wrappers, so some lines print what a
getter returned rather than what an app would do with it.

| Scenario | What it shows |
|---|---|
| `continuation` | `TextGenerationSession.Capabilities`, `State`, `GetRuntime`, and `GetConfiguration`; `WinMLTasks.GetRuntime` and `TaskRuntimeIdentity`; `TextGenerationTask.GetDefaultEosTokenIds`; `TextGenerationOptions.GetValues`. After a reply, `TextGenerationSequenceDisclosure` reports the retained sequence; when its tokens start the next turn's prompt, `ContinueTokens` appends only the new tokens. `Reset` then discards the sequence. |
| `constraint` | A conversation that asks for JSON, a `TokenConstraint` from `TokenizerConstraintFactory.CreateConstraint`, and `TextGenerationConstraintSession.GenerateTokensWithConstraint`. Then the same request through a `ChatCompletionSession`: `TryGetConstraint`, `GetFormatResult`, `GetOutputParser`, the events with their `ChatCompletionEventProvenance`, and `GetTextGenerationResult`. Last, `TextGenerationConstraintContinuation` continues the conversation with the next turn under a new constraint. |
| `speculative` | `TextGenerationSpeculativeSupport` for each method, with and without a draft model. Prompt lookup decoding, then draft model decoding with a second copy of the GGUF file, with `TextGenerationSpeculativeResult` statistics. The consumed configuration reports the stages the session composed, and the sample wires the same stages, with `SetDraftPipeline`, into a configuration of its own and decodes again. |
| `watermark` | `TextGenerationWatermarkConfiguration` with a Gumbel watermark and a fixed key, sampled generation with a fixed seed, `TextGenerationWatermarkResult` statistics, and `TextGenerationWatermarkDetector` with the key (a small p-value) and another key (a large one). A second request opts out with `WatermarkMode = Disabled`. |
| `segments` | `GenerateSegments` with a prompt split into two token segments. |

Each scenario checks the session's capabilities or the `HRESULT` of the call
and prints a line such as `... is not supported by this model/route` when the
model or route does not support a feature, then the sample continues.
Generation is greedy, except in the watermark scenario, which samples with a
fixed seed, so a run prints the same output every time on the same device.
Ctrl+C cancels generation through a `CancellationToken` and skips the
remaining scenarios.

With Qwen2.5 0.5B Instruct, the llama.cpp route on the CPU supports every
scenario, with two limits that the sample reports:

- The model's chat template has no JSON response formats.
  `FormatConversation` with `ResponseFormat = JsonSchema` returns
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`, so the constraint scenario asks for
  the same JSON as the arguments of a tool call the model must make
  (`ToolChoice = Required`), which the template does support.
- The model has no draft layers, so the `Model` speculative method is not
  supported. A draft model and prompt lookup are.

The scenarios do not cover media: `CreateMediaEncoderFromFile`,
`ChatCompletionSession.GenerateWithMedia`, and embedding segments need a
multimodal model and its projector, which the sample catalog does not include.

```text
== continuation ==
...
Reply 1: The three primary colors are red, yellow, and blue.
Tokens: prompt=39 generated=12
After reply 1: session=Ready sequence=Retained evaluated=51 retained=51 capabilities.sequenceState=Retained
The 51 retained tokens start the 68-token prompt; ContinueTokens appends 17.
...
== watermark ==
...
Watermark statistics: algorithm=Gumbel keyId=42 generated=64 watermarked=64 ...
Detect with the key:     scored 64 of 64 tokens, score=151.20 p-value=3.81E-16
Detect with another key: scored 64 of 64 tokens, score=53.79 p-value=0.905
```

## How the sample calls the Runtime

The sample calls the Runtime and Task APIs through the C# sample wrapper in
[`Runtime/shared/csharp/Runtime`](../../../../Runtime/shared/csharp/Runtime/),
which gives their COM-style C API an idiomatic C# surface; see its
[README](../../../../Runtime/shared/csharp/README.md).

- `LlamaModelLoader.LoadModelFromFile` loads the model once, and each scenario
  binds it to a new configuration with `ModelTextGenerationConfiguration.SetModel`,
  because a session consumes its configuration. The task uses the
  configuration's tokenizer.
- A text generation session, the views queried from it, its token
  constraints, and a chat session over it belong to the thread that created
  the session. `TextGenerator` keeps the session on a thread of its own and
  streams tokens with `await foreach`: `GenerateTokensAsync`,
  `ContinueTokensAsync`, and `GenerateAsync`, which starts a generation through
  a session view such as `TextGenerationConstraintSession`.
  `TextGenerator.InvokeAsync` runs the synchronous calls on that thread, such
  as reading `TextGenerationSequenceDisclosure` or the chat completion stream.
- A failure throws a .NET exception that keeps the `HRESULT`, such as
  `NotSupportedException` for `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.
- The project references `Microsoft.Windows.AI.MachineLearning.LibLlama.Core`,
  which copies the llama.cpp engine next to the executable and its CPU backend
  to `LlamaBackends\cpu`.

## Requirements

- The .NET 8 SDK or later.
- The `Microsoft.Windows.AI.MachineLearning` and
  `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` versions pinned in
  `Samples\Directory.Packages.props`. See
  [Package requirements](../../README.md#package-requirements).

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample text-generation-features-csharp -PackageOnly
.\run_text_generation_features.ps1
.\run_text_generation_features.ps1 -Scenario watermark
.\run_text_generation_features.ps1 -ModelPath <model.gguf>
```

The run script uses the cataloged Qwen2.5 0.5B Instruct GGUF file and offers
to download it. On an Arm64 device, pass `-Platform ARM64` to both scripts.
`dotnet build model-backed\text-generation-features\csharp\task-text-generation-features-csharp.csproj -c Release -p:Platform=x64`
builds the same project. The executable is
`out\<platform>\<configuration>\task-text-generation-features-csharp\task-text-generation-features-csharp.exe`:

```text
task-text-generation-features-csharp.exe <model.gguf> [all|continuation|constraint|speculative|watermark|segments]
```

The sample exits with 0 on success, 1 when the model fails to load or a
scenario fails (it prints `ERROR: <scenario> failed: 0x<HRESULT>` and runs the
remaining scenarios), and 2 for invalid arguments.
