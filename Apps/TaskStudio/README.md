<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Task Studio

Task Studio is a WinUI 3 desktop app, written in C#, for the Windows ML Task
APIs. It gives each Task a model in the pattern that
[Models-backed Tasks](../../Samples/Tasks/model-backed/README.md) describes, so
Windows ML plans the model into programs for the CGC backend and runs it on the
GPU you pick.

| Tab | What it does |
| --- | --- |
| Chat | Multi-turn chat with the model's own chat template, streamed, with system instructions and prompt starters. |
| Completion | Continues a raw prompt, streamed, with token counts and speculative decoding statistics. |
| Speech | Transcribes an audio file or a microphone recording, or transcribes the microphone live. |
| Decide | Answers typed questions about a text, with a probability for every option. |
| Perf | Measures every generation, runs llama-bench style tests, and compares speculative decoding with sequential decoding. |

Task Studio is a sample app, provided as is. It's complete enough to try models
and measure them on your own GPU, and its code shows how an application uses
the Task APIs from C#.

## Requirements

- Windows on an x64 or Arm64 PC with a Direct3D 12 GPU that the CGC backend
  supports. Models run only on the GPU, and there's no CPU fallback.
- The .NET 8 SDK or newer to build the app. The app is unpackaged and deploys
  the Windows App SDK with itself.
- Models, which you download yourself. [MODELS.md](MODELS.md) lists the models
  tested in this app, with links.

## Build and run

From the root of the repository, for an Arm64 PC:

```powershell
dotnet build Apps\TaskStudio\TaskStudio.csproj -p:Platform=ARM64
& .\Apps\TaskStudio\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\TaskStudio.exe
```

For an x64 PC:

```powershell
dotnet build Apps\TaskStudio\TaskStudio.csproj -p:Platform=x64
& .\Apps\TaskStudio\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\TaskStudio.exe
```

In Visual Studio, open `Apps\TaskStudio\TaskStudio.slnx`, which holds the app
and the [smoke tests](#smoke-tests), and select the ARM64 or x64 platform.

The app builds against the `Microsoft.Windows.AI.MachineLearning` version that
[`Samples\Directory.Packages.props`](../../Samples/Directory.Packages.props)
pins, the same version as the samples. The package supplies the Runtime, the
Tasks, the CGC backend, and the Win32 metadata from which CsWin32 generates the
app's interop code, and NuGet restores the Direct3D 12 Agility SDK with it. To
build against a package that isn't published yet, see
[`localpackages`](localpackages/README.md).

### Package and share

`scripts\Pack-TaskStudio.ps1` publishes a self-contained Release build for each
architecture and writes one zip for each to `Apps\TaskStudio\dist`:

```powershell
.\Apps\TaskStudio\scripts\Pack-TaskStudio.ps1 -Version 0.1.0
```

Each zip holds the app in a `studio` folder, with .NET, the Windows App SDK,
the Windows ML Runtime, and the Direct3D 12 Agility SDK beside
`TaskStudio.exe`, plus a `README.txt` and `MODELS.md`. The person who runs it
needs no .NET installation or build tools, and brings their own models. The
app is unsigned, so if Windows blocks a downloaded zip, unblock it in the zip's
properties before you extract it. `EnableMsixTooling` in the project makes
`dotnet publish` include the app's resources and compiled XAML; without it, the
published app fails at startup.

## How the app uses Windows ML

CsWin32 generates the unmanaged Runtime and Task interfaces that
[`Runtime\NativeMethods.txt`](Runtime/NativeMethods.txt) lists, from the
package's Win32 metadata. `Runtime\TaskRuntime*.cs` owns every COM reference
and runs every native session, stream, and live-input call on one dedicated MTA
thread. `MainWindow` owns the presentation and marshals progress back to the UI
thread. Only the free-threaded cancellation source crosses threads. Shutdown
cancels active work and drains the microphone before native teardown, and
native errors, including their HRESULTs, appear in the UI.

### GPU selection

The app lists the hardware Direct3D 12 adapters with DXCore and selects an
NVIDIA adapter first when there is one. Choose another adapter before you load
models. The app passes the selected adapter to
`IWinMLRuntime::CreateExecutionTargetFromAdapter`, and the Runtime creates its
own Direct3D 12 device on it. After a load, the device card shows the adapter
that the Runtime actually used, resolved from the LUID of its Direct3D 12
device. Release the text, speech, and decision sessions before you change GPUs.
The speech waveform is a host-memory tensor, and the speech model still runs on
the selected GPU.

Finding a GPU doesn't establish that the CGC backend supports its driver or a
given model. A model that the backend can't run fails to load with an error.

### Compiled-program cache

The first session of a model compiles its CGC programs, which can take many
seconds. Text, speech, and decision sessions pass a program cache directory,
`%LOCALAPPDATA%\WindowsMLTaskStudio\CompiledPrograms`, as the
`compiledProgramDirectory` of their model options with
`WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS`. The cache keeps only the compiled
programs, a few MB for each model, and never the prepared weights. Every load
prepares the weights again from the model's own files, and nothing is written
next to the model. Reusing the programs makes later loads much faster. On an
NVIDIA RTX Spark, the text smoke test of Qwen3-0.6B took 55 seconds with an
empty cache and 14 seconds with the cached programs.

A model that can't reuse compiled programs refuses the directory with
`ERROR_NOT_SUPPORTED`, as Whisper checkpoints do. The app then compiles for
each session without a cache, and the status line says which happened. To force
recompilation, open **Settings** (the gear at the bottom of the sidebar), expand
**Advanced**, and select **Delete program cache**, which is available when no
sessions are loaded. You can also delete the directory.

## Text: Chat and Completion

Choose a model **folder** that contains `config.json` and safetensors weights,
with the model's `tokenizer_config.json` and tokenizer files. When you browse
to a model folder, or type its path and leave the tokenizer empty, the app
selects its tokenizer configuration.

```text
Model:     C:\models\Qwen2.5-0.5B-Instruct
Tokenizer: C:\models\Qwen2.5-0.5B-Instruct\tokenizer_config.json
```

**Create task sessions** loads the model with
`IWinMLModelTextGenerationConfiguration::SetModel` on the selected GPU,
validates the configuration, and creates the text generation session. The
**Context window** list defaults to **Model default**, which skips
`SetLogicalCapacity`, so the session keeps the model's own sequence length. A
size from 512 to 32,768 tokens calls `SetLogicalCapacity` with it; a smaller
window uses less GPU memory. Chat layers a chat completion session over the
text generation session with the same tokenizer, and uses the tokenizer's
structured formatter with the model's own chat template and output parser.
Completion takes a raw prompt and applies no chat template.

### GGUF files

Chat and Completion can also load a single **`.gguf` file** with the file
button next to the folder button. Leave the tokenizer empty to use the
tokenizer and chat template stored in the file. The app loads the file with
`IWinMLRuntime::LoadModelFromFile`, calls `SetModel` without a tokenizer, and
creates the Tasks with the configuration's `GetTokenizer`. GGUF files have no
`generation_config.json`, so sampling starts from the Task defaults.

Windows ML plans a GGUF file into CGC programs like a checkpoint folder. A file
whose architecture or tensor types Windows ML doesn't support fails with
`ERROR_NOT_SUPPORTED` (0x80070032). This app never runs a model on llama.cpp;
to use llama.cpp for a GGUF file, see the
[llama.cpp text generation sample](../../Samples/Tasks/model-backed/llama-text-generation/).
[MODELS.md](MODELS.md) lists the files tested in this app. The **?** button
beside MODEL summarizes what each tab can load and links to that list.

Sampling, streaming, **Stop**, reset, and unload are Task API operations. The
app doesn't implement its own inference or token generation loop. The
conversation is kept in memory, and a failed or canceled turn isn't committed.
Switching between Chat and Completion starts a new conversation.

### Advanced generation

**Advanced generation** in the sidebar sets top-k, top-p, min-p, the repetition
penalty and its token window, the presence and frequency penalties, the full
64-bit sampling seed, and optional stop-token IDs. The settings apply to Chat
and Completion on the next request, without reloading the model. Selecting a
model folder or creating its sessions loads the supported settings from the
model's **`generation_config.json`**. The Task API resolves an omitted request
field to a fixed API default, not to the model's sampling configuration, so the
app maps the model's defaults explicitly.

The supported keys are `max_new_tokens`, `temperature`, `top_k`, `top_p`,
`min_p`, `repetition_penalty`, `repetition_penalty_window`,
`presence_penalty`, `frequency_penalty`, `seed`, `eos_token_id` (one ID or an
array), and `stop_token_ids` (an array). `do_sample: false` selects temperature
0 (greedy), and `do_sample: true` without a temperature uses 1. Other missing or
null settings use the Task defaults. The output limit defaults to 256 tokens as
an app policy, and `max_length` isn't treated as `max_new_tokens`. A setting
outside the app's output limit of 1 to 32,768 tokens is reported as an error,
and so is a malformed configuration file.

The sidebar shows where the settings came from. The sliders widen their ranges
to keep a model's valid defaults beyond the initial range. **Restore model
defaults** resets sampling and stop IDs to the loaded model's values, without
changing the model, the context window, the system instructions, or the
conversation.

Temperature 0 selects greedy decoding, and the probability filters and the seed
matter only for random sampling. A fixed seed repeats the same random draws,
but the text can still differ between runs when the GPU computes slightly
different scores at a near tie. Stop-token IDs are unsigned integers from the
selected tokenizer, separated by commas or spaces, not stop strings. The
model's end-of-sequence IDs from its configuration are added as stop IDs, and
the tokenizer's end-of-sequence policy stays enabled.

### Speculative decoding

**Speculative decoding** under the context window selects how draft tokens are
proposed when the app creates the sessions. The session verifies the drafts in
one step and keeps only the tokens that its own sampling produces, so the method
changes the speed, not the sampling procedure. The app uses only the Task API.
It sets `WINML_TEXT_GENERATION_MODEL_OPTIONS::draftModel` at `SetModel` and
calls `IWinMLTextGenerationSpeculativeConfiguration::SetSpeculativeOptions`
before `CreateSession`. It reads what the model supports from
`IWinMLTextGenerationSpeculativeSupport`, and the statistics of each stream
from `IWinMLTextGenerationSpeculativeResult`.

| Option | Task API method | Works with |
| --- | --- | --- |
| Off | `NONE` | Every model |
| Prompt lookup | `PROMPT_LOOKUP`, which drafts tokens copied from earlier prompt and reply text | Models whose state can roll back, such as Qwen3 |
| Draft layers | `MODEL`, which drafts with the multi-token prediction (MTP) layers that the model carries | Models with MTP layers, such as Qwen3.5-9B |
| Drafter model | `MODEL` with a companion drafter bound at `SetModel` | A drafter trained for the model |

A method that the Runtime reports as unsupported for the model fails the load
and lists the supported methods. Load the drafter the same way as the model, as
a folder. The Runtime can still refuse a drafter when it creates the session
and reads the drafter's weights, and the app then says so. The Connected model
card shows the method and what the model supports. Completion replies show the
drafted and accepted tokens; the Chat result doesn't report them.

The **Speculative decoding: Off vs on** card on the Perf tab runs greedy
prompts with speculation off and then with the loaded method, compares the
text, and reports the decode rate, drafted and accepted tokens, acceptance, and
tokens per verification step. Where the outputs differ, it reports how many
characters matched first. **Prompts used** shows the exact prompts.

- **Prompts.** *Short* runs three small prompts, which rewrite a function,
  explain a GPU, and count to 40. *Coding* gives a coding assistant workload. A
  frozen snapshot of this app's C# source (`BenchmarkData\coding`) is packed as
  `### File: <path>` code blocks up to 4K, 8K, or 16K tokens, or all of it,
  counted with the model's tokenizer, and followed by one of three tasks. The
  tasks add documentation comments to a file and output it in full, explain how
  a model is loaded, or write unit tests.
- **Generate.** 128 to 32,768 tokens for each prompt. Load the model with a
  context window that holds the prompt and these tokens.
- **End of text.** *Ignore* uses a Task with the `EXPLICIT` end-of-sequence
  policy and no tokens, so every prompt generates the full count, as llama-bench
  does. Text after the end token often repeats, which inflates acceptance, so
  use *Stop* for realistic numbers.

A configuration is consumed by the session it creates, so the comparison binds
a new one to the loaded model for each method and closes the old sessions
first. It resets the chat conversation.

Measured with `TaskStudio.SmokeTests --spec` on an NVIDIA RTX Spark, greedy,
128 tokens for each of the three short prompts, with the decode rate over all
three prompts:

| Model | Method | Off (tokens/s) | On (tokens/s) | Speedup | Drafts accepted (rewrite code, explain, count) |
| --- | --- | ---: | ---: | ---: | --- |
| Qwen3-0.6B | Prompt lookup | 168.8 | 254.5 | 1.51x | 96%, 66%, 14% |
| Qwen3.5-0.8B | Draft layers (MTP) | 113.9 | 118.9 | 1.04x | 74%, 37%, 100% |
| Qwen3.5-9B | Draft layers (MTP) | 15.3 | 25.1 | 1.64x | 92%, 49%, 100% |

The gain depends on the text. It's largest when the drafts are predictable,
such as code that repeats its input, and smallest for free prose. A
verification step evaluates several positions at once, which can differ
numerically from single steps, so greedy text can diverge after a near tie.

## Speech: recorded and live

The Speech tab has its own model and tokenizer, for example:

```text
Model:     C:\models\whisper-tiny
Tokenizer: C:\models\whisper-tiny\tokenizer_config.json
Language:  empty for the model default, or a supported BCP-47 tag
```

- **Recorded audio.** Import a WAV, MP3, M4A, FLAC, WMA, or AAC file through
  the installed Media Foundation decoders, or select **Record clip** to record
  the microphone, then select **Transcribe clip**. Recording and importing work
  before a model is loaded, and you can save a clip as a WAV file.
- **Live microphone.** **Start live transcription** is available only when the
  session exposes `IWinMLAutomaticSpeechRecognitionLiveSession`. Microphone
  chunks go directly to the native live input, not to a recording that's
  transcribed afterward. Text appears when the model commits it. A model that
  transcribes fixed windows of audio can wait for a full window, or for
  **Finish live input**.
- Audio is converted to mono float32 at 16 kHz. Clips are limited to 120
  seconds. Live capture has a bounded five-second queue and fails with an error
  if the recognizer can't keep up; it never drops audio silently.

The app records from the default microphone and has no microphone picker.
Windows asks whether to let the app use the microphone the first time it
records, and the Windows privacy settings must let desktop apps access the
microphone.

## Decide: typed decisions

The Decide tab uses the typed decision Task (`IWinMLDecisionTaskFactory`) with a
typed decision model on the selected GPU, like the
[typed decisions sample](../../Samples/Tasks/model-backed/decision/). The tested
model is the root checkpoint of
[convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya); see
[MODELS.md](MODELS.md#decide) for its files.

```text
Model: C:\models\laya
```

The app creates the tokenizer from the model's `tokenizer` folder, then calls
`LoadModelFromPath`, `CreateDecisionTaskFromModel`, and `CreateSession`. A
request is a **state**, which is any text, and typed questions:

| Kind | Options in the editor |
| --- | --- |
| Choice | One `label: description` per line. The description is optional. |
| Score | One criterion for each level, lowest first. |
| Yes / No | None, or two lines, the description of *no* and then of *yes*. |

Each answer shows the selected option, a probability bar for every option, the
confidence (calibrated when the model reports it), the distribution confidence,
the model's act estimate, the expected level for a score, and an **ACT** or
**ESCALATE** recommendation. An optional act threshold for each question makes
the recommendation depend on the confidence. There are three editable examples,
a support ticket, a product review, and an email triage. Nothing is generated,
and each question is one evaluation. The first request that's longer than any
before it prepares a larger program once, so it's slower.

## Perf: generation stats

The Perf tab uses the text model loaded for Chat and Completion. The app
measures every generation itself as it reads the Task stream, so no
administrator rights or trace session are needed.

| Stat | How it's measured |
| --- | --- |
| Time to first token | From the `GenerateText` or `Generate` call to the first token (Completion) or the first text delta (Chat) |
| Decode tok/s, ms/token | `(generated - 1)` tokens between the first and the last token |
| Prompt to first token | Prompt tokens divided by the time to the first token, including prompt encoding |
| Total | From the request call to the end of the stream, with the overall tokens per second |
| Prompt cache | `IWinMLChatCompletionResult::GetPromptCacheUse`, shown only when the Runtime reports a mode |
| GPU memory | This process's dedicated memory on the adapter, from the DXCore `AdapterMemoryBudget` |
| Session load | The time to create the text sessions |

Every Chat and Completion generation is added to the history. **Run
benchmark** works like llama.cpp's `llama-bench`:

| Option | llama-bench | Meaning |
| --- | --- | --- |
| Prompt (pp) | `-p` | Evaluate a prompt of N tokens. t/s is N divided by the time to its first token. |
| Generate (tg) | `-n` | Generate N tokens from a one-token prompt. t/s is N divided by the time to the last token. |
| Depth (d) | `-d` | Tokens already in the context before each test, evaluated untimed with `GenerateTokens`. The test then runs with `ContinueTokens`. |
| Repetitions (r) | `-r` | Measured runs for each test |
| Warm-up | warm-up run | Unmeasured runs for each test, 2 by default, because the first runs after a load are slower |

The tests use greedy sampling and ordinary text tokens from the model's
tokenizer. The results are the mean and sample standard deviation in tokens
per second, and **Copy as Markdown** copies them as a llama-bench style table.
The Task API has no per-request way to ignore the end of sequence, so a tg run
that ends early says so. The context window must hold the depth, the prompt,
and the generated tokens. **Copy as CSV** exports the history, and **Clear**
empties it and the results. A Completion read returns one token, so its timing
is per token. A Chat read returns a parser delta, and the parser can hold text
back, so a Chat turn can have no decode rate.

## Smoke tests

The `TaskStudio.SmokeTests` console project checks the same Runtime service that
the app uses, without the UI. Build it like the app, and pass model paths:

```powershell
dotnet build Apps\TaskStudio\SmokeTests\TaskStudio.SmokeTests.csproj -p:Platform=ARM64
$smoke = '.\Apps\TaskStudio\SmokeTests\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\TaskStudio.SmokeTests.exe'
& $smoke --devices
& $smoke --defaults
& $smoke --conversation C:\models\Qwen3-0.6B\tokenizer_config.json
# A GGUF file's tokenizer against the publisher's tokenizer, for encoding and chat formatting
& $smoke --tokparity C:\models\qwen2.5-0.5b-instruct-fp16.gguf C:\models\Qwen2.5-0.5B-Instruct\tokenizer_config.json
& $smoke --encode C:\models\Qwen3-0.6B\tokenizer_config.json "Hi <|im_end|> there"
& $smoke C:\models\Qwen2.5-0.5B-Instruct C:\models\Qwen2.5-0.5B-Instruct\tokenizer_config.json
& $smoke C:\models\Llama-3.2-1B-Instruct-Q4_0.gguf
& $smoke --audio
& $smoke --audio --microphone
& $smoke --speech C:\models\whisper-tiny C:\models\whisper-tiny\tokenizer_config.json C:\path\to\speech.wav
& $smoke --decision C:\models\laya
& $smoke --bench C:\models\Qwen3-0.6B-Q4_0.gguf
# Speculative decoding with lookup, layers, or companion <drafter>
#   [--context 4096|8192|16384|32768] [--tokens N] [--no-eos]
& $smoke --spec C:\models\Qwen3-0.6B C:\models\Qwen3-0.6B\tokenizer_config.json lookup
& $smoke --spec C:\models\Qwen3.5-9B C:\models\Qwen3.5-9B\tokenizer_config.json layers
& $smoke --spec C:\models\Qwen3.5-9B C:\models\Qwen3.5-9B\tokenizer_config.json layers --context 8192 --tokens 1024
```

The text checks cover Completion, two Chat turns, reset, cancellation during a
stream and before it starts, recovery, seeded advanced sampling in both modes
(two of three runs with the same seed must match), explicit stop tokens,
rejection of invalid options, and unload and reload. The
audio checks cover decoding, downmixing and resampling, a WAV round trip, and
optionally a real microphone capture. The speech checks cover cancellation
before a stream starts, recorded transcription, native live chunks, and ordered
cleanup of the stream and the input. The decision checks ask three questions
about a support ticket, require the expected answers (billing, blocking, and a
churn risk), and cover act thresholds, rejection with `E_INVALIDARG`,
cancellation, and unload and reload.
The speculative checks report the Runtime's support for each method, require
verification steps when the method is on and none when it's off, compare the
greedy text with Off, and check that the loaded method is restored. A native
error or a missing transcript or answer fails the process.

## Limitations

This is a sample app for local models, not a model catalog. It doesn't download
models, save conversations, run on an NPU, call tools, take images, or inspect
tokenizers. Models stay in their own folders. Loading a larger model can need
significant compilation time and GPU memory. See
[MODELS.md](MODELS.md#known-issues) for known issues with specific models.
