<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Text generation from published weights

Bring your own weights: load a published model and let Windows ML run it. The
app builds no pipeline and passes no tokenizer.

1. Load a checkpoint folder with `IWinMLRuntime::LoadModelFromPath`, or a
   single GGUF file with `IWinMLRuntime::LoadModelFromFile`.
2. Create a text generation configuration, query it for
   `IWinMLModelTextGenerationConfiguration` and call `SetModel` with a GPU
   execution target. Windows ML plans the model into programs for the CGC
   backend on that target and creates the model's own tokenizer from its files.
3. Create the task with the configuration's tokenizer (`GetTokenizer`), create
   a session and stream a reply.

The prompt is sent as one user turn in the model's chat template, so the
instruct model ends its reply on its own. The sample prints the streamed reply,
the token counts and why generation stopped:

```text
Prompt: Write one sentence about the ocean.
Reply: The ocean, a vast and mysterious body of water, is a vast expanse of blue and green, ...
Tokens: prompt=36 generated=60
Finished because: end-of-sequence token
```

A model that Windows ML does not support fails `SetModel` with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`, and the sample explains the two ways
forward: use a supported model, or load a GGUF file deliberately with the
llama.cpp loader as the [llama.cpp sample](../llama-text-generation/) does.

The first session compiles programs for the GPU. Pass `--program-cache
<directory>` (Python `--program-cache`, run script `-ProgramCache`) to keep them
so later runs start faster. By default the cache keeps only the programs, a few
megabytes for this model. Add `--cache-weights` (run script `-CacheWeights`) to
also keep the weights prepared for each program, so later runs skip preparing
them. That can take several times the model's size on disk, because each
program keeps its own copy. `--capacity` sets the largest sequence the session
holds; without it the session uses the model's default.

```powershell
..\..\run_model_text_generation.ps1
..\..\run_model_text_generation.ps1 -Format gguf -ProgramCache ..\..\models\program-cache
..\..\run_model_text_generation.ps1 -ProgramCache ..\..\models\program-cache-weights -CacheWeights
python.exe .\main.py ..\..\models\qwen2.5-0.5b-instruct
```

See [Models-backed Tasks](../README.md) for package and model requirements.
