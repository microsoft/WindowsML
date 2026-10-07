<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLAutomaticSpeechRecognitionTask

The Automatic Speech Recognition Task API composes caller-created Runtime
objects into waveform, Whisper log-mel, and optional live-input transcription
sessions.

Obtain `IWinMLAutomaticSpeechRecognitionTaskFactory` from `IWinMLTasks` to create
a task with a tokenizer. Then call
`IWinMLAutomaticSpeechRecognitionTask::CreateConfiguration` and configure the
result.

The generic Task/session lifecycle does not load models or select targets.

## Configuration

`IWinMLAutomaticSpeechRecognitionConfiguration::Validate` checks the configured
composition. Query the same object for
`IWinMLWhisperAutomaticSpeechRecognitionConfiguration` to set or read
`WINML_WHISPER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS`:

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
additional references; callers release each returned reference.

Pass a configuration created by this ASR task to `CreateSession`. A configuration
from another ASR task returns `E_INVALIDARG`. A caller-implemented configuration
can fail with `E_NOINTERFACE` if it does not expose the required typed view.
Successful session creation consumes the configuration; rejected configurations
remain usable.

## Session input paths

| Interface | Input path |
|---|---|
| `IWinMLAutomaticSpeechRecognitionSession` | Normalized mono waveform tensor plus explicit sample metadata. |
| `IWinMLWhisperLogMelInput` | Caller-prepared Whisper log-mel tensor. |
| `IWinMLAutomaticSpeechRecognitionLiveSession` | Optional incremental live-input session. |

Waveform metadata supplies sample rate, channel count, and valid sample count.
The Whisper composition accepts 16 kHz mono, 1-480000 valid samples, as finite
normalized FLOAT32 values in `[-1,1]`. Waveform tensors have shape `[samples]` or
`[1,samples]`; the valid span is copied before execution.

`IWinMLWhisperLogMelInput::TranscribeLogMel` requires FLOAT32 `[1,80,3000]` in
batch/mel/time order. Direct log-mel input bypasses preprocessing. Without the
optional preprocessing pipeline, the built-in log-mel computation is used.

Live input uses bounded backpressure. `WriteChunk` reports whether the chunk was
accepted and returns an optional lifetime watermark. The pull stream may return
`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_NEED_INPUT` when more audio is
required. `CompleteInput` signals the end of input.

## Streaming and results

`IWinMLAutomaticSpeechRecognitionPullStream::ReadNext` returns text updates,
need-input notifications, and terminal completion. Successful
`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_NEED_INPUT` and
`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS_COMPLETED` return an empty
fragment string.

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

Query the session for `IWinMLWhisperLogMelInput` or
`IWinMLAutomaticSpeechRecognitionLiveSession` to test support. Whisper sessions
expose `IWinMLWhisperLogMelInput` and do not expose the live-session interface.
Operation methods still require the session creation thread and a valid state;
use `GetState` to distinguish support from readiness.

Python exposes `session.supports_whisper_log_mel`, `session.supports_live_input`,
`configuration.whisper_bindings`, `configuration.configure_whisper(...)`, and
`session.transcribe_whisper_log_mel(features, cancellation=...)`.
