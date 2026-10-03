// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Chat completion: a two-turn conversation through the Chat Completion Task.
//
// Ask a question, then a follow-up that depends on the answer. The app keeps
// the message history and sends all of it on each turn.
//
//   A greedy Text Generation session on a CPU target backs a chat session
//   from the Chat Completion Task, so tokens it has already evaluated carry
//   over between turns. Each turn sends the full history; the session
//   applies the chat template, ReadNext streams content deltas, and
//   GetResult gives the reply, token counts, and finish reason. When a
//   prompt starts with tokens the session already holds, only the rest is
//   evaluated, and GetPromptCacheUse reports how many were reused. One
//   cancellation source serves both turns.
//
// Run it
//   ..\..\run_chat_completion.ps1 [-Backend ort|llama] [-Prompt <text>]
//                                  [-FollowUp <text>] [-MaxNewTokens <count>]
//
// Learn more (paths relative to this file)
//   README.md
//   ../../../../docs/Tasks/task-lifecycle.md
//   ../../../../docs/api-reference/IWinMLChatCompletionTask.md

#include "text_generation_task.h"

#include <cstdio>
#include <cwchar>
#include <iterator>
#include <string>
#include <vector>

namespace
{

using namespace winmlsamples::tasks;

LPCWSTR DescribeChatFinishReason(WINML_CHAT_COMPLETION_FINISH_REASON reason) noexcept
{
    switch (reason)
    {
    case WINML_CHAT_COMPLETION_FINISH_REASON_EOS_TOKEN:
        return L"end-of-sequence token";
    case WINML_CHAT_COMPLETION_FINISH_REASON_STOP_TOKEN:
    case WINML_CHAT_COMPLETION_FINISH_REASON_ADDITIONAL_STOP:
        return L"stop token";
    case WINML_CHAT_COMPLETION_FINISH_REASON_MAX_TOKENS:
        return L"reached max-new-tokens";
    case WINML_CHAT_COMPLETION_FINISH_REASON_SEQUENCE_CAPACITY:
        return L"reached the model's sequence capacity";
    case WINML_CHAT_COMPLETION_FINISH_REASON_CANCELED:
        return L"canceled";
    default:
        return L"error";
    }
}

// Send the conversation, print the reply as it streams, and return the reply
// so the caller can add it to the history.
HRESULT CompleteTurn(IWinMLChatCompletionSession* chat, IWinMLTextGenerationOptions* options,
                     IWinMLCancellationSource* cancellation,
                     const std::vector<WINML_CONVERSATION_MESSAGE>& history,
                     std::wstring& reply) noexcept
try
{
    WINML_CONVERSATION_REQUEST request = {};
    request.formatterMode = WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT;
    request.messageCount = static_cast<UINT32>(history.size());
    request.messages = history.data();
    request.addGenerationPrompt = TRUE;

    ComPtr<IWinMLChatCompletionPullStream> stream;
    RETURN_IF_FAILED(chat->Generate(&request, options, cancellation, stream.GetAddressOf()));
    StreamCloser streamCloser(stream.Get());

    // Content events carry the reply as it is generated. This model and prompt
    // produce no reasoning or tool-call events.
    for (;;)
    {
        WINML_CHAT_COMPLETION_READ_STATUS status{};
        ComPtr<IWinMLConversationOutputEvent> event;
        RETURN_IF_FAILED(stream->ReadNext(&status, event.GetAddressOf()));
        if (status == WINML_CHAT_COMPLETION_READ_STATUS_COMPLETED)
        {
            break;
        }

        WINML_CONVERSATION_OUTPUT_EVENT_TYPE type{};
        RETURN_IF_FAILED(event->GetType(&type));
        if (type == WINML_CONVERSATION_OUTPUT_EVENT_TYPE_CONTENT_DELTA)
        {
            LPCWSTR text = nullptr;
            RETURN_IF_FAILED(event->GetText(&text));
            std::wprintf(L"%ls", text != nullptr ? text : L"");
            std::fflush(stdout);
        }
        else if (type == WINML_CONVERSATION_OUTPUT_EVENT_TYPE_ERROR)
        {
            HRESULT error = E_FAIL;
            LPCWSTR message = nullptr;
            RETURN_IF_FAILED(event->GetError(&error, &message));
            std::fwprintf(stderr, L"\nGeneration failed: %ls\n",
                          message != nullptr ? message : L"");
            return FAILED(error) ? error : E_FAIL;
        }
    }

    std::wprintf(L"\n");

    // The result stays readable until the stream closes.
    ComPtr<IWinMLChatCompletionResult> result;
    RETURN_IF_FAILED(stream->GetResult(result.GetAddressOf()));
    HRESULT error = E_UNEXPECTED;
    RETURN_IF_FAILED(result->GetErrorCode(&error));
    RETURN_IF_FAILED(error);

    LPCWSTR content = nullptr;
    UINT32 promptTokenCount = 0;
    UINT32 completionTokenCount = 0;
    WINML_CHAT_COMPLETION_FINISH_REASON finishReason{};
    WINML_CHAT_COMPLETION_PROMPT_CACHE cacheUse{};
    RETURN_IF_FAILED(result->GetContent(&content));
    RETURN_IF_FAILED(result->GetTokenCounts(&promptTokenCount, &completionTokenCount));
    RETURN_IF_FAILED(result->GetFinishReason(&finishReason));
    RETURN_IF_FAILED(result->GetPromptCacheUse(&cacheUse));
    reply = content != nullptr ? content : L"";

    std::wprintf(L"  Tokens: prompt=%u (reused=%u) completion=%u\n", promptTokenCount,
                 cacheUse.cachedPromptTokenCount, completionTokenCount);
    std::wprintf(L"  Finished because: %ls\n", DescribeChatFinishReason(finishReason));
    return S_OK;
}
CATCH_RETURN()

} // namespace

int wmain(int argumentCount, wchar_t** arguments)
try
{
    if (argumentCount < 3)
    {
        std::fwprintf(stderr, L"Usage: task-chat-completion.exe <model> <tokenizer-source|-> "
                              L"[question] [follow-up] [max-new-tokens]\n");
        return 2;
    }

    LPCWSTR tokenizerSource = std::wcscmp(arguments[2], L"-") == 0 ? nullptr : arguments[2];
    const LPCWSTR questions[] = {
        argumentCount >= 4 ? arguments[3] : L"Name three primary colors.",
        argumentCount >= 5 ? arguments[4] : L"Which of those is the color of the sky?",
    };
    UINT32 maxNewTokens = 48;
    if (argumentCount >= 6 && !ParsePositiveTokenCount(arguments[5], maxNewTokens))
    {
        std::fwprintf(stderr, L"max-new-tokens must be a positive 32-bit integer.\n");
        return 2;
    }

    // The chat Task runs on a Text Generation Task, built here on the CPU with
    // greedy decoding so the replies are the same from run to run.
    ComPtr<IWinMLRuntime> runtime;
    THROW_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(runtime.GetAddressOf())));
    ComPtr<IWinMLExecutionTarget> target;
    THROW_IF_FAILED(runtime->CreateCpuExecutionTarget(target.GetAddressOf()));
    TextGenerationObjects objects;
    THROW_IF_FAILED(BuildTextGeneration(runtime.Get(), target.Get(), arguments[1], tokenizerSource,
                                        maxNewTokens, SamplingArguments{}, objects));

    // The chat session wraps the text-generation session, and the tokens that
    // session holds carry over from one turn to the next.
    ComPtr<IWinMLChatCompletionTask> chatTask;
    THROW_IF_FAILED(winml::tasks::CreateChatCompletionTask(
        objects.task.tasks.Get(), objects.task.tokenizer.Get(), chatTask.GetAddressOf()));
    ComPtr<IWinMLChatCompletionSession> chat;
    THROW_IF_FAILED(chatTask->CreateSession(objects.task.session.Get(), chat.GetAddressOf()));

    ComPtr<IWinMLCancellationSource> cancellation;
    THROW_IF_FAILED(objects.task.tasks->CreateCancellationSource(cancellation.GetAddressOf()));

    // Messages point at their strings, so the replies are kept for as long as
    // the history is sent.
    std::wstring replies[std::size(questions)];
    std::vector<WINML_CONVERSATION_MESSAGE> history;
    for (size_t turn = 0; turn < std::size(questions); ++turn)
    {
        history.push_back({L"user", questions[turn]});
        std::wprintf(L"User: %ls\nAssistant: ", questions[turn]);
        THROW_IF_FAILED(CompleteTurn(chat.Get(), objects.task.options.Get(), cancellation.Get(),
                                     history, replies[turn]));
        history.push_back({L"assistant", replies[turn].c_str()});
    }

    THROW_IF_FAILED(chat->Close());
    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fprintf(stderr, "Chat completion failed: 0x%08X\n", result);
    return 1;
}
