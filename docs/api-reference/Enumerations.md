<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Enumerations

> Part of the [WinML Runtime API Reference](README.md).

## Task, Conversation, and Server enumeration index

The exact type names below link to their owning family reference. Use the
named public header for complete enumerators, numeric values, and declaration
order. This is a navigation index, not a complete per-enumerator reference;
full value/behavior coverage for these families remains to be expanded.

### Text Generation - `WinMLTextGenerationTask.h`

| Enumeration | Purpose |
|---|---|
| [`WINML_TEXT_GENERATION_OUTPUT_KIND`](IWinMLTextGenerationTask.md#configuration-profiles) | Logits or composition-sampled token output. |
| [`WINML_TEXT_GENERATION_DECODE_INPUT_MODE`](IWinMLTextGenerationTask.md#configuration-profiles) | Caller-bound input or pipeline feedback. |
| [`WINML_TEXT_GENERATION_PIPELINE_PHASE`](IWinMLTextGenerationTask.md#configuration-profiles) | Unified, prefill, or decode endpoint. |
| [`WINML_TEXT_GENERATION_CONFIGURATION_MODE`](IWinMLTextGenerationTask.md#configuration-profiles) | Unified or prefill/decode composition. |
| [`WINML_TEXT_GENERATION_EXECUTION_PROFILE`](IWinMLTextGenerationTask.md#task-and-session) | Materialized session execution profile. |
| [`WINML_TEXT_GENERATION_SESSION_STATE`](IWinMLTextGenerationTask.md#threading-and-teardown) | Session lifecycle. |
| [`WINML_TEXT_GENERATION_FINISH_REASON`](IWinMLTextGenerationTask.md#streaming-and-results) | Terminal generation reason. |
| [`WINML_TEXT_GENERATION_READ_STATUS`](IWinMLTextGenerationTask.md#streaming-and-results) | Update or completion. |
| [`WINML_TEXT_GENERATION_OPTION_FIELDS`](IWinMLTextGenerationTask.md#option-fields) | Present-field flags for request values. |
| [`WINML_TEXT_GENERATION_EOS_POLICY`](IWinMLTextGenerationTask.md#task-and-session) | Tokenizer defaults or explicit EOS set. |
| [`WINML_TEXT_GENERATION_SEQUENCE_DISCLOSURE`](IWinMLTextGenerationTask.md#capabilities-and-sequence-state) | Whether the retained sequence can be extended. |
| [`WINML_TEXT_GENERATION_SPECULATIVE_METHOD`](IWinMLTextGenerationTask.md#speculative-decoding) | Draft token source for speculative decoding, or none. |

### Chat Completion - `WinMLChatCompletionTask.h`

| Enumeration | Purpose |
|---|---|
| [`WINML_CHAT_COMPLETION_FINISH_REASON`](IWinMLChatCompletionTask.md) | Terminal completion reason, including additional formatter stops. |
| [`WINML_CHAT_COMPLETION_READ_STATUS`](IWinMLChatCompletionTask.md) | Event or completion. |
| [`WINML_CHAT_COMPLETION_PROMPT_CACHE_MODE`](IWinMLChatCompletionTask.md#sequence-reuse-and-token-counts) | Full prefill, reused prefix, not evaluated, or unknown accounting. |

### Automatic Speech Recognition - `WinMLAutomaticSpeechRecognitionTask.h`

| Enumeration | Purpose |
|---|---|
| [`WINML_AUTOMATIC_SPEECH_RECOGNITION_SESSION_STATE`](IWinMLAutomaticSpeechRecognitionTask.md#threading-and-teardown) | Session lifecycle. |
| [`WINML_AUTOMATIC_SPEECH_RECOGNITION_FINISH_REASON`](IWinMLAutomaticSpeechRecognitionTask.md#streaming-and-results) | Terminal transcription reason. |
| [`WINML_AUTOMATIC_SPEECH_RECOGNITION_READ_STATUS`](IWinMLAutomaticSpeechRecognitionTask.md#streaming-and-results) | Update, need input, or completion. |

### Conversation and constraints - `WinMLConversation.h`

| Enumeration | Purpose |
|---|---|
| [`WINML_CONVERSATION_FORMATTER_MODE`](IWinMLStructuredConversationFormatter.md) | Requested formatter mode. |
| [`WINML_CONVERSATION_TOOL_CHOICE`](IWinMLStructuredConversationFormatter.md) | Automatic, none, required, or named tool selection. |
| [`WINML_CONVERSATION_PARALLEL_TOOL_CALLS`](IWinMLStructuredConversationFormatter.md) | Model-default, disabled, or enabled parallel calls. |
| [`WINML_CONVERSATION_CONTINUATION`](IWinMLStructuredConversationFormatter.md) | Assistant continuation mode. |
| [`WINML_CONVERSATION_TEMPLATE_VALUE_TYPE`](IWinMLStructuredConversationFormatter.md) | Type of a template value. |
| [`WINML_CONVERSATION_ENABLE_THINKING`](IWinMLStructuredConversationFormatter.md) | Model-default, disabled, or enabled thinking. |
| [`WINML_CONVERSATION_RESPONSE_FORMAT`](IWinMLStructuredConversationFormatter.md) | Text, JSON object, or JSON schema output. |
| [`WINML_CONVERSATION_PARSER_CAPABILITIES`](IWinMLStructuredConversationFormatter.md) | Parser semantic-output flags. |
| [`WINML_CONVERSATION_CONSTRAINT_TRIGGER_TYPE`](IWinMLTokenizerConstraintFactory.md) | Token, text, pattern, or full-pattern trigger. |
| [`WINML_CONVERSATION_CONSTRAINT_PRESENCE`](IWinMLStructuredConversationFormatter.md) | Absence or presence of a constraint. |
| [`WINML_CONVERSATION_OUTPUT_EVENT_TYPE`](IWinMLStructuredConversationFormatter.md) | Content, reasoning, tool-call, and terminal events. |
| [`WINML_CONVERSATION_OUTPUT_READ_STATUS`](IWinMLStructuredConversationFormatter.md) | Event available or parser queue empty. |
| [`WINML_TOKEN_CONSTRAINT_STATUS`](IWinMLTokenizerConstraintFactory.md) | Active, completed, or poisoned constraint. |
| [`WINML_TOKEN_CONSTRAINT_STATE_FLAGS`](IWinMLTokenizerConstraintFactory.md) | Awaiting-trigger and can-stop flags. |

### Server - `WinMLServer.h`

| Enumeration | Purpose |
|---|---|
| [`WINML_SERVER_STATE`](IWinMLServer.md#start-stop-and-getstate) | Created, running, stopping, or stopped. |
| [`WINML_SERVER_MODEL_CAPABILITY_FLAGS`](IWinMLServer.md#capabilities) | Request features a registered model accepts. |
| [`WINML_SERVER_REASONING_EFFORT_FLAGS`](IWinMLServer.md#capabilities) | `reasoning_effort` values a registered model accepts. |
| [`WINML_SERVER_CONTEXT_DISCLOSURE`](IWinMLServer.md#iwinmlservermodelcontext) | Whether the model file's declared context window is known. |
| [`WINML_SERVER_GENERATION_ENFORCEMENT_POLICY`](IWinMLServer.md#generation-enforcement) | Constrained or natural generation for a lane. |

---

## WINML_EXECUTION_TARGET_KIND

Identifies the hardware class an `IWinMLExecutionTarget` places execution on.

```cpp
typedef enum WINML_EXECUTION_TARGET_KIND
{
    WINML_EXECUTION_TARGET_KIND_CPU = 1,
    WINML_EXECUTION_TARGET_KIND_GPU = 2,
    WINML_EXECUTION_TARGET_KIND_NPU = 3
} WINML_EXECUTION_TARGET_KIND;
```

| Value | Enum | Description |
|---|---|---|
| 1 | `CPU` | CPU execution. |
| 2 | `GPU` | GPU execution via D3D12. |
| 3 | `NPU` | NPU execution. |

See [IWinMLExecutionTarget](IWinMLExecutionTarget.md).

---

## WINML_EXECUTION_TARGET_PREFERENCE

Expresses how `IWinMLRuntime::CreateExecutionTarget` should order candidate
devices of the requested [`WINML_EXECUTION_TARGET_KIND`](#winml_execution_target_kind).

```cpp
typedef enum WINML_EXECUTION_TARGET_PREFERENCE
{
    WINML_EXECUTION_TARGET_PREFERENCE_DEFAULT = 0,
    WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE = 1,
    WINML_EXECUTION_TARGET_PREFERENCE_EFFICIENCY = 2
} WINML_EXECUTION_TARGET_PREFERENCE;
```

| Value | Enum | Description |
|---|---|---|
| 0 | `DEFAULT` | Selection order is left to the platform. |
| 1 | `PERFORMANCE` | Prefer the highest-throughput device of the requested class. |
| 2 | `EFFICIENCY` | Prefer the lowest-power device of the requested class. |

The preference is intent, not a guarantee. It orders the candidates of the
requested class where the platform can order them, and is ignored where it cannot
or where only one candidate exists. It never widens the search to another hardware
class, and it does not apply to `WINML_EXECUTION_TARGET_KIND_CPU`. A value outside
this enumeration fails with `E_INVALIDARG`.

See [IWinMLRuntime::CreateExecutionTarget](IWinMLRuntime.md).

---

## WINML_COMPILED_MODEL_FORM

Selects the compilation stage represented by a compiled artifact.

```cpp
typedef enum WINML_COMPILED_MODEL_FORM
{
    WINML_COMPILED_MODEL_FORM_DEVICE_TARGETED = 0,
    WINML_COMPILED_MODEL_FORM_DURABLE = 1
} WINML_COMPILED_MODEL_FORM;
```

| Value | Enum | Description |
|---|---|---|
| 0 | `DEVICE_TARGETED` | Device-specific artifact for the bound target and current software stack. |
| 1 | `DURABLE` | Hardware-independent artifact completed for the target when loaded. |

See [IWinMLModelCompiler](IWinMLModelCompiler.md).

---

## WINML_PIPELINE_EXECUTION_CAPABILITIES

Static, Build-time capability flags reported by `IWinMLPipelineExecution::GetCapabilities`.

```cpp
typedef enum WINML_PIPELINE_EXECUTION_CAPABILITIES
{
    WINML_PIPELINE_EXECUTION_CAPABILITY_NONE             = 0x0,
    WINML_PIPELINE_EXECUTION_CAPABILITY_REPLAYABLE       = 0x1,
    WINML_PIPELINE_EXECUTION_CAPABILITY_ITERATIVE_REPLAY = 0x2
} WINML_PIPELINE_EXECUTION_CAPABILITIES;
DEFINE_ENUM_FLAG_OPERATORS(WINML_PIPELINE_EXECUTION_CAPABILITIES)
```

| Value | Enum | Description |
|---|---|---|
| `0x0` | `NONE` | No async/replay capability; use `IWinMLPipeline::Run`. |
| `0x1` | `REPLAYABLE` | The graph can submit on the device timeline without a blocking host fallback. `IWinMLPipelineExecution` is available. |
| `0x2` | `ITERATIVE_REPLAY` | Build validated a replayable iterative region; `IWinMLPipelineIterationExecution::SubmitIterations` is available. |

See [IWinMLPipelineExecution](IWinMLPipelineExecution.md) and
[IWinMLPipelineIterationExecution](IWinMLPipelineIterationExecution.md).

---

## WINML_TENSOR_DATA_TYPE

Element data types for tensors. `UNDEFINED` (0) supports backends that
report raw byte buffers without a known element type.

```cpp
typedef enum WINML_TENSOR_DATA_TYPE
{
    WINML_TENSOR_DATA_TYPE_UNDEFINED = 0,
    WINML_TENSOR_DATA_TYPE_FLOAT32   = 1,
    WINML_TENSOR_DATA_TYPE_FLOAT16   = 2,
    WINML_TENSOR_DATA_TYPE_BFLOAT16  = 3,
    WINML_TENSOR_DATA_TYPE_INT8      = 4,
    WINML_TENSOR_DATA_TYPE_UINT8     = 5,
    WINML_TENSOR_DATA_TYPE_INT16     = 6,
    WINML_TENSOR_DATA_TYPE_UINT16    = 7,
    WINML_TENSOR_DATA_TYPE_INT32     = 8,
    WINML_TENSOR_DATA_TYPE_UINT32    = 9,
    WINML_TENSOR_DATA_TYPE_INT64     = 10,
    WINML_TENSOR_DATA_TYPE_UINT64    = 11,
    WINML_TENSOR_DATA_TYPE_FLOAT64   = 12,
    WINML_TENSOR_DATA_TYPE_BOOL      = 13,
    WINML_TENSOR_DATA_TYPE_INT4      = 14,
    WINML_TENSOR_DATA_TYPE_UINT4     = 15
} WINML_TENSOR_DATA_TYPE;
```

| Value | Enum | Bytes |
|---|---|---|
| 0 | `UNDEFINED` | -- |
| 1 | `FLOAT32` | 4 |
| 2 | `FLOAT16` | 2 |
| 3 | `BFLOAT16` | 2 |
| 4 | `INT8` | 1 |
| 5 | `UINT8` | 1 |
| 6 | `INT16` | 2 |
| 7 | `UINT16` | 2 |
| 8 | `INT32` | 4 |
| 9 | `UINT32` | 4 |
| 10 | `INT64` | 8 |
| 11 | `UINT64` | 8 |
| 12 | `FLOAT64` | 8 |
| 13 | `BOOL` | 1 |
| 14 | `INT4` | packed (2 / byte) |
| 15 | `UINT4` | packed (2 / byte) |

---

## WINML_TENSOR_LOCK_MODE

```cpp
typedef enum WINML_TENSOR_LOCK_MODE
{
    WINML_TENSOR_LOCK_MODE_READ       = 1,
    WINML_TENSOR_LOCK_MODE_WRITE      = 2,
    WINML_TENSOR_LOCK_MODE_READ_WRITE = 3
} WINML_TENSOR_LOCK_MODE;
```

| Value | Enum | Meaning |
|---|---|---|
| 1 | `READ` | Read existing contents. |
| 2 | `WRITE` | Write new contents without preserving prior values. |
| 3 | `READ_WRITE` | Read existing contents, then write updated contents. |

---

## WINML_TENSOR_LOCK_FLAGS

```cpp
typedef enum WINML_TENSOR_LOCK_FLAGS
{
    WINML_TENSOR_LOCK_FLAG_NONE                          = 0,
    WINML_TENSOR_LOCK_FLAG_ALLOW_SYNCHRONIZED_CPU_ACCESS = 0x1
} WINML_TENSOR_LOCK_FLAGS;
```

| Value | Enum | Meaning |
|---|---|---|
| 0x0 | `NONE` | Require direct CPU accessibility only. |
| 0x1 | `ALLOW_SYNCHRONIZED_CPU_ACCESS` | Allow staged readback / copy-back for non-CPU-visible tensors. |

---

## WINML_TENSOR_ACCESS_MODE

Declares how a tensor created from a caller-owned CPU buffer relates to that
backing memory for the tensor's lifetime. Maps 1:1 onto the Win32 file-mapping
access-protection model. See
[`IWinMLRawTensorFactory::CreateTensorFromRawBuffer`](IWinMLRawTensorFactory.md).

```cpp
typedef enum WINML_TENSOR_ACCESS_MODE
{
    WINML_TENSOR_ACCESS_MODE_COPY       = 0,
    WINML_TENSOR_ACCESS_MODE_READ       = 1,
    WINML_TENSOR_ACCESS_MODE_READWRITE  = 2
} WINML_TENSOR_ACCESS_MODE;
```

| Value | Enum | Win32 analog | Description |
|---|---|---|---|
| 0 | `COPY` | `FILE_MAP_COPY` / `PAGE_WRITECOPY` | Private copy; the caller's buffer is never read after the call returns. |
| 1 | `READ` | `FILE_MAP_READ` / `PAGE_READONLY` | Read-only in-place view; no copy. Write locks and in-place ops are rejected. |
| 2 | `READWRITE` | `FILE_MAP_WRITE` / `PAGE_READWRITE` | Read-write in-place view; no copy. The tensor reads and writes the caller's buffer directly. |

`COPY` is the universal default. `READ`/`READWRITE` are honored only when the
source bytes already match the requested tensor layout/dtype exactly.

---

## WINML_TENSOR_LAYOUT_FORMAT

```cpp
typedef enum WINML_TENSOR_LAYOUT_FORMAT
{
    WINML_TENSOR_LAYOUT_FORMAT_UNSPECIFIED = 0,
    WINML_TENSOR_LAYOUT_FORMAT_NCHW        = 1,
    WINML_TENSOR_LAYOUT_FORMAT_NHWC        = 2,
    WINML_TENSOR_LAYOUT_FORMAT_NCT         = 16,
    WINML_TENSOR_LAYOUT_FORMAT_NT          = 17,
    WINML_TENSOR_LAYOUT_FORMAT_NL          = 32
} WINML_TENSOR_LAYOUT_FORMAT;
```

| Value | Enum | Domain |
|---|---|---|
| 0 | `UNSPECIFIED` | Adapter picks the vertical default. |
| 1 | `NCHW` | Image: batch, channel, height, width. |
| 2 | `NHWC` | Image: batch, height, width, channel. |
| 16 | `NCT` | Audio: batch, channel, time. |
| 17 | `NT` | Audio: batch, time. |
| 32 | `NL` | Text/token: batch, sequence length. |

Used by [`WINML_IMAGE_TENSOR_DESC`](Structures.md#winml_image_tensor_desc) and
[`WINML_AUDIO_TENSOR_DESC`](Structures.md#winml_audio_tensor_desc).

---

## WINML_TENSOR_CHANNEL_ORDER

```cpp
typedef enum WINML_TENSOR_CHANNEL_ORDER
{
    WINML_TENSOR_CHANNEL_ORDER_UNSPECIFIED = 0,
    WINML_TENSOR_CHANNEL_ORDER_RGB         = 1,
    WINML_TENSOR_CHANNEL_ORDER_BGR         = 2,
    WINML_TENSOR_CHANNEL_ORDER_GRAY        = 3,
    WINML_TENSOR_CHANNEL_ORDER_RGBA        = 4,
    WINML_TENSOR_CHANNEL_ORDER_BGRA        = 5
} WINML_TENSOR_CHANNEL_ORDER;
```

Used by [`WINML_IMAGE_TENSOR_DESC`](Structures.md#winml_image_tensor_desc) and
[IWinMLImageTensorFactory](IWinMLImageTensorFactory.md).

---

## WINML_IMAGE_RESIZE_MODE

```cpp
typedef enum WINML_IMAGE_RESIZE_MODE
{
    WINML_IMAGE_RESIZE_MODE_STRETCH     = 0,
    WINML_IMAGE_RESIZE_MODE_CENTER_CROP = 1,
    WINML_IMAGE_RESIZE_MODE_LETTERBOX   = 2
} WINML_IMAGE_RESIZE_MODE;
```

| Value | Enum | Description |
|---|---|---|
| 0 | `STRETCH` | Resize without preserving aspect ratio. |
| 1 | `CENTER_CROP` | Preserve aspect ratio, crop to fit. |
| 2 | `LETTERBOX` | Preserve aspect ratio, pad with `letterboxValue`. |

---

## WINML_TENSOR_QUANTIZATION_MODE

```cpp
typedef enum WINML_TENSOR_QUANTIZATION_MODE
{
    WINML_TENSOR_QUANTIZATION_MODE_NONE                 = 0,
    WINML_TENSOR_QUANTIZATION_MODE_SYMMETRIC_PER_TENSOR = 1,
    WINML_TENSOR_QUANTIZATION_MODE_ZERO_POINT_AFFINE    = 2,
    WINML_TENSOR_QUANTIZATION_MODE_RAW                  = 3
} WINML_TENSOR_QUANTIZATION_MODE;
```

Used by [`WINML_TENSOR_QUANTIZATION`](Structures.md#winml_tensor_quantization),
optionally attached to a [`WINML_IMAGE_TENSOR_DESC`](Structures.md#winml_image_tensor_desc).

---

## WINML_TOKENIZER_ENCODE_FLAGS

```cpp
typedef enum WINML_TOKENIZER_ENCODE_FLAGS
{
    WINML_TOKENIZER_ENCODE_FLAG_NONE               = 0x0,
    WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS = 0x1
} WINML_TOKENIZER_ENCODE_FLAGS;
DEFINE_ENUM_FLAG_OPERATORS(WINML_TOKENIZER_ENCODE_FLAGS)
```

See [IWinMLTokenizer::Encode](IWinMLTokenizer.md).

---

## WINML_TOKENIZER_DECODE_FLAGS

```cpp
typedef enum WINML_TOKENIZER_DECODE_FLAGS
{
    WINML_TOKENIZER_DECODE_FLAG_NONE                = 0x0,
    WINML_TOKENIZER_DECODE_FLAG_SKIP_SPECIAL_TOKENS = 0x1
} WINML_TOKENIZER_DECODE_FLAGS;
DEFINE_ENUM_FLAG_OPERATORS(WINML_TOKENIZER_DECODE_FLAGS)
```

See [IWinMLTokenizer::Decode](IWinMLTokenizer.md).

---
