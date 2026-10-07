<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Windows ML Task API samples

The Task API adds typed, task-level interfaces to the Windows ML Runtime. A
text-generation Task streams text and reports token counts and why generation
stopped; a speech-recognition Task turns a waveform into a transcript. The
application still creates the Runtime, execution targets, models, and
pipelines, so a Task runs on the same backends and hardware as the
[Runtime samples](../Runtime/README.md): ONNX models through ONNX Runtime and
GGUF models through llama.cpp.

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
| Language | [Text generation](language/text-generation/) | C++, Python | Stream text from an ONNX, ORT, or GGUF model and print token counts and the finish reason. GGUF models can use speculative decoding. The C++ sample also prints timing and context capacity. |
| Language | [Chat completion](language/chat-completion/) | C++, Python | Hold a two-turn conversation. The application keeps the message history; the Task applies the model's chat template, streams each reply, and reports how many prompt tokens it reused. |
| Speech | [Automatic speech recognition](speech/automatic-speech-recognition/) | C++, Python | Bind Whisper encoder and decoder pipelines to the typed ASR Task and transcribe a waveform. |
| Composition | [Speech to text generation](composition/speech-to-text-generation/) | C++, Python | Send an ASR transcript to a separate text-generation Task as a chat turn. |

The composition sample connects two Tasks in application code; it does not
create a combined model or pipeline.

To serve a text-generation model to coding agents and other OpenAI-compatible
clients, see the [Server samples](../Server/README.md).

## Build

```powershell
.\build.ps1 -Sample all -Configuration Release -PackageOnly
```

Builds default to the host architecture, restore the
`Microsoft.Windows.AI.MachineLearning` package from nuget.org or the
[`localpackages`](localpackages/README.md) override directory, and do not
download models. The GGUF samples also reference
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core`, which deploys the CPU
llama.cpp backend; see
[C++ payload deployment](../../docs/Runtime/gguf-models.md#c-payload-deployment).
Running a GGUF model on a GPU needs a backend that you build yourself; see
[GPU backends](../../docs/Runtime/gguf-models.md#gpu-backends).
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
  --expected-windowsml-version <windowsml-version>
```

Pass the `windowsml` package version that corresponds to the package pinned in
[`Samples\Directory.Packages.props`](../Directory.Packages.props). The script
also checks that the installed `onnxruntime-windowsml` version is the one
`windowsml` requires, and that an installed `windowsml-llama-core` wheel has the
same version as `windowsml`. Add `--require-llama` before a GGUF flow to fail
when the `windowsml-llama-core` wheel is missing.
