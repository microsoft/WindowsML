<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLDecisionTask

> Part of the [WinML Runtime API Reference](README.md). Declared in
> `winml/tasks/WinMLDecisionTask.h`.

A decision task evaluates a state, such as a message, a support ticket, or a
serialized document, against typed questions. For each question it returns a
probability distribution over the options, the selected option, a confidence,
and a recommendation to act or to escalate. A decision is a single evaluation
per question; no text is generated.

```
IWinMLDecisionTaskFactory  IID: a87f6ca1-2a69-43ac-baa2-56c6508d8edd
IWinMLDecisionTask         IID: 18856433-4413-4ae1-8291-ab62cd3908ea
IWinMLDecisionSession      IID: 21f65408-39b3-4244-85f1-ad0707fbafcb
IWinMLDecisionResult       IID: e3ef04d2-61e7-47a2-a41b-fce6f9f59bc8
```

## Create a task from a model

Load a published decision model folder with
[`IWinMLRuntime::LoadModelFromPath`](IWinMLRuntime.md#loadmodelfrompath), query
`IWinMLTasks` for `IWinMLDecisionTaskFactory`, and call
`CreateDecisionTaskFromModel(target, tokenizer, model, options, task)`. Windows
ML reads the model when the task is created and runs every model computation of
its sessions on the target the application picked. The target must be a GPU
target; a CPU target returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

`tokenizer` is optional. With null, the task creates the model's own tokenizer
from `tokenizer.json` at the root of the model folder, then from
`tokenizer\tokenizer.json`, read with the `tokenizer_config.json` beside it. A
tokenizer that the application passes must be the model's tokenizer and must
implement `IWinMLTokenizerMetadata`.

`options` is optional too. Null selects every default, which is no program
cache; see [Model options](#model-options).

`CreateDecisionTaskFromModel` reports errors in this order: `E_POINTER` for a
null target, model, or task; `E_INVALIDARG` for a model that the Windows ML
Runtime did not load, an unknown option flag, or a present option field that is
null or empty; then, without a tokenizer, `E_NOT_VALID_STATE` for a model
with no tokenizer source, such as one loaded from a buffer,
`HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND)` when the model's files no longer
exist, and the error of creating the tokenizer; then, while the model is read,
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` for a model that Windows ML does not
support or a target that cannot run it, `HRESULT_FROM_WIN32(ERROR_BAD_FORMAT)` or
`HRESULT_FROM_WIN32(ERROR_INVALID_DATA)` for malformed model files,
`E_NOINTERFACE` when the tokenizer does not implement `IWinMLTokenizerMetadata`,
`HRESULT_FROM_WIN32(ERROR_DIRECTORY)` when the options name a program cache
directory that is an existing file, and file errors such as
`HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)`. A model that
`IWinMLLlamaModelLoader` loaded returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.

`IWinMLDecisionTask::CreateSession` prepares the model for the target and returns
the Runtime error when the target cannot run it.

```cpp
wil::com_ptr<IWinMLModel> model;
RETURN_IF_FAILED(runtime->LoadModelFromPath(modelFolder, nullptr, model.put()));
wil::com_ptr<IWinMLDecisionTaskFactory> factory;
RETURN_IF_FAILED(tasks->QueryInterface(IID_PPV_ARGS(factory.put())));
wil::com_ptr<IWinMLDecisionTask> task;
RETURN_IF_FAILED(factory->CreateDecisionTaskFromModel(
    gpuTarget.get(), nullptr, model.get(), nullptr, task.put()));

wil::com_ptr<IWinMLDecisionSession> session;
RETURN_IF_FAILED(task->CreateSession(session.put()));

const WINML_DECISION_OPTION options[] = {
    {L"billing", L"A question about an invoice or payment"},
    {L"technical", L"A problem using the product"},
};
WINML_DECISION_QUESTION question{};
question.kind = WINML_DECISION_QUESTION_KIND_CHOICE;
question.instructions = L"Which team should handle this support ticket?";
question.optionCount = ARRAYSIZE(options);
question.options = options;
const WINML_DECISION_REQUEST request{L"I was charged twice for my subscription.", 1, &question};

wil::com_ptr<IWinMLDecisionResult> result;
RETURN_IF_FAILED(session->Decide(&request, nullptr, result.put()));
WINML_DECISION_ANSWER answer{};
RETURN_IF_FAILED(result->GetAnswer(0, &answer));
float probabilities[ARRAYSIZE(options)]{};
RETURN_IF_FAILED(result->GetProbabilities(0, ARRAYSIZE(probabilities), probabilities));
RETURN_IF_FAILED(session->Close());
```

In Python, call `tasks.create_decision_task(target, model, tokenizer=None,
compiled_program_directory=None, store_compiled_program_weights=False)` and
`session.decide(state, questions, cancellation=None)` with `DecisionQuestion`
and `DecisionOption` values. See the
[typed decisions sample](../../Samples/Tasks/model-backed/decision/).

## Model options

`WINML_DECISION_MODEL_OPTIONS` is borrowed for the duration of
`CreateDecisionTaskFromModel`, which copies it. Fields whose flag is absent from
`presentFields` take their defaults.

| Field | Description |
|---|---|
| `presentFields` | `WINML_DECISION_MODEL_OPTION_FIELDS` flags naming the present fields. |
| `compiledProgramDirectory` | With `WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY`, the directory in which sessions store the programs they compile and reuse them in later processes. It is created on first use. |
| `compiledProgramStorage` | With `WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE`, what the directory keeps for each program. Requires the directory. See [Compiled programs and stored weights](IWinMLTasks.md#compiled-programs-and-stored-weights). |

```cpp
WINML_DECISION_MODEL_OPTIONS modelOptions{};
modelOptions.presentFields = WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
modelOptions.compiledProgramDirectory = programCacheDirectory;
RETURN_IF_FAILED(factory->CreateDecisionTaskFromModel(
    gpuTarget.get(), nullptr, model.get(), &modelOptions, task.put()));
```

## Questions

| Kind | Options |
|---|---|
| `WINML_DECISION_QUESTION_KIND_CHOICE` | At least one. Each has a nonempty label, unique within the question, and an optional description. |
| `WINML_DECISION_QUESTION_KIND_SCORE` | At least one. Option `i` is level `i`, and its nonempty description states the criterion of that level. Labels are ignored. |
| `WINML_DECISION_QUESTION_KIND_YES_NO` | None, which uses the model's default description of both answers, or exactly two (false, then true). |

A question can set `WINML_DECISION_QUESTION_FIELDS_ACT_THRESHOLD` and
`actThreshold`, from 0 to 1: the answer recommends
`WINML_DECISION_RECOMMENDATION_ACT` when its confidence reaches the threshold
and `WINML_DECISION_RECOMMENDATION_ESCALATE` otherwise. Without a threshold, the
recommendation is `ACT` when the model's `actProbability` is at least 0.5.

`state` is required and may be empty. A structured state is passed in its
serialized text form. A state longer than the room a question leaves in the
model's sequence is truncated at its end.

## Answers

| Field of `WINML_DECISION_ANSWER` | Meaning |
|---|---|
| `selectedOption` | The most probable option. For `YES_NO`, 1 means true. |
| `confidence` | The probability of the selected option, calibrated when `WINML_DECISION_CAPABILITIES::calibrated` is `TRUE`. |
| `distributionConfidence` | One minus the normalized entropy of the distribution: 1 when all probability is on one option, 0 when uniform. Not a calibrated probability. |
| `expectedScore` | For `SCORE` questions, the expected level. Zero otherwise. |
| `actProbability` | The model's estimate that the answer can be acted on. |
| `recommendation` | `ACT` or `ESCALATE`, as described above. |

`IWinMLDecisionResult::GetProbabilities` copies the probabilities of one answer,
one per option in option order, and returns
`HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)` when the buffer is smaller than
the option count. `GetInputTokenCount` reports the tokens the model evaluated.
A recommendation is a model output, not permission to take an action.

## Errors from Decide

`Decide` is synchronous. It returns `E_POINTER` for a null request or result;
`E_INVALIDARG` for a missing state or instructions, no questions, an unknown
kind or field flag, an option count or option field outside the rules of the
question's kind, more options than `maximumOptionsPerQuestion`, a duplicate
`CHOICE` label, or a threshold outside [0, 1];
`HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)` when a question's options do not
fit in the model's sequence; `HRESULT_FROM_WIN32(ERROR_CANCELLED)` when
cancellation was requested, which is checked before each question; and
`E_NOT_VALID_STATE` after `Close`. A failed call returns no result.

## Threading

The factory and the task are free-threaded and need no `Close`. A session is
bound to the thread that created it: its methods return `RPC_E_WRONG_THREAD` from
another thread, and `Close` releases its execution resources and is idempotent.
Results are immutable and free-threaded, and remain readable after the session
closes.

## Availability

`IWinMLDecisionTaskFactory` and `WinMLDecisionTask.h` are available in packages
that include Models-backed Tasks. Where the factory is unavailable,
`CreateDecisionTaskFromModel` returns `HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)`.
