<!-- Copyright (C) Microsoft Corporation. All rights reserved. -->

# IWinMLTasks

`IWinMLTasks` bootstraps Task API composition over a caller-created
`IWinMLRuntime`. Objects created through the Task API retain that Runtime
identity; the application still loads models, creates targets, builds pipelines,
and manages conversation history.

A task's supported composition depends on the supplied model, pipeline,
tokenizer, and execution target. The Task API uses the caller-provided Runtime
objects and does not expose a backend-selection switch.

Use the C++ activation helper:

```cpp
wil::com_ptr<IWinMLTasks> tasks;
RETURN_IF_FAILED(winml::tasks::Create(runtime.get(), tasks.put()));
```

Query the bootstrap for the task-family factory that is needed:

| Factory interface | Purpose |
|---|---|
| `IWinMLTextGenerationTaskFactory` | Text options, configuration, and task creation |
| `IWinMLChatCompletionTaskFactory` | Conversation formatting and text-session composition |
| `IWinMLAutomaticSpeechRecognitionTaskFactory` | Speech-recognition task creation with Whisper configuration |

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

## Ownership and threading

The bootstrap and task objects may retain caller-supplied Runtime, tokenizer,
pipeline, target, and options objects. Calls that use retained objects must also
follow those objects' threading and mutation rules.

Text-generation sessions, chat sessions, ASR sessions, streams, and live input
objects use the creation-thread checks described on their family pages. Request
cancellation is supplied with `IWinMLCancellationSource` when starting an
operation.
