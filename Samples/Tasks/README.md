<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Task API samples

The Task API adds typed, task-level interfaces to the Windows ML Runtime. A
text-generation Task streams text and reports token counts and why generation
stopped; a speech-recognition Task turns a waveform into a transcript, and a
decision Task returns structured answers and probabilities. The application
creates the Runtime, execution targets, models and tokenizers. Models-backed
Tasks take a model and run it: published weights that Windows ML plans for the
CGC backend, a composition the application describes, or a GGUF file the
application loads with llama.cpp. Caller-composed Tasks also let applications build pipelines
with the backends used by the [Runtime samples](../Runtime/README.md).

> [!NOTE]
> The Runtime and Task APIs are new and still evolving, and they may change.
> Report issues and share feedback through
> [GitHub Issues](https://github.com/microsoft/windowsml/issues).

Start with [Task and Runtime lifecycle](../../docs/Tasks/task-lifecycle.md) to see which
objects the application owns, what a typed Task adds, and how pull streams,
cancellation, and final results fit together. The interfaces are documented in
the [Runtime API reference](../../docs/api-reference/README.md).

| Area | Sample | Languages | What it demonstrates |
| --- | --- | --- | --- |
| Models-backed | [Text generation](model-backed/text-generation/) | C++, Python | Run published weights, a checkpoint folder or a GGUF file, with no pipeline and no tokenizer argument, and stream a reply. |
| Models-backed | [Composed text generation](model-backed/composed-text-generation/) | C++, Python | Describe an exported decoder as a composition with `SetComposition` and stream a reply. |
| Models-backed | [llama.cpp text generation](model-backed/llama-text-generation/) | C++, Python | Load a GGUF file with the llama.cpp loader, optionally with a draft model for speculative decoding. |
| Models-backed | [Speech recognition](model-backed/automatic-speech-recognition/) | C++, Python | Bind a speech model, set language/mode and stream a transcript. |
| Models-backed | [Typed decisions](model-backed/decision/) | C++, Python | Ask a choice question and read probabilities, confidence and a recommendation. |
| Language | [Text generation](language/text-generation/) | C++, Python | Stream text from an ONNX, ORT, or GGUF model and print token counts and the finish reason. GGUF models can use speculative decoding. The C++ sample also prints timing and context capacity. |
| Language | [Chat completion](language/chat-completion/) | C++, Python | Hold a two-turn conversation. The application keeps the message history; the Task applies the model's chat template, streams each reply, and reports how many prompt tokens it reused. |
| Speech | [Automatic speech recognition](speech/automatic-speech-recognition/) | C++, Python | Bind Whisper encoder and decoder pipelines to the typed ASR Task and transcribe a waveform. |
| Composition | [Speech to text generation](composition/speech-to-text-generation/) | C++, Python | Send an ASR transcript to a separate text-generation Task as a chat turn. |

The composition sample connects two Tasks in application code; it does not
create a combined model or pipeline.

Start with [Models-backed Tasks](model-backed/README.md). These compact C++ and
Python examples show the three ways to give a Task a model: published weights,
an application-described composition, and an explicit llama.cpp model. The
other samples above keep the hand-wired caller-composed paths.

To serve a text-generation model to coding agents and other OpenAI-compatible
clients, see the [Server samples](../Server/README.md).

## Build

```powershell
.\build.ps1 -Sample all -Configuration Release -PackageOnly
```

Every sample builds against the package versions pinned in
[`Samples\Directory.Packages.props`](../Directory.Packages.props). The
Models-backed Task samples need Windows ML 2.9 or later; see
[their package requirements](model-backed/README.md#package-requirements).
The C++ samples need Visual Studio with Desktop development with C++. The C#
model-backed text generation sample builds with the .NET 8 SDK or later;
without it, `-Sample all` skips that sample with a warning.

Builds default to the host architecture, restore the
`Microsoft.Windows.AI.MachineLearning` package from nuget.org or the
[`localpackages`](localpackages/README.md) override directory, and do not
download models. The GGUF samples also reference
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core`, which deploys the CPU
llama.cpp backend; see
[C++ payload deployment](../../docs/Runtime/gguf-models.md#c-payload-deployment).
Running a GGUF model on a GPU from C++ needs a backend that you build yourself;
see [GPU backends](../../docs/Runtime/gguf-models.md#gpu-backends).
Pass `-Platform ARM64` or `-Platform x64` consistently to both
build and run scripts when targeting a different architecture.

## Prepare and run

```powershell
.\check_artifacts.ps1 -Sample task-text-generation-gguf
.\run_text_generation.ps1 -Backend llama
.\run_chat_completion.ps1 -Backend llama

.\check_artifacts.ps1 -Sample task-text-generation
.\run_text_generation.ps1 -Backend ort
.\run_text_generation.ps1 -Backend hybrid-ort
.\run_chat_completion.ps1 -Backend ort

.\check_artifacts.ps1 -Sample task-automatic-speech-recognition
.\run_automatic_speech_recognition.ps1

.\check_artifacts.ps1 -Sample task-speech-to-text-generation-gguf
.\run_speech_to_text_generation.ps1 -LanguageBackend llama

.\check_artifacts.ps1 -Sample task-speech-to-text-generation
.\run_speech_to_text_generation.ps1 -LanguageBackend ort
```

The Models-backed Task samples have their own checks and run scripts, described in
[Models-backed Tasks](model-backed/README.md#run-the-c-samples):

```powershell
.\check_artifacts.ps1 -Sample task-model-text-generation
.\run_model_text_generation.ps1
.\run_composed_text_generation.ps1
.\run_llama_text_generation.ps1
.\run_model_speech_recognition.ps1
.\run_decision.ps1
```

`check_artifacts.ps1` reads `Samples\shared\SampleArtifacts.psd1`, checks
whether the required files are present under `Samples\Tasks\models`, and prints
the commands that download or prepare anything missing. The ONNX path uses the
exported Qwen Task graphs and tokenizer in `models\llm`.

The `hybrid-ort` backend uses the Task API's prefill/decode configuration:
`task_model.onnx` processes the full prompt, then `task_decode.onnx` decodes one
token at a time. The C++ sample prints the prefill and decode placements it uses.

## Python

Set up Python as described in
[Python samples](../../docs/Runtime/python-samples.md#set-up-python). The Python
samples follow the same flow as the C++ samples:

```powershell
python.exe .\language\text-generation\main.py `
  .\models\llm\task_model.onnx `
  --prompt "The sky is often" --max-new-tokens 8

python.exe .\language\chat-completion\main.py .\models\llm\task_model.onnx

python.exe .\speech\automatic-speech-recognition\main.py `
  ..\Runtime\speech\whisper\test_audio.wav `
  --model-dir .\models\whisper-medium-q4f16\onnx

python.exe .\composition\speech-to-text-generation\main.py `
  ..\Runtime\speech\whisper\test_audio.wav `
  --speech-model-dir .\models\whisper-medium-q4f16\onnx `
  --language-model .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf
```

Before running a Python GGUF flow, install the llama.cpp backend with
`python.exe -m pip install --pre windowsml-llama-core`. See the
[GGUF setup instructions](../../docs/Runtime/gguf-models.md#python-payload-install).

To check the installed packages, run:

```powershell
python.exe .\scripts\verify_python_environment.py `
  --expected-windowsml-version $windowsmlVersion
```

`$windowsmlVersion` is the `windowsml` version that corresponds to the package
pinned in [`Samples\Directory.Packages.props`](../Directory.Packages.props); see
[Matching wheel version](../Runtime/localpackages/README.md#matching-wheel-version). The script
also checks that the installed `onnxruntime-windowsml` version is the one
`windowsml` requires, and that an installed `windowsml-llama-core` wheel has the
same version as `windowsml`. Add `--require-llama` before a GGUF flow to fail
when the `windowsml-llama-core` wheel is missing.
