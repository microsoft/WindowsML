<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# Structures

> Part of the [WinML Runtime API Reference](README.md).

## WINML_TENSOR_SCHEMA_DESC

```cpp
typedef struct WINML_TENSOR_SCHEMA_DESC
{
    WINML_TENSOR_DATA_TYPE dataType;
    UINT32 dimensionCount;
    [size_is(dimensionCount)] const UINT64* dimensions;
} WINML_TENSOR_SCHEMA_DESC;
```

The **declared** schema descriptor returned by `IWinMLModelSchema`. A dimension
may be `UINT64_MAX` to represent a free/dynamic declared dimension. Reflection
of a free dimension does not guarantee execution support on every target.
Contrast with the fully-resolved `WINML_TENSOR_DESC` used for
concrete tensors and the post-Build materialized `IWinMLStageSchema`. See
[IWinMLModelSchema](IWinMLModelSchema.md).

A rank-zero tensor has `dimensionCount == 0` and represents one tensor element.
It is distinct from a non-tensor scalar value in an artifact's intermediate
representation; tensor-schema support does not imply support for such values.

---

## WINML_TENSOR_DESC

```cpp
typedef struct WINML_TENSOR_DESC
{
    WINML_TENSOR_DATA_TYPE dataType;
    UINT32                 dimensionCount;
    [size_is(dimensionCount)] const UINT64* dimensions;
} WINML_TENSOR_DESC;
```

**Remarks:** `dimensions` points to an array of at least `dimensionCount`
elements. On input the runtime copies what it needs; on a `GetDesc`-style
output it points into the object's own storage and is valid for that object's
lifetime -- the caller never frees it. `UINT64_MAX` indicates a dynamic/free
dimension.

Used by [IWinMLTensor::GetDesc](IWinMLTensor.md),
[IWinMLStageSchema](IWinMLStageSchema.md), and
[IWinMLProcessor](IWinMLProcessor.md).

---

## WINML_BUFFER_BINDING

Zero-copy GPU buffer binding for GPU interop.

```cpp
typedef struct WINML_BUFFER_BINDING
{
    IUnknown* resource;        // QI-able for ID3D12Resource
    UINT64    offsetInBytes;
    UINT64    sizeInBytes;
} WINML_BUFFER_BINDING;
```

**Remarks:** Used with `IWinMLRawTensorFactory::CreateTensorFromBuffer` to
create GPU-backed tensors, and returned by
`IWinMLD3D12Tensor::GetBufferBinding` for zero-copy resource sharing.

---

## WINML_RESOURCE_DESCRIPTOR

Describes a single externalized weight blob. Shared across both directions (the
compile sink and the load reader) so a caller can persist a resource with the
alignment the backend wants and serve it back zero-copy. The key that names the
resource is the artifact's `external_data` location string and is carried
separately (it is not part of this struct).

```cpp
typedef struct WINML_RESOURCE_DESCRIPTOR
{
    UINT64 byteSize;     // Total size of the raw payload in bytes
    UINT64 alignment;    // Requested payload alignment in bytes
    UINT64 offset;       // Reserved for backend-assigned layout (0 for in-memory)
} WINML_RESOURCE_DESCRIPTOR;
```

| Field | Description |
|---|---|
| `byteSize` | Size of the resource payload in bytes. |
| `alignment` | Requested byte alignment (nonzero power of two) so the caller can persist the blob for zero-copy reload. |
| `offset` | Reserved for backend-assigned layout; `0` for the in-memory keyed store. |

Used by [`IWinMLResourceMapReader`](IWinMLResourceMapReader.md) and
[`IWinMLCompileOutputSink`](IWinMLCompileOutputSink.md).

---

## WINML_TENSOR_NORMALIZATION

```cpp
typedef struct WINML_TENSOR_NORMALIZATION
{
    FLOAT scale[4];
    FLOAT mean[4];
} WINML_TENSOR_NORMALIZATION;
```

Per-channel `(x - mean) * scale` normalization applied by the image tensor
adapters. Embedded in [`WINML_IMAGE_TENSOR_DESC`](#winml_image_tensor_desc).

---

## WINML_TENSOR_QUANTIZATION

```cpp
typedef struct WINML_TENSOR_QUANTIZATION
{
    WINML_TENSOR_QUANTIZATION_MODE mode;
    UINT32 scaleCount;
    const FLOAT* scales;
    UINT32 zeroPointCount;
    const FLOAT* zeroPoints;
} WINML_TENSOR_QUANTIZATION;
```

Optional quantization parameters attached to an image tensor descriptor. See
[`WINML_TENSOR_QUANTIZATION_MODE`](Enumerations.md#winml_tensor_quantization_mode).

---

## WINML_IMAGE_TENSOR_DESC

```cpp
typedef struct WINML_IMAGE_TENSOR_DESC
{
    WINML_TENSOR_DATA_TYPE dataType;
    WINML_TENSOR_LAYOUT_FORMAT layout;
    WINML_TENSOR_CHANNEL_ORDER channelOrder;
    UINT32 batchSize;
    UINT32 channels;
    UINT32 width;
    UINT32 height;
    WINML_IMAGE_RESIZE_MODE resizeMode;
    FLOAT letterboxValue[4];
    WINML_TENSOR_NORMALIZATION normalization;
    const WINML_TENSOR_QUANTIZATION* quantization;
} WINML_IMAGE_TENSOR_DESC;
```

| Field | Description |
|---|---|
| `dataType` | Destination tensor element type. |
| `layout` | `NCHW` or `NHWC`. |
| `channelOrder` | `RGB`, `BGR`, `GRAY`, `RGBA`, or `BGRA`. |
| `batchSize` | Destination batch dimension. |
| `channels` | Destination channel count. |
| `width` / `height` | Destination spatial dimensions. |
| `resizeMode` | `STRETCH`, `CENTER_CROP`, or `LETTERBOX`. |
| `letterboxValue` | Per-channel pad value used when `resizeMode` is `LETTERBOX`. |
| `normalization` | Per-channel scale/mean applied during conversion. |
| `quantization` | Optional; `nullptr` for unquantized tensors. |

Used by [IWinMLImageTensorFactory](IWinMLImageTensorFactory.md).

---

## WINML_AUDIO_TENSOR_DESC

```cpp
typedef struct WINML_AUDIO_TENSOR_DESC
{
    WINML_TENSOR_DATA_TYPE dataType;
    WINML_TENSOR_LAYOUT_FORMAT layout;
    UINT32 sampleRate;
    UINT32 channels;
    UINT32 windowFrames;
    UINT32 hopFrames;
} WINML_AUDIO_TENSOR_DESC;
```

| Field | Description |
|---|---|
| `dataType` | Destination tensor element type. |
| `layout` | `NCT` or `NT`. |
| `sampleRate` | Source PCM sample rate in Hz. |
| `channels` | Channel count. |
| `windowFrames` | Frames per analysis window. |
| `hopFrames` | Frame stride between windows. |

Used by [IWinMLAudioTensorFactory](IWinMLAudioTensorFactory.md).

---

## WINML_TOKEN_TENSOR_DESC

```cpp
typedef struct WINML_TOKEN_TENSOR_DESC
{
    WINML_TENSOR_DATA_TYPE dataType;
    UINT32 batchSize;
} WINML_TOKEN_TENSOR_DESC;
```

Used by [IWinMLTextTensorFactory](IWinMLTextTensorFactory.md).

---

## WINML_VIDEO_FRAME_METADATA

```cpp
typedef struct WINML_VIDEO_FRAME_METADATA
{
    UINT32 sourceWidth;
    UINT32 sourceHeight;
    UINT32 sourceStride;
    INT64 timestamp100Ns;
    INT64 duration100Ns;
} WINML_VIDEO_FRAME_METADATA;
```

Declares the source plane geometry and timing for the Media Foundation NV12
adapters. `sourceStride` of `0` means tightly packed. Used by
[IWinMLImageTensorFactory](IWinMLImageTensorFactory.md).

---

## Public structure naming

Every public structure in the Runtime C ABI - `WinMLRuntime.idl`,
`WinMLTensor.idl`, `WinMLTensorFactory.idl`, `WinMLConversation.idl`,
`WinMLTextGenerationTask.idl`, `WinMLChatCompletionTask.idl`,
and `WinMLAutomaticSpeechRecognitionTask.idl` - uses the
`WINML_UPPER_SNAKE_CASE` spelling, matching the enumerations declared beside it.
Interfaces keep the `IWinMLPascalCase` spelling.

---

## Fixed-layout evolution policy

Public structures in these files carry no size or version header. Their layouts
are fixed: callers zero-initialize a structure and then set the fields they use,
callees fully initialize the structures they return, and compiler-inserted
padding has no defined meaning. Padding must not be used as versioning
storage, and new reserved fields must not be added solely to occupy it.

`WINML_TEXT_GENERATION_SEQUENCE_STATE` and
`WINML_CONSTRAINT_VOCABULARY_IDENTITY` have no reserved member; their
compiler padding is ignored. The vocabulary identity consists of
`UINT32 tokenCount` followed by `BYTE sha256[32]`: it is 36 bytes, with the
digest at offset 4.

The Task and Conversation IDLs restate this policy at the owning API.

---

## Task, Conversation, and Server structure index

Each exact type name links to its family reference (or the declaration below).
The named public headers provide the authoritative member names, types, and
order. This index adds navigation and ownership guidance, not full declaration
and field-by-field reference coverage; that expansion remains incomplete.

| Structure | Public header | Direction and ownership |
|---|---|---|
| [`WINML_TEXT_GENERATION_OPTIONS`](#winml_text_generation_options) | `WinMLTextGenerationTask.h` | Caller input copied by the options factory; resolved value output from `GetValues`. |
| [`WINML_TEXT_GENERATION_SEQUENCE_STATE`](IWinMLTextGenerationTask.md#capabilities-and-sequence-state) | `WinMLTextGenerationTask.h` | Fully initialized session disclosure output; no pointers. |
| [`WINML_TEXT_GENERATION_CAPABILITIES`](IWinMLTextGenerationTask.md#capabilities-and-sequence-state) | `WinMLTextGenerationTask.h` | Composition support and current sequence-state snapshot; no borrowed buffers. |
| [`WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS`](IWinMLTextGenerationTask.md#speculative-decoding) | `WinMLTextGenerationTask.h` | Caller input copied by `SetSpeculativeOptions`: `method` and `draftTokenCount`; no pointers. |
| [`WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS`](IWinMLTextGenerationTask.md#speculative-decoding) | `WinMLTextGenerationTask.h` | Fully initialized terminal output from `GetSpeculativeStatistics`; no pointers. |
| [`WINML_SEGMENTED_SEQUENCE_CAPABILITIES`](IWinMLSegmentedSequenceStage.md#getcapabilities) | `WinMLRuntime.h` | Read-only encoder-construction/embedding-input support and optional known embedding shape. |
| [`WINML_WHISPER_AUTOMATIC_SPEECH_RECOGNITION_BINDINGS`](IWinMLAutomaticSpeechRecognitionTask.md#configuration) | `WinMLAutomaticSpeechRecognitionTask.h` | Atomic input binding set of seven typed interface pointers plus `BOOL forceEncoderOutputCopy`; getter returns each non-null interface AddRef'd. No size/version fields. |
| [`WINML_CHAT_COMPLETION_PROMPT_CACHE`](IWinMLChatCompletionTask.md#sequence-reuse-and-token-counts) | `WinMLChatCompletionTask.h` | Fully initialized terminal accounting output; no pointers. |
| [`WINML_AUTOMATIC_SPEECH_RECOGNITION_WAVEFORM_METADATA`](IWinMLAutomaticSpeechRecognitionTask.md#session-input-paths) | `WinMLAutomaticSpeechRecognitionTask.h` | Caller input: `UINT32 sampleRate`, `UINT32 channelCount`, `UINT64 validSampleCount`, in that order. No waveform storage is embedded. |
| [`WINML_CONVERSATION_REQUEST`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input and nested pointers valid through the synchronous formatting/Chat Generate call. |
| [`WINML_CONVERSATION_MESSAGE`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input strings, content parts, and tool-call arrays within a request. |
| [`WINML_CONVERSATION_TOOL`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input tool schema and strings within a request. |
| [`WINML_CONVERSATION_TEMPLATE_VALUE`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input typed value and optional string/JSON pointers within a request. |
| [`WINML_CONVERSATION_CONTENT_PART`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input typed content and optional string/JSON pointers within a message. |
| [`WINML_CONVERSATION_TOOL_CALL`](IWinMLStructuredConversationFormatter.md) | `WinMLConversation.h` | Caller input call identity, name, arguments, and optional extension strings within a message. |
| [`WINML_CONSTRAINT_VOCABULARY_IDENTITY`](IWinMLTokenizerConstraintFactory.md) | `WinMLConversation.h` | Vocabulary identity output for comparison; inline hash, no pointers. |
| [`WINML_TOKEN_CONSTRAINT_CANDIDATE`](IWinMLTokenizerConstraintFactory.md) | `WinMLConversation.h` | Token/logit value in a caller-owned candidate array. |
| [`WINML_TOKEN_CONSTRAINT_CANDIDATE_BUFFER`](IWinMLTokenizerConstraintFactory.md) | `WinMLConversation.h` | In/out caller-owned candidate storage for in-place filtering; not transferred. |
| [`WINML_TOKEN_CONSTRAINT_STATE`](IWinMLTokenizerConstraintFactory.md) | `WinMLConversation.h` | Fully initialized constraint-state output; no pointers. |
| [`WINML_SERVER_OPTIONS`](IWinMLServer.md#options) | `WinMLServer.h` | Caller input copied by `CreateServer`; initialize it with `WinMLMakeDefaultServerOptions`. |
| [`WINML_SERVER_MODEL_REGISTRATION`](IWinMLServer.md#registermodel) | `WinMLServer.h` | Caller input valid through the synchronous `RegisterModel` call; the server copies the strings and retains the lane objects. |
| [`WINML_SERVER_LANE`](IWinMLServer.md#lanes) | `WinMLServer.h` | Caller input interface pointers; successful registration retains them until `Stop` completes or the server is released. |
| [`WINML_SERVER_MODEL_CAPABILITIES`](IWinMLServer.md#capabilities) | `WinMLServer.h` | Caller input embedded in the registration; no pointers. |
| [`WINML_SERVER_MODEL_CONTEXT`](IWinMLServer.md#iwinmlservermodelcontext) | `WinMLServer.h` | Caller input copied by `SetModelContext`; no pointers. |

## WINML_TEXT_GENERATION_CAPABILITIES

Declared in `WinMLTextGenerationTask.h`.

```cpp
typedef struct WINML_TEXT_GENERATION_CAPABILITIES
{
    WINML_TEXT_GENERATION_EXECUTION_PROFILE executionProfile;
    WINML_TEXT_GENERATION_OUTPUT_KIND outputKind;
    BOOL supportsContinuation;
    BOOL supportsConstraints;
    BOOL supportsSegmentedPrompts;
    BOOL supportsEmbeddingSegments;
    BOOL supportsConstrainedContinuation;
    BOOL supportsConstrainedEmbeddingSegments;
    WINML_TEXT_GENERATION_SEQUENCE_STATE sequenceState;
} WINML_TEXT_GENERATION_CAPABILITIES;
```

The session fills every field. See
[capability snapshots](IWinMLTextGenerationTask.md#capabilities-and-sequence-state) for
what each one reports and when the snapshot is taken.

## WINML_TEXT_GENERATION_OPTIONS

Declared in `WinMLTextGenerationTask.h`.

```cpp
typedef struct WINML_TEXT_GENERATION_OPTIONS
{
    WINML_TEXT_GENERATION_OPTION_FIELDS presentFields;
    UINT32 maxNewTokens;
    float temperature;
    UINT32 topK;
    float topP;
    float minP;
    float repetitionPenalty;
    UINT32 repetitionPenaltyWindow;
    float presencePenalty;
    float frequencyPenalty;
    UINT64 seed;
} WINML_TEXT_GENERATION_OPTIONS;
```

`WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS` is required in
`presentFields`. Omitted sampling fields resolve to `temperature=0`, `topK=0`,
`topP=1`, `minP=0`, `repetitionPenalty=1`, `repetitionPenaltyWindow=0`,
`presencePenalty=0`, `frequencyPenalty=0`, and `seed=0`. Unknown field bits are
rejected with `E_INVALIDARG`. The factory copies these values and the separately
supplied stop-token array into immutable options; `GetValues` returns every field
resolved with `WINML_TEXT_GENERATION_OPTION_FIELD_ALL`. See
[Text Generation option fields](IWinMLTextGenerationTask.md#option-fields).

---

## Structured conversation types

`WINML_CONVERSATION_REQUEST` is the input to
[`IWinMLStructuredConversationFormatter`](IWinMLStructuredConversationFormatter.md).
It references
caller-owned arrays of `WINML_CONVERSATION_MESSAGE`, `WINML_CONVERSATION_TOOL`, and
`WINML_CONVERSATION_TEMPLATE_VALUE` for the duration of the call.

Messages carry optional reasoning text, assistant tool-call arrays, tool result
identity, custom roles, simple string content, and typed content parts. Tool,
message, content-part, and call records include validated JSON extension points
and optional model-native identifiers. Unsupported extensions fail closed.
Template values are typed as Boolean, 64-bit integer, double,
string, or nested JSON. Deterministic time input uses Unix seconds and a fixed
timezone offset in minutes.

`WINML_CONVERSATION_MESSAGE::role` is an extensible string, not a closed enum.
`WinMLTokenizer.h` defines five well-known role strings:
`WINML_CHAT_ROLE_SYSTEM` (`L"system"`), `WINML_CHAT_ROLE_DEVELOPER`
(`L"developer"`), `WINML_CHAT_ROLE_USER` (`L"user"`),
`WINML_CHAT_ROLE_ASSISTANT` (`L"assistant"`), and `WINML_CHAT_ROLE_TOOL`
(`L"tool"`). Model templates may support other names; not every template
supports every role.

---

## Token constraint types

`WINML_CONSTRAINT_VOCABULARY_IDENTITY` carries the canonical vocabulary
SHA-256 and contiguous token count.
`WINML_TOKEN_CONSTRAINT_CANDIDATE_BUFFER` references caller-owned candidate
entries for in-place filtering.
`WINML_TOKEN_CONSTRAINT_STATE` reports active, completed, or poisoned state,
accepted generated-token count, and bounded grammar diagnostics.
`AWAITING_TRIGGER` distinguishes a lazy constraint that has not activated.
`CAN_STOP` indicates that stopping at the current grammar position is valid.
Flags are zero after completion or poisoning.

These structures carry no size or version header. Output structures are fully
initialized by the callee, and input structures are validated on their semantic
fields.
See
[`IWinMLTokenizerConstraintFactory`](IWinMLTokenizerConstraintFactory.md).

---
