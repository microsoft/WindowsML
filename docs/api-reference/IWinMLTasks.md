<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTasks

`IWinMLTasks` bootstraps Task API composition over a caller-created
`IWinMLRuntime`. Objects created through the Task API retain that Runtime
identity. The application loads models, creates targets, and manages
conversation history; it gives a Task either a loaded model or the pipelines it
built.

How a Task runs a model follows from how the application loaded it and from the
target it picked. A model loaded with `IWinMLRuntime::LoadModelFromPath` or
`LoadModelFromFile` runs through Windows ML on that target; a GGUF model loaded
with [`IWinMLLlamaModelLoader`](IWinMLLlamaModelLoader.md) runs on llama.cpp;
caller-built pipelines run on the backends of their stages. The Task API has no
backend-selection switch and never moves a model from one engine to another.

Use the C++ activation helper:

```cpp
wil::com_ptr<IWinMLTasks> tasks;
RETURN_IF_FAILED(winml::tasks::Create(runtime.get(), tasks.put()));
```

Query the bootstrap for the task-family factory that is needed:

| Factory interface | Purpose |
|---|---|
| `IWinMLTextGenerationTaskFactory` | Text options, configuration (from a model, a described composition, or caller-built pipelines), and task creation |
| `IWinMLChatCompletionTaskFactory` | Conversation formatting and text-session composition |
| `IWinMLAutomaticSpeechRecognitionTaskFactory` | Speech-recognition task creation, from a model or an encoder-decoder composition |
| `IWinMLDecisionTaskFactory` | Typed decision task creation from a model; see [IWinMLDecisionTask](IWinMLDecisionTask.md) |

A missing task family returns `E_NOINTERFACE` without preventing other families
from being used.

Use the typed C++ helpers for normal consumption:

```cpp
wil::com_ptr<IWinMLTextGenerationTaskFactory> textFactory;
RETURN_IF_FAILED(winml::tasks::GetTextGenerationFactory(
    tasks.get(),
    textFactory.put()));

wil::com_ptr<IWinMLChatCompletionTaskFactory> chatFactory;
RETURN_IF_FAILED(winml::tasks::GetChatCompletionFactory(
    tasks.get(),
    chatFactory.put()));

wil::com_ptr<IWinMLAutomaticSpeechRecognitionTaskFactory> speechFactory;
RETURN_IF_FAILED(winml::tasks::GetAutomaticSpeechRecognitionFactory(
    tasks.get(),
    speechFactory.put()));
```

The equivalent raw COM pattern is:

```cpp
wil::com_ptr<IWinMLChatCompletionTaskFactory> chatFactory;
RETURN_IF_FAILED(tasks->QueryInterface(
    IID_PPV_ARGS(chatFactory.put())));
```

Query for `IWinMLDecisionTaskFactory` the same way; it has no typed helper. It
is declared in `winml/tasks/WinMLDecisionTask.h`.

Raw COM callers link `WinMLTasks.lib` and call the reg-free factory export:

```cpp
IWinMLTasks* tasks = nullptr;
HRESULT result = WinMLCreateTasks(
    runtime,
    IID_IWinMLTasks,
    reinterpret_cast<void**>(&tasks));
RETURN_IF_FAILED(result);
tasks->Release();
```

`WinMLCreateTasks` returns `E_POINTER` for null required pointers. The output
interface is selected by `REFIID`; unsupported interfaces return
`E_NOINTERFACE`.


## `GetRuntime`

Returns the controlling Runtime supplied during activation.

## `IWinMLTaskRuntimeIdentity`

Text-generation configurations expose `IWinMLTaskRuntimeIdentity`; `GetRuntime`
returns the Runtime used to create the configuration. Typed session `GetRuntime`
methods are the primary session identity APIs.

## Compiled programs and stored weights

The model options of the text generation
([`WINML_TEXT_GENERATION_MODEL_OPTIONS`](IWinMLTextGenerationTask.md#sessions-from-a-model)),
speech recognition
([`WINML_AUTOMATIC_SPEECH_RECOGNITION_MODEL_OPTIONS`](IWinMLAutomaticSpeechRecognitionTask.md#models-backed-configuration)),
and decision ([`WINML_DECISION_MODEL_OPTIONS`](IWinMLDecisionTask.md#model-options))
Tasks can name a `compiledProgramDirectory` and, with each structure's
`..._MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE` flag, a
`compiledProgramStorage` that says what the directory keeps.

With a directory, sessions store each program they compile in it and reuse it
in later processes. A program is identified by the program, the execution
target, and the content of the model's files. By default the directory keeps
only the programs, and a session that reuses them prepares the weights from the
model's files again, one at a time as it reads them. Programs that share weights
also share them on the device, so for a language model, whose decode and
prefill programs share their weights, loading from the programs alone is about
as fast as loading from stored weights. With
`WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS` the directory also keeps
the weights prepared for each program, so a later session reads them instead
of preparing them. That saves the host the preparation work, at the cost of
about one copy of the prepared weights for each program on disk. Without a
directory, every session compiles every program.

A storage is valid only with a directory. A present `compiledProgramStorage`
without `compiledProgramDirectory`, or an unknown value, returns `E_INVALIDARG`.
A model that can't reuse compiled programs returns
`HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)` when a directory is present. A route
that doesn't use a program directory, such as llama.cpp text generation,
ignores it.

In Python, pass `store_compiled_program_weights=True` with
`compiled_program_directory` to `TextGenerationConfiguration.set_model`,
`AutomaticSpeechRecognitionConfiguration.set_model`, or
`Tasks.create_decision_task`.

### `WINML_COMPILED_PROGRAM_STORAGE`

Declared in `WinMLTasksRuntime.h`.

| Value | Description |
|---|---|
| `WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS` (0) | The compiled programs only. A session that reuses them prepares the weights from the model's files again, except those another program already put on the device. The default. |
| `WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS` (1) | The compiled programs and the weights prepared for them. A session that reuses them reads the prepared weights from the directory, and the directory holds about one copy of the prepared weights for each program. |

## Ownership and threading

The bootstrap and task objects may retain caller-supplied Runtime, tokenizer,
pipeline, target, and options objects. Calls that use retained objects must also
follow those objects' threading and mutation rules.

Text-generation sessions, chat sessions, ASR sessions, decision sessions,
streams, and live input objects use the creation-thread checks described on
their family pages. Request cancellation is supplied with
`IWinMLCancellationSource`, created by `CreateCancellationSource`, when starting
an operation.
