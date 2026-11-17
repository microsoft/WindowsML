<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Chat completion in C#

This is the C# version of the [chat completion sample](../README.md). It holds
the same two-turn conversation through the Chat Completion Task, takes the same
arguments, and prints the same output, error messages, and exit codes as the
C++ sample.

The sample builds the Text Generation Task the way the C++ sample's shared
helper does, and the model's file extension selects the route:

| Model | Runtime pipeline | Token input and logits output |
|---|---|---|
| `.gguf` (llama.cpp) | One stage from `WinMLRuntime.LoadModelFromFile`; the tokenizer comes from the GGUF file | Ordinal 0 of the stage |
| `.onnx` (ONNX Runtime) | One stage with state pair (1, 1) and the sequence capacity the model declares | Found by name (`input_ids`, `logits`) with `OrtModelSchema` |
| `.ort` (compiled ONNX Runtime) | One stage with state pair (1, 1) and the sequence capacity the model declares | Ordinal 0 of the stage |

Every route then configures a `TextGenerationConfiguration` with the calls the
package's `winml\tasks\text_generation\TextGeneration.hpp` helper makes, in its
order: `Mode`, `SetPipeline`, `SetTokenInput`, `SetOutput`,
`SetStateOwnerStage`, `SetTokenTarget`, `SetTokenizer`, and `Validate`. A
`ChatGenerator` creates the text generation session and the chat session over
it, and streams each turn's content deltas with `await foreach`. Its
`LastOutcome` gives the reply, token counts, how many prompt tokens the session
reused, and why generation stopped. The ONNX route reports the step that failed,
as the C++ sample does.

```text
User: Name three primary colors.
Assistant: Three primary colors are red, blue, and yellow.
  Tokens: prompt=34 (reused=0) completion=11
  Finished because: end-of-sequence token
User: Which of those is the color of the sky?
Assistant: The color of the sky is blue.
  Tokens: prompt=65 (reused=45) completion=8
  Finished because: end-of-sequence token
```

Ctrl+C cancels a `CancellationToken` that serves both turns. The canceled
reply reports what was generated and `Finished because: canceled`, and the
conversation ends there. The C++ sample has no Ctrl+C handler; Ctrl+C ends it.

## How the sample calls the Runtime

The sample calls the Runtime and Task APIs through the C# sample wrapper in
[`Runtime/shared/csharp/Runtime`](../../../../Runtime/shared/csharp/Runtime/),
which gives their COM-style C API an idiomatic C# surface. The sample code has
no pointers or `HRESULT` checks. The wrapper is sample code, not a supported
API; the [shared README](../../../../Runtime/shared/csharp/README.md) explains
why it exists and what it covers.

- Every object is `IDisposable`, and `As<T>()` stands in for
  `QueryInterface`: `stage.As<StatefulStageOptions>()` declares the state
  pair before `Build`, and `model.As<OrtModelSchema>()` finds the ONNX
  endpoints by name.
- A failure throws a .NET exception that keeps the `HRESULT`, and the sample
  prints it as the C++ sample does: `Chat completion failed: 0x80070002` for a
  missing model file.
- A text generation session belongs to the thread that created it.
  `ChatGenerator` keeps it, and the chat session that wraps it, on one
  dedicated thread and exposes each turn as an `IAsyncEnumerable` that accepts
  a `CancellationToken`. `CloseAsync` closes the sessions and reports a
  failure to close, as the C++ sample's `Close` does.
- The project imports
  [`WinMLSamples.CSharp.props`](../../../../Runtime/shared/csharp/WinMLSamples.CSharp.props),
  which the C# samples share and which compiles the wrapper, and builds into
  `Samples\Tasks\out`.
- The project references `Microsoft.Windows.AI.MachineLearning.LibLlama.Core`,
  as the C++ sample does. The package copies the llama.cpp engine next to the
  executable and its CPU backend to `LlamaBackends\cpu`.

## Requirements

- The .NET 8 SDK or later.
- The `Microsoft.Windows.AI.MachineLearning` and
  `Microsoft.Windows.AI.MachineLearning.LibLlama.Core` versions pinned in
  `Samples\Directory.Packages.props`.
- The text generation sample's models: the GGUF model for `-Backend llama`, or
  the exported Qwen Task graph in `models\llm` for `-Backend ort`. See the
  [chat completion sample](../README.md).

## Build and run

From `Samples\Tasks`:

```powershell
.\build.ps1 -Sample chat-completion-csharp -PackageOnly

.\run_chat_completion.ps1 -Language csharp -Backend llama
.\run_chat_completion.ps1 -Language csharp -Backend ort
```

`dotnet build language\chat-completion\csharp\task-chat-completion-csharp.csproj -c Release -p:Platform=x64`
builds the same project. Use `-Platform ARM64` (or `-p:Platform=ARM64`) on an
Arm64 device. The executable is
`out\<platform>\<configuration>\task-chat-completion-csharp\task-chat-completion-csharp.exe`:

```text
task-chat-completion-csharp.exe <model> <tokenizer-source|-> [question] [follow-up] [max-new-tokens]
```

Pass `-` as the tokenizer source for a GGUF model, which carries its own
tokenizer. The question and follow-up default to the ones above, and
max-new-tokens to 48.
