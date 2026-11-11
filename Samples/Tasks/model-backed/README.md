<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Models-backed Tasks

These samples give a Task a model instead of a pipeline. How the app loads the
model decides how Windows ML runs it: Windows ML does not choose among engines
for the app and never falls back from one to another.

> [!NOTE]
> These samples use APIs that are new in Windows ML 2.9. The package pinned in
> [`Samples\Directory.Packages.props`](../../Directory.Packages.props) includes
> them. See [Package requirements](#package-requirements).

## Three calling patterns

| Pattern | The app | Use it when | Samples |
| --- | --- | --- | --- |
| Bring your own weights | Loads published weights with `IWinMLRuntime::LoadModelFromPath` (a checkpoint folder) or `LoadModelFromFile` (a GGUF file), then calls `SetModel`. | You have a published model that Windows ML supports. Windows ML plans it into programs for the CGC backend on the GPU target and creates its tokenizer from the model's files. | [Text generation](text-generation/), [speech recognition](automatic-speech-recognition/), [typed decisions](decision/) |
| Bring your own model | Loads the models it built, sets their tokenizer with `SetTokenizer`, then describes them with `SetComposition`. | You exported your own graphs and know their inputs, outputs and state. | [Composed text generation](composed-text-generation/) |
| llama.cpp | Loads a GGUF file with `IWinMLLlamaModelLoader::LoadModelFromFile` (Python `Runtime.load_llama_model`), then calls `SetModel`. | You want llama.cpp for a GGUF file, for example a model that Windows ML does not plan, or speculative decoding with a draft model. Text generation only. | [llama.cpp text generation](llama-text-generation/) |

For full control over stages, tensor bindings and backend options, wire the
configuration by hand as the [language samples](../language/) do. The
composed sample uses the same exported model as the hand-wired
[text generation sample](../language/text-generation/), so the two can be
compared side by side.

After binding, every pattern produces the same `IWinMLTextGenerationSession`:
streaming, cancellation, token counts and finish reasons work the same way.

## Samples

| Sample | Pattern | Languages | What it shows |
| --- | --- | --- | --- |
| [Text generation](text-generation/) | Bring your own weights | C++, Python | A checkpoint folder or a GGUF file, no tokenizer argument, a streamed chat reply and an optional program cache. |
| [Composed text generation](composed-text-generation/) | Bring your own model | C++, Python | `SetTokenizer`, then a `UNIFIED` composition of an exported ONNX decoder with an explicit state pair. |
| [llama.cpp text generation](llama-text-generation/) | llama.cpp | C++, Python | The llama.cpp loader, and speculative decoding with a draft model loaded the same way. |
| [Speech recognition](automatic-speech-recognition/) | Bring your own weights | C++, Python | A Whisper checkpoint folder with its tokenizer, streamed into a transcript, with an optional language. |
| [Typed decisions](decision/) | Bring your own weights | C++, Python | A choice question answered with probabilities, confidence and a recommendation. No tokenizer argument, and an optional program cache. |

## Package requirements

The Models-backed Task APIs are new in Windows ML 2.9. The samples build against the
`Microsoft.Windows.AI.MachineLearning` and
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core` versions pinned in
[`Samples\Directory.Packages.props`](../../Directory.Packages.props), which
include them. An older package does not, and changing only its version number
does not enable them.

```powershell
.\build.ps1 -Sample model-text-generation -PackageOnly
```

The llama.cpp sample also references
`Microsoft.Windows.AI.MachineLearning.LibLlama.Core`, which deploys the llama.cpp
engine and its CPU backend beside the app:

```powershell
.\build.ps1 -Sample llama-text-generation -PackageOnly
```

For Python, install the `windowsml` wheel that matches the pinned Runtime
package. The composed sample also needs ONNX Runtime (the `with-ort` extra), the
speech sample needs NumPy and the llama.cpp sample needs the
`windowsml-llama-core` wheel of the same version, which carries the llama.cpp
engine and its CPU backend. From `Samples\Tasks`:

```powershell
$runtimeVersion = (Select-Xml -Path ..\Directory.Packages.props -XPath '//WinMLRuntimePackageVersion').Node.InnerText
$windowsmlVersion = $runtimeVersion -replace '-dev(-g[0-9a-f]+)?$', '.dev0' -replace '-experimental$', 'a0'
python.exe -m pip install "windowsml[with-ort]==$windowsmlVersion" "windowsml-llama-core==$windowsmlVersion" numpy
```

Because the command names exact `windowsml` versions, pip accepts those
pre-releases without `--pre` and keeps the other dependencies on their stable
releases.

To use packages that aren't published yet, see
[Local package override](#local-package-override).

## Models

The samples never download models. `check_artifacts.ps1` reports which files a
sample needs, verifies their size and SHA-256, and prints the commands that
download anything missing so you can review each source first. The model files
are published by third parties, not by Microsoft; review each publisher's terms
before you use a model.

```powershell
.\check_artifacts.ps1 -Sample task-model-text-generation
```

| Check with `-Sample` | Source | Files | Folder under `Samples\Tasks\models` |
| --- | --- | --- | --- |
| `task-model-text-generation` | [Qwen/Qwen2.5-0.5B-Instruct](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct) | `config.json`, `model.safetensors`, `tokenizer.json`, `tokenizer_config.json` | `qwen2.5-0.5b-instruct` |
| `task-model-text-generation-gguf`, `task-llama-text-generation` | [Qwen/Qwen2.5-0.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF) | `qwen2.5-0.5b-instruct-q4_k_m.gguf` | `gguf` |
| `task-composed-text-generation` | Exported locally from [Qwen/Qwen2.5-0.5B-Instruct](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct) | `task_model.onnx`, `tokenizer.json` | `llm` |
| `task-model-speech-recognition` | [openai/whisper-tiny](https://huggingface.co/openai/whisper-tiny) | `config.json`, `generation_config.json`, `model.safetensors`, `tokenizer.json`, `tokenizer_config.json` | `whisper-tiny` |
| `task-decision` | [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya) | `config.json`, `rl_agent_config.json`, `model.safetensors`, `encoder\config.json`, `tokenizer\tokenizer.json`, `tokenizer\tokenizer_config.json` | `laya` |

The composed sample uses the ONNX decoder that the
[LLM export script](../../Runtime/language/llm-chat/export_model.ps1) writes;
`check_artifacts.ps1` prints its command. The speech sample transcribes the
[test clip](../../Runtime/speech/whisper/test_audio.wav) of the Runtime samples
unless you pass another WAV file.

Other models work when Windows ML supports them. Text generation supports
dense decoder models such as Qwen2.5, Qwen3 and Llama 3.2, as folders of
unquantized safetensors weights or as GGUF files. A model that Windows ML does
not support fails with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`; see
[Errors](#errors).

## Run the C++ samples

Run from `Samples\Tasks`. Each script checks its models, then runs the sample
built for the host architecture. Pass `-ModelPath` to use your own model.

```powershell
.\run_model_text_generation.ps1
.\run_model_text_generation.ps1 -Format gguf
.\run_composed_text_generation.ps1
.\run_llama_text_generation.ps1
.\run_llama_text_generation.ps1 -ModelPath <model.gguf> -DraftModel <draft.gguf>
.\run_model_speech_recognition.ps1
.\run_decision.ps1 -State "The app closes every time I open the settings page."
```

The executables are under `out\<platform>\<configuration>\<project-name>`:

| Sample | Arguments |
| --- | --- |
| `task-model-text-generation` | `<checkpoint-folder or model.gguf> [prompt] [max-new-tokens] [--capacity <tokens>] [--program-cache <directory> [--cache-weights]]` |
| `task-composed-text-generation` | `<task_model.onnx> <tokenizer.json> [prompt] [max-new-tokens] [--device cpu or gpu]` |
| `task-llama-text-generation` | `<model.gguf> [prompt] [max-new-tokens] [--draft-model <draft.gguf>] [--device cpu or gpu]` |
| `task-model-speech-recognition` | `<model-folder> <tokenizer.json> <input.wav> [--language <tag>] [--mode transcribe or translate]` |
| `task-decision` | `<model-folder> [state] [--program-cache <directory> [--cache-weights]]` |

## Run the Python samples

Run from `Samples\Tasks` with the `python.exe` that has the packages above:

```powershell
python.exe .\model-backed\text-generation\main.py .\models\qwen2.5-0.5b-instruct
python.exe .\model-backed\text-generation\main.py .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf
python.exe .\model-backed\composed-text-generation\main.py `
  .\models\llm\task_model.onnx .\models\llm\tokenizer.json
python.exe .\model-backed\llama-text-generation\main.py `
  .\models\gguf\qwen2.5-0.5b-instruct-q4_k_m.gguf
python.exe .\model-backed\automatic-speech-recognition\main.py `
  .\models\whisper-tiny ..\Runtime\speech\whisper\test_audio.wav
python.exe .\model-backed\decision\main.py .\models\laya
```

Each script accepts `--help`. The text samples accept `--prompt` and
`--max-new-tokens`; the decision sample accepts `--state`, `--question` and
`--options`. The text generation and decision samples accept
`--program-cache <directory>` and `--cache-weights`, as do their run scripts as
`-ProgramCache` and `-CacheWeights`.

## Errors

| Error | Where | What to do |
| --- | --- | --- |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | `SetModel`, `CreateDecisionTaskFromModel` | Windows ML does not support this model for the Task. Use a supported model. For a GGUF file, you can instead load it deliberately with the llama.cpp loader. |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | `CreateSession` | The execution target cannot run the model or the options, for example a GPU target for llama.cpp without a GPU backend. |
| `E_NOINTERFACE` | `QueryInterface` for `IWinMLModelTextGenerationConfiguration` or `IWinMLLlamaModelLoader` | The deployed package does not include the API. In Python, `Runtime.load_llama_model` raises `NotSupportedError`. |
| `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)` | First `CreateSession` of a llama.cpp model | The llama.cpp payload is not deployed: loading the model and `SetModel` succeed without it, and the first session loads it. Reference `Microsoft.Windows.AI.MachineLearning.LibLlama.Core`, or for Python install `windowsml-llama-core` of the same version as `windowsml`. |
| `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)` or `ERROR_PATH_NOT_FOUND` | Loading or `SetModel` | A model file is missing. Check the folder against the [Models](#models) table. |

The samples print this guidance with the error. Python raises
`windowsml.runtime.WinMLError`; its `hresult` attribute holds the code.

## Developer notes

- Without `SetTokenizer`, `SetModel` creates the model's own tokenizer from its
  files: the GGUF file itself, or `tokenizer.json` and `tokenizer_config.json`
  in the checkpoint folder. Create the task with the configuration's tokenizer
  (`GetTokenizer`, Python `configuration.tokenizer`).
- A composition has no tokenizer files. Call `SetTokenizer` before
  `SetComposition`, and create the task with the same tokenizer.
- Call `SetLogicalCapacity` after `SetModel` or `SetComposition`. Without it a
  Models-backed Task session holds the model's default sequence length. The composed
  sample sets the capacity that its exported model declares.
- The first session compiles CGC programs for the GPU, which takes several
  seconds. Pass a program cache directory (`compiledProgramDirectory`, Python
  `compiled_program_directory`) to the text generation or decision Task to
  reuse them in later runs. The cache keeps only the programs by default, which
  takes little space. With `--cache-weights`
  (`WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS`, Python
  `store_compiled_program_weights=True`) it also keeps the weights prepared for
  each program, so a later run skips preparing them, but it can take several
  times the model's size on disk. The speech recognition Task accepts a program
  cache only for a model that can reuse compiled programs, and Whisper
  checkpoints can't in Windows ML 2.9. See
  [The CGC backend](../../../docs/Runtime/backends.md#the-cgc-backend).
- Models-backed speech recognition transcribes only in Windows ML 2.9; a
  configuration set to translate fails `CreateSession` with
  `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.
- A draft model works only when the llama.cpp loader loaded both models.
- Speech recognition keeps an explicit tokenizer, and its input waveform stays
  in host memory, separate from the model's GPU target.
- A decision result holds answers, not generated text. A recommendation is a
  model output, not permission to take an action.

## Local package override

To build against packages that aren't published yet, follow
[Local package override](../localpackages/README.md). Place the packages in
`Samples\Tasks\localpackages` and either update the two versions in
`Samples\Directory.Packages.props` or pass `-RuntimePackageVersion` and
`-LibLlamaPackageVersion` to `build.ps1`. Do not mix package versions or
architectures.
