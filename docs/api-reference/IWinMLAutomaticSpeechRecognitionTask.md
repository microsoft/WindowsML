<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLAutomaticSpeechRecognitionTask

The Automatic Speech Recognition Task API turns a waveform into a transcript.
A session either runs a published speech model that the application loaded, or
composes caller-created Runtime encoder and decoder pipelines. Sessions accept
waveforms, an optional precomputed encoder input, and optional live input.

Obtain `IWinMLAutomaticSpeechRecognitionTaskFactory` from `IWinMLTasks` to create
a task with a tokenizer. Then call
`IWinMLAutomaticSpeechRecognitionTask::CreateConfiguration` and configure the
result in one of two ways:

| Configuration | The application | Interface |
|---|---|---|
| Models-backed | Loads a published speech model, such as a Whisper checkpoint folder, with `IWinMLRuntime::LoadModelFromPath` and calls `SetModel`. | `IWinMLModelAutomaticSpeechRecognitionConfiguration` |
| Encoder-decoder composition | Builds encoder and decoder pipelines and binds them with `SetBindings`. | `IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration` |

A configuration is one or the other: `SetModel` after `SetBindings`, and
`SetBindings` after `SetModel`, return `E_NOT_VALID_STATE`.

## Language, mode, and predictions

Every configuration, Models-backed or composed, carries the language, mode, and
prediction settings of the session it describes, so one task can serve sessions
in different languages. `CreateSession` reads them; a session never changes
them. `Validate` does not check them; `CreateSession` returns
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the model or composition cannot
honor them.

| Method | Behavior |
|---|---|
| `SetLanguage`, `GetLanguage` | The language spoken in the audio, as a BCP 47 tag such as `en-US` or `de`, compared without regard to case and possibly by its primary subtag. Null or empty restores the default: a Models-backed configuration identifies the language automatically, or uses the model's default language; a composed configuration uses English. A malformed tag returns `E_INVALIDARG`. `GetLanguage` returns a `CoTaskMemAlloc` string, empty for the default. |
| `SetMode`, `GetMode` | `WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE_TRANSCRIBE`, the default, writes the speech as text in the language spoken. `WINML_AUTOMATIC_SPEECH_RECOGNITION_MODE_TRANSLATE` writes it as English text. A Models-backed configuration transcribes only, so `TRANSLATE` fails its `CreateSession` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. The encoder-decoder composition translates when its tokenizer maps `<\|translate\|>` to identifier 50358. |
| `SetPredictionsEnabled`, `GetPredictionsEnabled` | Opt in to `WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_PREDICTION` updates: provisional text that follows the recognized text and that later recognition may revise. Off by default. A model that recognizes speech in fixed-length windows, and the encoder-decoder composition, report no predictions. |

The setters return `E_NOT_VALID_STATE` for a reserved or consumed configuration.
In Python they are the `language`, `mode`, and `predictions_enabled` properties.

## Models-backed configuration

Query the configuration for `IWinMLModelAutomaticSpeechRecognitionConfiguration`
(IID `3b6932a2-a570-4b23-9781-78b6ae4c7071`) and call
`SetModel(target, model, options)` with a model that
`IWinMLRuntime::LoadModelFromPath` or `LoadModelFromFile` loaded. Windows ML
reads the model from the path it was loaded from during `SetModel` and runs
every model computation on the target the application picked. The task's
tokenizer, which the application creates from the model's `tokenizer.json`,
converts the recognized tokens to text.

| Behavior | Detail |
|---|---|
| Model files | A model folder needs `config.json` and its weights in `model.safetensors` or `model.safetensors.index.json` with every shard. A Whisper folder also needs `generation_config.json` unless `config.json` holds `no_timestamps_token_id`. A missing required file returns `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)`. `SetModel` reads no tokenizer file. |
| Input | Sessions accept mono waveforms at the model's sample rate, or at any rate from 8000 through 48000 Hz when the model converts its input. They expose `IWinMLAutomaticSpeechRecognitionLiveSession` when the model accepts live input, and never expose `IWinMLAutomaticSpeechRecognitionEncoderInput`. The waveform stays in host memory, separate from the model's target. |
| Text | `UPDATE` fragments deliver the text of each segment as the model commits it, and are never revised. Every segment after the first starts with one space, so the fragments of an operation concatenate into its transcript. A word that spans a fixed-length window can be cut or transcribed differently in each window. |
| Program cache | `WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTIONS::compiledProgramDirectory` stores compiled programs for reuse in later processes, and `compiledProgramStorage`, with `WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE`, says whether the directory also keeps the prepared weights. See [Compiled programs and stored weights](IWinMLTasks.md#compiled-programs-and-stored-weights). A model that can't reuse compiled programs returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` from `SetModel` when a program cache is present; in Windows ML 2.9 that includes Whisper checkpoints. In Python, pass `compiled_program_directory` and `store_compiled_program_weights` to `AutomaticSpeechRecognitionConfiguration.set_model`. |
| Cancellation | Observed between model steps. |
| llama.cpp | A model that `IWinMLLlamaModelLoader` loaded returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |

`SetModel` returns `E_POINTER` for a null argument other than `options`;
`E_INVALIDARG` for a model that the Windows ML Runtime did not load or for
invalid options; `E_NOT_VALID_STATE` for a reserved, consumed, or composed
configuration; `HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)` when the model's files
no longer exist; `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a model Windows
ML does not support, including one loaded from a buffer, or a present option the
model can't honor;
`HRESULT_FROM_WIN32(ERROR_DIRECTORY)` when the program cache names an existing
file; and `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)` or
`HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` for malformed model files.
`CreateSession` prepares the model for the target and returns the Runtime error
when the target cannot run it.

```cpp
wil::com_ptr<IWinMLModel> model;
RETURN_IF_FAILED(runtime->LoadModelFromPath(modelFolder, nullptr, model.put()));

wil::com_ptr<IWinMLAutomaticSpeechRecognitionConfiguration> configuration;
RETURN_IF_FAILED(task->CreateConfiguration(configuration.put()));
RETURN_IF_FAILED(configuration->SetLanguage(L"en"));
wil::com_ptr<IWinMLModelAutomaticSpeechRecognitionConfiguration> modelConfiguration;
RETURN_IF_FAILED(configuration->QueryInterface(IID_PPV_ARGS(modelConfiguration.put())));
RETURN_IF_FAILED(modelConfiguration->SetModel(gpuTarget.get(), model.get(), nullptr));

wil::com_ptr<IWinMLAutomaticSpeechRecognitionSession> session;
RETURN_IF_FAILED(task->CreateSession(configuration.get(), session.put()));
```

In Python, call `configuration.set_model(target, model,
compiled_program_directory=None, store_compiled_program_weights=False)`. See the
[Models-backed speech recognition sample](../../Samples/Tasks/model-backed/automatic-speech-recognition/).

## Encoder-decoder configuration

`IWinMLAutomaticSpeechRecognitionConfiguration::Validate` checks the configured
composition. Query the same object for
`IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration` to set or read
`WINML_ENCODER_DECODER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS`:

| Fields | Meaning |
|---|---|
| `encoderPipeline`, `encoderStage` | Required encoder graph and stage. |
| `decoderPipeline`, `decoderStage` | Required decoder graph and stage. |
| `tensorTarget` | Required target with raw tensor creation. |
| `preprocessingPipeline`, `preprocessingStage` | Optional pair; both supplied or both null. |
| `forceEncoderOutputCopy` | `FALSE` for default binding policy; `TRUE` forces the copy fallback. |

`SetBindings` replaces the binding set after validating required pointers and
the optional preprocessing pair. A failed call leaves the previous set intact.
`GetBindings` initializes its output and returns retained interfaces with
additional references; callers release each returned reference. The supported
composition is a Whisper encoder and decoder. The C++ helper
`winml::tasks::ConfigureWhisper` in
`winml/tasks/speech/asr/WhisperAutomaticSpeechRecognition.hpp` fills the
bindings.

> [!NOTE]
> Windows ML 2.9 renamed the Whisper-specific names of this composition.
> `IWinMLWhisperAutomaticSpeechRecognitionConfiguration` is now
> `IWinMLEncoderDecoderAutomaticSpeechRecognitionConfiguration`,
> `WINML_WHISPER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS` is now
> `WINML_ENCODER_DECODER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS`, and
> `IWinMLWhisperLogMelInput::TranscribeLogMel` is now
> `IWinMLAutomaticSpeechRecognitionEncoderInput::TranscribeEncoderInput`. The
> IIDs and behavior are unchanged. Because methods were added to
> `IWinMLAutomaticSpeechRecognitionConfiguration`, rebuild code that calls
> `SetBindings` or `GetBindings` against the 2.9 headers.

Pass a configuration created by this ASR task to `CreateSession`. A configuration
from another ASR task returns `E_INVALIDARG`. A caller-implemented configuration
can fail with `E_NOINTERFACE` if it does not expose the required typed view.
Successful session creation consumes the configuration; rejected configurations
remain usable.

## Session input paths

| Interface | Input path |
|---|---|
| `IWinMLAutomaticSpeechRecognitionSession` | Normalized mono waveform tensor plus explicit sample metadata. |
| `IWinMLAutomaticSpeechRecognitionEncoderInput` | Caller-computed encoder input, such as a Whisper log-mel tensor, in place of a waveform. Encoder-decoder sessions only. |
| `IWinMLAutomaticSpeechRecognitionLiveSession` | Optional incremental live-input session. |

Waveform metadata supplies sample rate, channel count, and valid sample count.
The encoder-decoder composition accepts 16 kHz mono, 1-480000 valid samples, as
finite normalized FLOAT32 values in `[-1,1]`. Waveform tensors have shape
`[samples]` or `[1,samples]`; the valid span is copied before execution.

`IWinMLAutomaticSpeechRecognitionEncoderInput::TranscribeEncoderInput` requires
FLOAT32 `[1,80,3000]` log-mel features in batch/mel/time order for the supported
composition. Direct encoder input bypasses preprocessing. Without the optional
preprocessing pipeline, the built-in log-mel computation is used.

Live input uses bounded backpressure. `WriteChunk` reports whether the chunk was
accepted and returns an optional lifetime watermark. The pull stream may return
`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_NEED_INPUT` when more audio is
required. `CompleteInput` signals the end of input.

## Streaming and results

`IWinMLAutomaticSpeechRecognitionPullStream::ReadNext` returns text updates,
need-input notifications, predictions when enabled, and terminal completion.
Successful `WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_NEED_INPUT` and
`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED` return an empty
fragment string.

`UPDATE` fragments are never revised, and the `UPDATE` fragments of an operation
concatenate into its transcript. A `PREDICTION` fragment is the whole
provisional text that currently follows the delivered text and replaces the
previous prediction; an empty fragment withdraws it. Every `UPDATE` discards the
current prediction, so remove a displayed prediction before appending the
`UPDATE` fragment. `COMPLETED` discards any prediction.

Pass an `IWinMLCancellationSource` when starting transcription or live input and
call `Cancel` from any thread to request cancellation. The native stream has no
`Cancel` method.

`IWinMLAutomaticSpeechRecognitionResult` reports transcript text, finish reason,
and terminal HRESULT.

## Threading and teardown

The factory, task, and native configuration storage impose no creation-thread
check. Each session belongs to the thread that calls `CreateSession`. Session
methods, feature/live views, live input, and streams use that thread and return
`RPC_E_WRONG_THREAD` from another thread.

Session, stream, and live-input `Close` use the session creation thread.
Repeating a successful `Close` returns `S_OK`, subject to the same prerequisites:
session `Close` rejects active work, and live-input `Close` requires
`CompleteInput` or a terminal stream first; otherwise they return
`E_NOT_VALID_STATE`.

Stream `Close` finalizes cancellation if the stream is not already terminal.
`GetResult` remains available and subsequent `ReadNext` returns `COMPLETED` with
an empty string. After session closure, `GetState` reports
`WINML_AUTOMATIC_SPEECH_RECOGNITION_SESSION_STATE_CLOSED`; `GetRuntime` and
`GetConfiguration` remain available.

## Capability discovery

Query the session for `IWinMLAutomaticSpeechRecognitionEncoderInput` or
`IWinMLAutomaticSpeechRecognitionLiveSession` to test support. Encoder-decoder
sessions expose `IWinMLAutomaticSpeechRecognitionEncoderInput` and do not expose
the live-session interface. Operation methods still require the session creation
thread and a valid state; use `GetState` to distinguish support from readiness.

Python exposes `session.supports_encoder_input`, `session.supports_live_input`,
`configuration.encoder_decoder_bindings`, `configuration.configure_whisper(...)`,
`configuration.set_model(...)`, and
`session.transcribe_encoder_input(features, cancellation=...)`. Windows ML 2.9
renamed the Python names `WhisperAutomaticSpeechRecognitionBindings`,
`whisper_bindings`, `transcribe_whisper_log_mel`, and `supports_whisper_log_mel`
to `EncoderDecoderAutomaticSpeechRecognitionBindings`, `encoder_decoder_bindings`,
`transcribe_encoder_input`, and `supports_encoder_input`;
`configure_whisper` is unchanged.