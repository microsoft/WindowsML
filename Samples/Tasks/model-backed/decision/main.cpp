// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Typed decisions: ask a choice question about a state and read the answer's
// probabilities, confidence and recommendation. The task creates the model's
// own tokenizer from the model directory, so the app passes none.

#include "model_task.h"

#include <WinMLDecisionTask.h>

#include <string>

int wmain(int argumentCount, wchar_t** arguments)
try
{
    using namespace winmlsamples::tasks;

    LPCWSTR modelDirectory = nullptr;
    LPCWSTR state = L"I was charged twice for my subscription.";
    LPCWSTR programCache = nullptr;
    bool cacheWeights = false;
    int positionalCount = 0;
    bool validArguments = true;

    for (int index = 1; index < argumentCount && validArguments; ++index)
    {
        const std::wstring argument = arguments[index];
        if (argument == L"--program-cache" && index + 1 < argumentCount)
        {
            programCache = arguments[++index];
        }
        else if (argument == L"--cache-weights")
        {
            cacheWeights = true;
        }
        else if (argument.starts_with(L"--") || positionalCount == 2)
        {
            validArguments = false;
        }
        else if (positionalCount++ == 0)
        {
            modelDirectory = arguments[index];
        }
        else
        {
            state = arguments[index];
        }
    }

    if (!validArguments || modelDirectory == nullptr || (cacheWeights && programCache == nullptr))
    {
        std::fwprintf(stderr, L"Usage: task-decision.exe <model-directory> [state] "
                              L"[--program-cache <directory> [--cache-weights]]\n");
        return 2;
    }

    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(runtime->CreateExecutionTarget(WINML_EXECUTION_TARGET_KIND_GPU,
                                                   WINML_EXECUTION_TARGET_PREFERENCE_PERFORMANCE,
                                                   target.GetAddressOf()));
    ComPtr<IWinMLModel> model;
    THROW_IF_FAILED(runtime->LoadModelFromPath(modelDirectory, nullptr, model.GetAddressOf()));
    ComPtr<IWinMLTasks> tasks;
    THROW_IF_FAILED(WinMLCreateTasks(runtime.Get(), IID_PPV_ARGS(tasks.GetAddressOf())));
    ComPtr<IWinMLDecisionTaskFactory> factory;
    THROW_IF_FAILED(tasks.As(&factory));

    // Sessions compile programs for the GPU. A program cache directory keeps
    // them for later runs, and with cacheWeights also the weights prepared for
    // each program. Null options select the defaults: no program cache.
    WINML_DECISION_MODEL_OPTIONS modelOptions{};
    if (programCache != nullptr)
    {
        modelOptions.presentFields = WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_DIRECTORY;
        modelOptions.compiledProgramDirectory = programCache;
        if (cacheWeights)
        {
            modelOptions.presentFields |=
                WINML_DECISION_MODEL_OPTION_FIELDS_COMPILED_PROGRAM_STORAGE;
            modelOptions.compiledProgramStorage =
                WINML_COMPILED_PROGRAM_STORAGE_PROGRAMS_AND_WEIGHTS;
        }
    }

    // A null tokenizer asks the task to create the model's own.
    ComPtr<IWinMLDecisionTask> task;
    const HRESULT created = factory->CreateDecisionTaskFromModel(
        target.Get(), nullptr, model.Get(), programCache != nullptr ? &modelOptions : nullptr,
        task.GetAddressOf());
    if (IsNotSupported(created))
    {
        std::fwprintf(stderr, L"Windows ML cannot run this model for typed decisions on this GPU "
                              L"(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED)). Use a supported "
                              L"decision model directory and a GPU execution target.\n");
        return 1;
    }

    THROW_IF_FAILED(created);
    WINML_DECISION_CAPABILITIES capabilities{};
    THROW_IF_FAILED(task->GetCapabilities(&capabilities));
    std::wprintf(L"Maximum options: %u; calibrated probabilities: %ls\n",
                 capabilities.maximumOptionsPerQuestion, capabilities.calibrated ? L"yes" : L"no");

    const WINML_DECISION_OPTION options[] = {
        {L"billing", L"A question about an invoice or payment"},
        {L"technical", L"A problem using the product"},
    };
    THROW_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                capabilities.maximumOptionsPerQuestion < ARRAYSIZE(options));
    WINML_DECISION_QUESTION question{};
    question.kind = WINML_DECISION_QUESTION_KIND_CHOICE;
    question.instructions = L"Which team should handle this support ticket?";
    question.optionCount = ARRAYSIZE(options);
    question.options = options;
    const WINML_DECISION_REQUEST request{
        state,
        1,
        &question,
    };
    std::wprintf(L"State: %ls\nQuestion: %ls\n", request.state, question.instructions);

    ComPtr<IWinMLDecisionSession> session;
    THROW_IF_FAILED(task->CreateSession(session.GetAddressOf()));
    StreamCloser sessionCloser(session.Get());
    ComPtr<IWinMLCancellationSource> cancellation;
    THROW_IF_FAILED(tasks->CreateCancellationSource(cancellation.GetAddressOf()));
    ComPtr<IWinMLDecisionResult> result;
    THROW_IF_FAILED(session->Decide(&request, cancellation.Get(), result.GetAddressOf()));
    THROW_IF_FAILED(session->Close());

    // Results are immutable and remain readable after the session closes.
    WINML_DECISION_ANSWER answer{};
    THROW_IF_FAILED(result->GetAnswer(0, &answer));
    THROW_HR_IF(E_UNEXPECTED, answer.optionCount != ARRAYSIZE(options) ||
                                  answer.selectedOption >= ARRAYSIZE(options));
    float probabilities[ARRAYSIZE(options)]{};
    THROW_IF_FAILED(result->GetProbabilities(0, ARRAYSIZE(probabilities), probabilities));

    for (UINT32 index = 0; index < ARRAYSIZE(options); ++index)
    {
        std::wprintf(L"%ls: %.4f\n", options[index].label, probabilities[index]);
    }

    UINT64 inputTokenCount = 0;
    THROW_IF_FAILED(result->GetInputTokenCount(&inputTokenCount));
    std::wprintf(L"Selected: %ls; confidence: %.4f; recommendation: %ls\nInput tokens: %llu\n",
                 options[answer.selectedOption].label, answer.confidence,
                 answer.recommendation == WINML_DECISION_RECOMMENDATION_ACT ? L"act" : L"escalate",
                 inputTokenCount);
    return 0;
}
catch (...)
{
    winmlsamples::tasks::ReportFailure(L"Decision", wil::ResultFromCaughtException());
    return 1;
}
