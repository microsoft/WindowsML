<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Speech to text generation

This sample composes two independent Tasks in application code. It transcribes
a WAV file with the ASR Task, combines the transcript with an instruction in one
chat turn, and submits that turn to a text-generation Task.

The sample does not build a combined model. It runs ASR first, then passes the
transcript text to a separate text-generation Task.

```text
WAV -> ASR Runtime pipelines -> ASR Task -> transcript string
                                              |
                                              v
instruction + transcript (one chat turn) -> Text Generation Task -> response
```

Because plain text crosses the boundary, each Task keeps its own model, target,
cancellation source, and result. See
[Task and Runtime lifecycle](../../../../docs/Tasks/task-lifecycle.md),
[`IWinMLAutomaticSpeechRecognitionTask`](../../../../docs/api-reference/IWinMLAutomaticSpeechRecognitionTask.md),
and
[`IWinMLTextGenerationTask`](../../../../docs/api-reference/IWinMLTextGenerationTask.md).

```powershell
..\..\build.ps1 -Sample speech-to-text-generation -PackageOnly
..\..\run_speech_to_text_generation.ps1 -LanguageBackend llama
```

`-LanguageBackend ort` runs the text-generation half on an ONNX model. The
Python entry point exposes the same choice through `--language-backend`.
