<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTextGenerationTask

The Text Generation Task API runs autoregressive generation over caller-created
Runtime pipelines. It handles token sampling, optional constraints, streaming,
cancellation, and terminal results while preserving access to the supplied
Runtime composition.

## Composition flow

```text
IWinMLRuntime + target + tokenizer
  -> caller-built pipeline stage graph
  -> IWinMLTextGenerationConfiguration
  -> IWinMLTextGenerationTask
  -> IWinMLTextGenerationSession
  -> IWinMLTextGenerationPullStream
  -> IWinMLTextGenerationResult
```

A pipeline may contain one model stage or multiple connected stages. Model
layout does not change the Task API surface.

## Configuration profiles

| Profile | Configuration |
|---|---|
| Unified | Select `UNIFIED`, then bind the pipeline, token input, output, state-owner stage, token target, and tokenizer through the `UNIFIED` phase. |
| Prefill/decode | Select `PREFILL_DECODE`, then bind the `PREFILL` and `DECODE` pipelines, endpoints, logical capacity, decode-input mode, and optional caller-owned outputs on the same configuration object. |

Set the mode and configure each required endpoint before calling `Validate`.
Validation returns an error for incomplete or incompatible configurations.

`WINML_TEXT_GENERATION_DECODE_INPUT_MODE_CALLER_BOUND` supplies each decode token
through the configured token input. `WINML_TEXT_GENERATION_DECODE_INPUT_MODE_PIPELINE_FEEDBACK`
uses a next-iteration pipeline connection and requires sampled-token output.

## Task and session

Create immutable `IWinMLTextGenerationOptions`, then create the task with the
matching tokenizer and an EOS policy. `TOKENIZER_DEFAULT` requires a zero count and null EOS token array. `EXPLICIT`
copies the caller-supplied EOS token list and allows an empty list.

`IWinMLTextGenerationTask::CreateSession` consumes a configuration after
successful validation. A second `CreateSession` with the same consumed
configuration returns `E_NOT_VALID_STATE`; rejected configurations remain usable.
A configuration created by a different Runtime returns `E_INVALIDARG`.

The session supports:

- `GenerateText` for tokenizer-encoded text.
- `GenerateTokens` for caller-provided token IDs.
- `ContinueTokens` when the composition supports continuation.
- `Reset` after completion or recoverable failure.
- `Close` for deterministic teardown.
- `CreateMediaEncoderFromFile` and `GenerateSegments` for single-pipeline media and segmented prompts.

Query `IWinMLTextGenerationConstraintSession` to start generation with an
`IWinMLTokenConstraint` created by the same tokenizer. Query
`IWinMLTextGenerationConstraintContinuation` to continue a retained sequence
under a constraint when the composition supports it.

## Capabilities and sequence state

`GetCapabilities` reports the execution profile, output kind, optional operation
support, and current sequence state. It does not start generation or reserve
capacity.

`IWinMLTextGenerationSequenceDisclosure` reports whether a live session has no
retained sequence, a retained sequence that can be continued, or retained state
that cannot be continued. `GetRetainedTokenIds` returns a `CoTaskMemAlloc` token
array for retained token IDs. Compare token IDs, not text, when deciding whether
a new prompt can be continued.

`ContinueTokens` appends caller-supplied suffix tokens to the retained sequence.
If the suffix would exceed logical capacity, the call returns
`HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)` before mutating the session.

## Streaming and results

`IWinMLTextGenerationPullStream::ReadNext` returns generated fragments and token
IDs until completion. On `WINML_TEXT_GENERATION_READ_STATUS_COMPLETED`, `tokenId`
is zero and `fragment` is null. Update strings are borrowed until the next
`ReadNext` or `Close`.

Pass an `IWinMLCancellationSource` when starting generation and call `Cancel`
from any thread to request cancellation. Cancellation does not invalidate the
current fragment returned by `ReadNext`.

`IWinMLTextGenerationResult` reports generated text, token IDs, finish reason,
the terminal HRESULT, and prompt token count.

## Threading and teardown

Task methods read immutable metadata or create a separate session on the calling
thread. Sessions, capability and sequence-disclosure views, and streams use the
session creation thread. Calls from another thread return `RPC_E_WRONG_THREAD`
where the implementation performs the creation-thread check.

Call session and stream `Close` on the creation thread. Repeating a successful
`Close` returns `S_OK`. Session `Close` rejects an active stream with
`E_NOT_VALID_STATE`; close or finish the stream first. After session closure,
`GetState` reports `WINML_TEXT_GENERATION_SESSION_STATE_CLOSED`; `GetRuntime` and
`GetConfiguration` remain available. After stream closure, `ReadNext` and
`GetResult` return `E_NOT_VALID_STATE`, so retrieve a terminal result before
closing the stream if it is needed.

## Option fields

`WINML_TEXT_GENERATION_OPTIONS::presentFields` is a
`WINML_TEXT_GENERATION_OPTION_FIELDS` bitmask. Bits outside
`WINML_TEXT_GENERATION_OPTION_FIELD_ALL` are rejected with `E_INVALIDARG`.
`WINML_TEXT_GENERATION_OPTION_FIELD_MAX_NEW_TOKENS` is required and must be
nonzero. `IWinMLTextGenerationOptions::GetValues` returns resolved values with
`presentFields` set to `WINML_TEXT_GENERATION_OPTION_FIELD_ALL`.

When omitted, resolved sampling defaults include `topP = 1.0f`,
`repetitionPenalty = 1.0f`, and zero-valued numeric fields for the remaining
options.

## Speculative decoding

Speculative decoding is off by default. Query the configuration for
`IWinMLTextGenerationSpeculativeConfiguration` and call `SetSpeculativeOptions`
before `Validate` to select how draft tokens are proposed:

| Method | Draft source |
|---|---|
| `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL` | The state owner's own draft predictor, through `IWinMLStatefulStage::ProposeDraftTokens`. Enable it before Build with `IWinMLStatefulStageOptions::SetDraftTokenLimitHint` and `SetModelDraftPredictorEnabled`. The predictor is the model's multi-token prediction layers, or a companion block draft model (DFlash, DFlash2, or DSpark) named with `SetDraftPredictorPath`. |
| `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL` | A separate, smaller model that shares the tokenizer, supplied with `SetDraftPipeline`. Its pipeline must differ from the target pipeline, take token IDs, and output logits. |
| `WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP` | Longest-suffix n-gram matches in the prompt and generated history. No model support is needed. |

`WINML_TEXT_GENERATION_SPECULATIVE_OPTIONS::draftTokenCount` sets the most draft
tokens per verification step; zero selects the default of three. The Task
clamps it to what the stage can verify and roll back. When the target is a
recurrent or hybrid model, also set the draft token limit hint on the stage
before Build so the stage retains enough state to roll back rejected draft
tokens.

Each verification step submits the pending token and the draft tokens to
`IWinMLStatefulStage::StepAllPositions`, samples the returned logits rows in
order with the session's sampler, and keeps draft tokens while the sampled token
equals the draft. Sampling settings and token constraints apply to every
verified row as they do sequentially. A multi-position step can differ
numerically from single-position steps on some models and accelerators, so a
token whose probability is nearly tied with another can be selected differently
than in sequential decoding. Steps stay within the remaining output-token budget
and sequence capacity, and draft tokens stop before a known end-of-sequence or
stop token. A stream decodes sequentially when its prompt contains media or its
token constraint cannot be cloned.

`CreateSession` returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the
selected method cannot run on the composition: a prefill/decode profile or
non-logits output, a state owner that is not the output stage, a stage that
cannot return a logits row per consumed token or cannot roll back, or `MODEL` on
a stage whose `WINML_SPECULATIVE_CAPABILITIES::maxDraftTokens` is zero. A
rejected configuration is not consumed, so the caller can select another method
and retry.

Query the terminal result for `IWinMLTextGenerationSpeculativeResult` and call
`GetSpeculativeStatistics` to read the effective method and the verification
step, drafted token, and accepted draft token counts. The method is `NONE` when
the stream decoded sequentially.

## C++ composition helpers

`winml/tasks/text_generation/TextGeneration.hpp` provides `BuildTextGeneration`
for a caller-built unified pipeline. Set
`TextGenerationArguments::speculative` to a `SpeculativeDecoding` value to
select a speculative method and draft token count and, for `DRAFT_MODEL`, the
draft pipeline and its endpoints.

`winml/tasks/text_generation/PrefillDecodeTextGeneration.hpp` provides
`BuildPrefillDecodeTextGeneration` for caller-built prefill and decode
pipelines.

`winml/tasks/text_generation/onnx/TextGeneration.hpp` provides
`onnx::BuildTextGeneration`. It requires token-input and output names, resolves
them through `IWinMLOrtModelSchema`, applies optional symbolic-dimension and
sequence-capacity values, and returns the generic Text Generation composition.

For models with persistent tensor state, populate
`winml::tasks::text_generation::onnx::BuildTextGenerationArguments::stateTensorPairs`.
Each `StateTensorPair` contains `UINT32 inputIndex` and `UINT32 outputIndex`.
The helper does not infer persistent state from names, shapes, or shared storage.

The helpers do not load files, select targets or backends, discover model
layouts, or own model caches.
