<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTextGenerationTask

The Text Generation Task API runs autoregressive generation over a model that
the application gives it or over caller-created Runtime pipelines. It handles
token sampling, optional constraints, streaming, cancellation, and terminal
results while preserving access to the Runtime objects underneath.

## Composition flow

```text
IWinMLRuntime + target + tokenizer
  -> a loaded model (SetModel), a described composition (SetComposition),
     or a caller-built pipeline stage graph
  -> IWinMLTextGenerationConfiguration
  -> IWinMLTextGenerationTask
  -> IWinMLTextGenerationSession
  -> IWinMLTextGenerationPullStream
  -> IWinMLTextGenerationResult
```

A pipeline may contain one model stage or multiple connected stages. Model
layout does not change the Task API surface.

## Sessions from a model

Query a configuration created by `CreateTextGenerationConfiguration` for
`IWinMLModelTextGenerationConfiguration` (IID `984e9699-2339-4c7c-88ea-c1b770945ad0`)
to give the Task a model instead of a pipeline. How the sessions run the model
follows from how the application loaded it. Windows ML does not choose among
engines for the application and never switches from one to another.

| Pattern | The application | The sessions |
|---|---|---|
| Bring your own weights | Loads a published model with [`IWinMLRuntime::LoadModelFromPath`](IWinMLRuntime.md#loadmodelfrompath) (a checkpoint folder) or [`LoadModelFromFile`](IWinMLRuntime.md#loadmodelfromfile) (a GGUF file), then calls `SetModel`. | Windows ML reads the model from the path it was loaded from, plans it into programs for the CGC backend, and runs it on the target the application picked. It supports model folders whose `config.json` it recognizes and GGUF files whose header declares an architecture, layout, and weight types it supports. It decides from the model's metadata. Any other model fails `SetModel` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |
| Bring your own model | Loads each model of a composition, sets the tokenizer with `SetTokenizer`, describes the models with `WINML_TEXT_GENERATION_COMPOSED_MODEL`, and calls `SetComposition`. | Each `CreateSession` composes the described models from Runtime stages. See [Composed models](#composed-models). |
| llama.cpp, an explicit choice | Loads a GGUF file, and any draft model, with [`IWinMLLlamaModelLoader::LoadModelFromFile`](IWinMLLlamaModelLoader.md), then calls `SetModel`. | Each `CreateSession` composes a llama.cpp stage. See [llama.cpp sessions](#llamacpp-sessions). This is the only way `SetModel` uses llama.cpp. |

A configuration wired by hand with `SetPipeline` and the other composition
setters remains the path for full control. The two are exclusive: `SetModel`
and `SetComposition` return `E_NOT_VALID_STATE` for a configuration that holds a
pipeline, a stage, a token target, a caller output binding, a draft pipeline,
or the `PREFILL_DECODE` mode. After `SetModel` or `SetComposition`, `SetMode`,
`SetPipeline`, `SetTokenInput`, `SetOutput`, `SetStateOwnerStage`,
`SetTokenTarget`, `SetTokenizer`, `SetDecodeInputMode`,
`AddCallerOutputBinding`, and `SetDraftPipeline` return `E_NOT_VALID_STATE`, and
`Validate` succeeds. A later `SetModel` or `SetComposition` replaces the binding;
a failed call leaves the configuration unchanged.

The session that `CreateSession` returns is an ordinary
`IWinMLTextGenerationSession`. Generation, continuation, streaming,
cancellation, token counts, and finish reasons work as for a hand-wired session,
and a chat completion task can use it.

| Behavior | Detail |
|---|---|
| Tokenizer | Without a tokenizer, `SetModel` creates the model's own tokenizer from its files: for a GGUF file, the file itself, or the `tokenizer.json` beside it when the file's own tokenizer cannot be created; for a model folder, `tokenizer.json` at its root, then `tokenizer\tokenizer.json`. A `tokenizer.json` is read with the `tokenizer_config.json` beside it, and without that file `SetModel` returns `HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`. That tokenizer becomes the configuration's tokenizer. Create the task with `GetTokenizer`; another tokenizer makes `CreateSession` return `E_INVALIDARG`. A model without a tokenizer source, such as one loaded from a buffer, returns `E_NOT_VALID_STATE`. A tokenizer set with `SetTokenizer` before `SetModel` must be the model's tokenizer. |
| Model files | For a model folder, Windows ML reads `config.json`, the weights in `model.safetensors` or `model.safetensors.index.json` with every shard it names, and `generation_config.json` when present. A missing required file returns `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)`. A GGUF file is read alone. |
| Capacity | Call `SetLogicalCapacity` after `SetModel` to set the largest sequence, in tokens, that a session holds. Without it a session holds the model's default sequence length. |
| Program cache | `WINML_TEXT_GENERATION_MODEL_OPTIONS::compiledProgramDirectory` names a directory where sessions store the CGC programs they compile for the GPU and reuse them in later processes, and `compiledProgramStorage` says whether it also keeps the weights prepared for each program. The first session of a model compiles its programs, which can take several seconds. See [Compiled programs and stored weights](IWinMLTasks.md#compiled-programs-and-stored-weights). |
| Draft model | `WINML_TEXT_GENERATION_MODEL_OPTIONS::draftModel` is accepted only where the model's route supports a supplied draft, and both models must be loaded the same way. Published weights accept a draft model that `IWinMLRuntime::LoadModelFromPath` loaded from a folder and that runs as the model's companion draft predictor, such as a DFlash 2 drafter checkpoint; see [Speculative decoding with published weights](#speculative-decoding-with-published-weights). The llama.cpp route accepts a draft model that `IWinMLLlamaModelLoader` also loaded, and `CreateSession` returns `E_INVALIDARG` when llama.cpp doesn't accept its vocabulary for the model's. Any other draft model returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |
| Session creation | `CreateSession` prepares the model for the target and returns the Runtime error when the target cannot run it, or `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when the session cannot honor the logical capacity or the speculative options. A failed `CreateSession` leaves the configuration reusable. |
| Requests | A session from published weights generates from token runs: `CreateMediaEncoderFromFile` and `GenerateSegments` return `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. A request value it cannot honor fails the call that presents it with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` before the session changes. `GetCapabilities` reports continuation and constraint support. |
| Sessions | Sessions of one model share no mutable state. Each holds its own token sequence. Windows ML may share read-only model data, such as weights, among the sessions of a process. |

`SetModel` reports errors in a fixed order, so a model that the call never
accepts returns the same error with or without a tokenizer: `E_POINTER` for a
null target or model; `E_INVALIDARG` for a model or draft model that the
Windows ML Runtime did not load, an unknown option flag, or a present option
field that is null or empty; `E_NOT_VALID_STATE` for a reserved, consumed, or
hand-wired configuration; then the tokenizer errors above; then, while the model
binds, `HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)` when the model's files no
longer exist, `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a model or option
that is not supported, `HRESULT_FROM_WIN32(ERROR_DIRECTORY)` when the program
cache names an existing file, `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)` or
`HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` for malformed model files, and file
errors such as `HRESULT_FROM_WIN32(ERROR_SHARING_VIOLATION)`.

```cpp
wil::com_ptr<IWinMLTextGenerationConfiguration> configuration;
RETURN_IF_FAILED(textFactory->CreateTextGenerationConfiguration(configuration.put()));
wil::com_ptr<IWinMLModelTextGenerationConfiguration> modelConfiguration;
RETURN_IF_FAILED(configuration->QueryInterface(IID_PPV_ARGS(modelConfiguration.put())));

// A checkpoint folder; LoadModelFromFile loads a GGUF file the same way.
wil::com_ptr<IWinMLModel> model;
RETURN_IF_FAILED(runtime->LoadModelFromPath(modelFolder, nullptr, model.put()));

WINML_TEXT_GENERATION_MODEL_OPTIONS options{};
options.presentFields = WINML_TEXT_GENERATION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
options.compiledProgramDirectory = programCacheDirectory;
RETURN_IF_FAILED(modelConfiguration->SetModel(gpuTarget.get(), model.get(), &options));

// SetModel created the model's tokenizer; the task uses the same one.
wil::com_ptr<IWinMLTokenizer> tokenizer;
RETURN_IF_FAILED(configuration->GetTokenizer(tokenizer.put()));
wil::com_ptr<IWinMLTextGenerationTask> task;
RETURN_IF_FAILED(textFactory->CreateTextGenerationTask(
    tokenizer.get(), WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT, 0, nullptr, task.put()));
wil::com_ptr<IWinMLTextGenerationSession> session;
RETURN_IF_FAILED(task->CreateSession(configuration.get(), session.put()));
```

In Python, `Runtime.load_model` loads a folder or a file, and
`TextGenerationConfiguration.set_model(target, model, *, draft_model=None,
compiled_program_directory=None, store_compiled_program_weights=False)` binds
it; read `configuration.tokenizer` for the task. See the
[Models-backed Task samples](../../Samples/Tasks/model-backed/README.md).

### Composed models

An application that exported its own graphs describes them with
`WINML_TEXT_GENERATION_COMPOSED_MODEL` and binds the description with
`SetComposition(target, description, options)`. Each `CreateSession` composes
the models on the target exactly as an application would wire them by hand
from the same models, endpoints, and state pairs, and each model keeps the
backend its load selected.

| Kind | Roles | Composition |
|---|---|---|
| `WINML_TEXT_GENERATION_COMPOSITION_KIND_UNIFIED` | `model` | One pipeline of one model stage that takes the token identifiers at `tokenInputIndex`, produces `outputIndex` of `outputKind`, and owns the sequence through its state pairs. |
| `WINML_TEXT_GENERATION_COMPOSITION_KIND_EMBEDDING_DECODER` | `tokenEmbedding`, `model`, `head` | One pipeline: the token embedding maps token identifiers to embeddings, the decoder (`model`) takes them at input 0, produces hidden states at output 0, and owns the sequence, and the head maps them to logits. Every other decoder input and output is a state pair. |
| `WINML_TEXT_GENERATION_COMPOSITION_KIND_PREFILL_DECODE` | `prefill`, `decode` | A prefill pipeline and a decode pipeline of one model stage each, with `decodeInputMode` and `logicalCapacity` present. |

| Behavior | Detail |
|---|---|
| Description | Every role the kind uses names a model that `LoadModelFromFile` or `LoadModelFromPath` loaded, and every role it does not use is zero-initialized. Endpoints are indices; resolve names through the model's schema. A stage holds at most 128 state pairs, and an index appears in at most one pair. `PREFILL_DECODE` requires `decodeInputMode` and a `logicalCapacity` from 1 to 1048576 tokens. |
| Tokenizer | A composition has no tokenizer files. Call `SetTokenizer` before `SetComposition`, which otherwise returns `E_NOT_VALID_STATE`, and create the task with the same tokenizer. |
| Capacity | `SetLogicalCapacity` after `SetComposition` becomes the state owner's sequence capacity hint for `UNIFIED` and `EMBEDDING_DECODER`, and replaces the description's logical capacity for `PREFILL_DECODE`. A state owner whose stage resolves no capacity without a hint, such as an ONNX Runtime stage, fails `CreateSession` with `HRESULT_FROM_WIN32(ERROR_INVALID_DATA)`, as the same composition wired by hand without `IWinMLStatefulStageOptions::SetSequenceCapacityHint` does. Set the capacity for such a model. |
| Speculation | `NONE` runs. `PROMPT_LOOKUP` runs for `UNIFIED` with `LOGITS` output when the state owner can verify and roll back drafts. `MODEL` returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` and `DRAFT_MODEL` returns `E_INVALIDARG` at `CreateSession`. A composition takes no draft model. |
| Errors | `E_POINTER` for a null target or description; `E_INVALIDARG` for a description these rules refuse; `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a model that no stage runs, such as a model folder, and for a draft model; `E_NOT_VALID_STATE` for a reserved, consumed, or hand-wired configuration, or one without a tokenizer. A model whose inputs or outputs do not match its role fails `CreateSession` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` or the Runtime error. |

```cpp
// An exported decoder with token input 0, logits output 0, and one state pair.
const UINT32 stateInputs[] = {1};
const UINT32 stateOutputs[] = {1};
WINML_TEXT_GENERATION_COMPOSED_MODEL description{};
description.kind = WINML_TEXT_GENERATION_COMPOSITION_KIND_UNIFIED;
description.model.model = decoder.get();
description.model.tokenInputIndex = 0;
description.model.outputIndex = 0;
description.model.outputKind = WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS;
description.model.statePairCount = 1;
description.model.stateInputIndices = stateInputs;
description.model.stateOutputIndices = stateOutputs;

RETURN_IF_FAILED(configuration->SetTokenizer(tokenizer.get()));
RETURN_IF_FAILED(modelConfiguration->SetComposition(target.get(), &description, nullptr));
RETURN_IF_FAILED(configuration->SetLogicalCapacity(declaredCapacity));
```

In Python, pass a `TextGenerationComposedModel` of `TextGenerationComposedStage`
values to `TextGenerationConfiguration.set_composition`.

### llama.cpp sessions

For a GGUF model that `IWinMLLlamaModelLoader` loaded, `SetModel` reads none of
the weights. Each `CreateSession` composes one llama.cpp model stage on the
target in the `UNIFIED` mode, the composition an application builds by hand:
token identifiers at input 0, logits at output 0, and the stage owns the
sequence. Each session loads the weights, so a file that llama.cpp cannot load
fails `CreateSession` with the Runtime error. Without the llama.cpp payload
deployed with the application, `CreateSession` returns
`HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)`.

| Behavior | Detail |
|---|---|
| Capacity | `SetLogicalCapacity` sets the stage's sequence capacity hint. Without it the stage holds the model's declared context. |
| Speculation | `DRAFT_MODEL` composes the draft model as a second `UNIFIED` stage on the same target and uses it as the draft pipeline. `MODEL` uses the stage's own draft predictor, or the draft model as its companion predictor. `DRAFT_MODEL` without a draft model, and `NONE` or `PROMPT_LOOKUP` with one, fail `CreateSession` with `E_INVALIDARG` before the weights are read. |
| Options | The program cache directory is ignored. |
| Composition | After `CreateSession`, the consumed configuration's getters (`GetMode`, `GetPipeline`, `GetStateOwnerStage`, `GetTokenTarget`, `GetDraftPipeline`, and the others) report the composed objects, so an application can continue with a custom composition of the same stages. |

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
`presentFields` set to `WINML_TEXT_GENERATION_OPTION_FIELD_ALL`. That value grows
when fields are added: Windows ML 2.9 adds
`WINML_TEXT_GENERATION_OPTION_FIELD_WATERMARK_MODE` and the `watermarkMode`
field, so compare with the named constant, never a literal, and rebuild against
the current headers.

When omitted, resolved sampling defaults include `topP = 1.0f`,
`repetitionPenalty = 1.0f`, `watermarkMode =
WINML_TEXT_GENERATION_WATERMARK_MODE_DEFAULT`, and zero-valued numeric fields
for the remaining options.

## Watermarking

Query a configuration for `IWinMLTextGenerationWatermarkConfiguration` and call
`SetWatermarkOptions` before `CreateSession` to embed a statistical watermark in
the tokens a session samples. A keyed pseudorandom function of the preceding
generated tokens selects among the candidates in proportion to their
probabilities, so each token's distribution is the model's own and the text
format does not change. A detector that holds the key scores a token sequence
without the model or the prompt.

| Algorithm | Behavior |
|---|---|
| `WINML_TEXT_GENERATION_WATERMARK_ALGORITHM_GUMBEL` | Every watermarked token uses the configured key. |
| `WINML_TEXT_GENERATION_WATERMARK_ALGORITHM_DUAL_KEY_GUMBEL` | Each watermarked token uses a second derived key with probability `alpha`, so regenerated responses can vary, with a weaker signal per token. |
| `WINML_TEXT_GENERATION_WATERMARK_ALGORITHM_SYNTHID` | Tournament sampling over `depth` keyed layers; the token is drawn with the request's own randomness, so regenerated responses differ. |

A watermark applies only where the session samples a token from logits on the
host with a positive temperature. Greedy decoding (temperature 0, the default,
or `topK` 1) and `SAMPLED_TOKEN` output are not watermarked, so configuring a
watermark does not by itself watermark a default request. A token whose context
already occurred in the searched history (`contextScope`) is sampled without
the watermark. A request can opt out with
`WINML_TEXT_GENERATION_WATERMARK_MODE_DISABLED`; with
`WINML_TEXT_GENERATION_WATERMARK_ENFORCEMENT_REQUIRED`, that request fails with
`HRESULT_FROM_WIN32(ERROR_ACCESS_DISABLED_BY_POLICY)` and a greedy or
`SAMPLED_TOKEN` request fails with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.
`BEST_EFFORT` runs them and counts the tokens it did not watermark. Speculative
decoding keeps the watermark on every committed token.

Query the terminal result for `IWinMLTextGenerationWatermarkResult` to read
`WINML_TEXT_GENERATION_WATERMARK_STATISTICS`, which counts watermarked and
unwatermarked tokens and reports the key identifier, never the key. To check
text, query the text generation factory for
`IWinMLTextGenerationWatermarkDetector` and call `DetectWatermark` with the
generated token IDs and the same options; a small `pValue`, such as below 1e-3,
indicates the watermark. A watermark is cooperative provenance, not tamper
evidence: editing or paraphrasing weakens it, and the key is only as secret as
the process that holds it.

Python exposes `TextGenerationConfiguration.set_watermark(...)`,
`TextGenerationOptions(watermark=...)`, `TextGenerationResult.watermark_statistics`,
and `Tasks.detect_watermark(...)`.

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

### Speculative decoding with published weights

A session from published weights honors the same speculative options. Windows
ML maps them onto the session's stage, and speculation stays off until a method
other than `NONE` is set.

| Method | Draft model | Behavior |
|---|---|---|
| `NONE` | none | Sequential decoding. A draft model fails `CreateSession` with `E_INVALIDARG`. |
| `MODEL` | optional | With a draft model, the session drafts with it as the model's companion draft predictor, such as a DFlash 2 drafter. Without one, it drafts with the multi-token prediction layers the model carries. A model without them, or a draft model that can't run with the model, fails `CreateSession` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |
| `PROMPT_LOOKUP` | none | Drafts tokens that followed an earlier occurrence of the end of the sequence. A model that can't verify drafts without a predictor, such as a recurrent model, fails `CreateSession` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. A draft model fails `CreateSession` with `E_INVALIDARG`. |
| `DRAFT_MODEL` | - | Fails `CreateSession` with `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. |

Windows ML never looks for a draft model on its own. Load a drafter with
`IWinMLRuntime::LoadModelFromPath` from the folder that holds it and pass it as
`WINML_TEXT_GENERATION_MODEL_OPTIONS::draftModel`. With a program cache, the
programs are keyed by both the model and the draft model, so replacing either
one compiles its programs again.

### Speculative method support

```
IWinMLTextGenerationSpeculativeSupport IID: 37825096-9132-47a6-8cc4-63a9d7d31909
```

Query a configuration for `IWinMLTextGenerationSpeculativeSupport` after
`SetModel` to learn what each method does for the bound model and draft model,
without creating a session, compiling anything, or reading weight data. The
interface is available where `IWinMLModelTextGenerationConfiguration` is;
elsewhere `QueryInterface` returns `E_NOINTERFACE`. Published weights and GGUF
models loaded with `IWinMLLlamaModelLoader` both report from the model's
metadata, so one code path serves both.

```cpp
HRESULT GetSpeculativeMethodSupport(
    [in] WINML_TEXT_GENERATION_SPECULATIVE_METHOD method,
    [out, retval] WINML_TEXT_GENERATION_SPECULATIVE_METHOD_SUPPORT* support);
```

`CreateSession` accepts a supported method with any `draftTokenCount`, clamped
to `maxDraftTokenCount`, and refuses an unsupported one with
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`. Failures to load or prepare the
weights, including draft tensors that don't fit the model, appear only at
`CreateSession`. `NONE` is always supported.

| Return value | Condition |
|---|---|
| `E_POINTER` | `support` is null. |
| `E_INVALIDARG` | `method` isn't a defined method. |
| `E_NOT_VALID_STATE` | No model is bound, including a configuration wired by hand, and `method` isn't `NONE`. |
| `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` | The route that serves the model reports speculative support only through its sessions, such as a composition. |

`WINML_TEXT_GENERATION_SPECULATIVE_METHOD_SUPPORT` holds the report.

| Field | Description |
|---|---|
| `supported` | Whether `CreateSession` accepts the method for the bound model and draft model. |
| `draftSource` | Where the method draws its draft tokens, or `WINML_TEXT_GENERATION_DRAFT_SOURCE_NONE` when it isn't supported. |
| `defaultDraftTokenCount` | The draft token count that a `draftTokenCount` of zero selects; zero when the method isn't supported. |
| `maxDraftTokenCount` | The most draft tokens one verification step takes. |
| `modelDraftLayersPresent` | Whether the model carries draft layers of its own. `MODEL` drafts with them when no draft model is bound. |

`WINML_TEXT_GENERATION_DRAFT_SOURCE` identifies the draft source.

| Value | Description |
|---|---|
| `WINML_TEXT_GENERATION_DRAFT_SOURCE_NONE` (0) | No draft tokens. The method is `NONE` or isn't supported. |
| `WINML_TEXT_GENERATION_DRAFT_SOURCE_MODEL_LAYERS` (1) | `MODEL` drafts with draft layers the model carries, such as multi-token prediction layers. |
| `WINML_TEXT_GENERATION_DRAFT_SOURCE_COMPANION_MODEL` (2) | `MODEL` drafts with the bound draft model, a companion draft predictor trained against the model, in place of the model's own draft layers. |
| `WINML_TEXT_GENERATION_DRAFT_SOURCE_DRAFT_MODEL` (3) | `DRAFT_MODEL` drafts with a separate model that shares the tokenizer and runs its own sequence. |
| `WINML_TEXT_GENERATION_DRAFT_SOURCE_SEQUENCE` (4) | `PROMPT_LOOKUP` drafts tokens of the sequence itself. |

In Python, `TextGenerationConfiguration.speculative_method_support(method)`
returns a `TextGenerationSpeculativeMethodSupport` with `supported`,
`draft_source`, `default_draft_token_count`, `max_draft_token_count`, and
`model_draft_layers_present`.

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
