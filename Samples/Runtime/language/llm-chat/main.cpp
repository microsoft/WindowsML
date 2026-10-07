// Copyright (C) Microsoft Corporation. All rights reserved.
//
// LLM Chat: compare unified language loading with an explicit split pipeline.
//
// Chat with a local language model in the console, or answer one -Prompt.
// -Mode unified loads one decoder; -Mode split chains three ONNX models.
//
//   Unified mode loads one "decoder" stage with LoadLanguageModel, as in
//   hello-language-model, and runs each prompt in one Run. Split mode loads
//   emb.onnx, decoder.onnx, and head.onnx, finds tensors by name with
//   IWinMLOrtModelSchema, puts the embedding and head on a CPU target and
//   the decoder on the -Device/-Ep target, declares the decoder's KV pairs
//   with AddStateTensorPair, and chains the stages with Connect. Each token,
//   prompt tokens included, is one Run: input_ids goes to the embedding
//   stage, position_id and attention_mask to the decoder, and the Runtime
//   advances the decoder state.
//   Each reply resets state and re-encodes the whole history with the chat
//   template (-Raw sends only the new text), so no KV cache carries across
//   turns. Both modes pick tokens by argmax.
//
// Run it
//   .\run_llm_chat.ps1 [-Backend ort|llama] [-Mode unified|split]
//                       [-Device cpu|gpu|npu] [-Ep <name>] [-Prompt <text>]
//                       [-MaxTokens <n>] [-ContextCapacity <tokens>] [-Raw]
//                       [-NoStream] [-Diagnostics]
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../../docs/Runtime/tutorials/07-gguf-language-models.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 3, 4, 9, and 11)
//   ../../../../docs/api-reference/IWinMLOrtModelSchema.md

#include <cstdio>
#include <functional>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include <Windows.h>

#include <WinMLRuntime.h>
#include <WinMLTokenizer.h>

#include "common.h"           // DeviceTypeName, WideToUtf8
#include "language_args.h"    // ChatOptions: shared language CLI parsing
#include "language_console.h" // Shared stream, stats, and console input
#include "language_model_loader.h"
#include "winml_llm_pipeline.h" // BuildLlmPipeline
#include "winml_llm_session.h"  // LlmSession

namespace
{
using winmlsamples::language_args::ChatMode;
using winmlsamples::language_args::ChatOptions;

HRESULT GenerateLanguageModel(winmlsamples::language::LanguageModel& model,
                              std::vector<std::pair<std::wstring, std::wstring>>& history,
                              const std::wstring& prompt, const ChatOptions& options)
{
    winmlsamples::language::GenerationResult result;
    std::function<bool(const wchar_t*)> callback;
    if (options.streamFragments)
    {
        callback = winmlsamples::language::StreamFragment;
    }

    if (options.raw)
    {
        RETURN_IF_FAILED(model.Reset());
        result = model.GenerateTokens(prompt.c_str(), options.maxNewTokens, callback);
    }
    else
    {
        history.push_back({L"user", prompt});
        std::vector<WINML_CONVERSATION_MESSAGE> messages;
        messages.reserve(history.size());
        for (const auto& turn : history)
        {
            messages.push_back({turn.first.c_str(), turn.second.c_str()});
        }

        result = model.GenerateConversation(messages.data(), static_cast<UINT32>(messages.size()),
                                            options.maxNewTokens, callback);
        const bool keepPartialAssistant =
            result.hr == S_FALSE &&
            result.stopReason == winmlsamples::language::StopReason::Capacity &&
            result.stats.generatedTokenCount != 0;
        if (result.hr == S_OK || keepPartialAssistant)
        {
            history.push_back({L"assistant", result.text});
        }
        else
        {
            history.pop_back();
        }
    }

    if (!options.streamFragments && !result.text.empty())
    {
        wprintf(L"%s", result.text.c_str());
    }

    winmlsamples::language::PrintGenerationStats(result);
    return result.hr;
}

using GenerateTurn = std::function<HRESULT(const std::wstring&)>;
using ResetConversation = std::function<HRESULT()>;

int RunConversation(const ChatOptions& options, const GenerateTurn& generate,
                    const ResetConversation& reset)
{
    if (!options.prompt.empty())
    {
        wprintf(L"> %s\nAssistant: ", options.prompt.c_str());
        const HRESULT hr = generate(options.prompt);
        wprintf(L"\n");
        if (hr == S_FALSE)
        {
            wprintf(L"ERROR: generation exhausted the fixed context capacity.\n");
        }
        else if (FAILED(hr))
        {
            wprintf(L"ERROR: generation failed (0x%08X).\n", hr);
        }

        return hr == S_OK ? 0 : 1;
    }

    wprintf(L"Type a message. Commands: \"new\" clears the conversation, \"quit\" exits.\n\n");
    while (true)
    {
        wprintf(L"> ");
        fflush(stdout);

        std::wstring line;
        if (!winmlsamples::language::ReadConsoleLine(line))
        {
            break;
        }

        if (line.empty())
        {
            continue;
        }

        if (line == L"quit" || line == L"exit")
        {
            break;
        }

        if (line == L"new" || line == L"reset")
        {
            const HRESULT resetHr = reset();
            if (FAILED(resetHr))
            {
                wprintf(L"ERROR: failed to reset session (0x%08X).\n\n", resetHr);
                return 1;
            }

            wprintf(L"(context cleared)\n\n");
            continue;
        }

        wprintf(L"Assistant: ");
        const HRESULT hr = generate(line);
        wprintf(L"\n\n");
        if (hr == S_FALSE)
        {
            wprintf(L"(context window full -- type \"new\" to start over)\n\n");
            continue;
        }

        if (FAILED(hr))
        {
            wprintf(L"ERROR: generation failed (0x%08X). Resetting.\n\n", hr);
            const HRESULT resetHr = reset();
            if (FAILED(resetHr))
            {
                wprintf(L"ERROR: failed to reset session (0x%08X).\n\n", resetHr);
                return 1;
            }
        }
    }

    wprintf(L"Goodbye!\n");
    return 0;
}
} // namespace

int wmain(int argc, wchar_t* argv[])
{
    wprintf(L"=== Windows ML Runtime: LLM chat ===\n\n");

    ChatOptions opt;
    const int parseExitCode = winmlsamples::language_args::ParseChatOptions(argc, argv, opt);
    if (parseExitCode != 0)
    {
        return parseExitCode;
    }

    const auto artifactKind = winmlsamples::language::GetLanguageArtifactKind(opt.modelPath);
    if (artifactKind == winmlsamples::language::LanguageArtifactKind::Gguf && !opt.ep.empty())
    {
        wprintf(
            L"ERROR: --ep selects an ONNX Runtime execution provider and cannot be used with a GGUF model.\n");
        return 2;
    }

    // An explicit --ep prepares one installed execution provider. The decoder
    // target created below pins that provider and hardware class.
    if (artifactKind != winmlsamples::language::LanguageArtifactKind::Gguf &&
        (opt.device == WINML_EXECUTION_TARGET_KIND_NPU || !opt.ep.empty()))
    {
        const std::string epUtf8 = WideToUtf8(opt.ep);
        if (FAILED(PrepareAndValidateExecutionProviders(opt.device,
                                                        opt.ep.empty() ? nullptr : epUtf8.c_str())))
        {
            return 1;
        }
    }

    if (opt.mode != ChatMode::Split)
    {
        wprintf(L"Loading %s decoder (%s, target %s)...\n",
                winmlsamples::language::LanguageArtifactName(artifactKind),
                L"unified decoder stage", DeviceTypeName(opt.device));
        // Unified mode delegates construction to shared/language_model_loader.h,
        // which performs one LoadModelFromFile and a one-stage pipeline build.
        std::unique_ptr<winmlsamples::language::LanguageModel> model;
        const HRESULT loadHr = winmlsamples::language::LoadLanguageModel(
            opt.modelPath.c_str(),
            opt.tokenizerSource.empty() ? nullptr : opt.tokenizerSource.c_str(), opt.deviceArgs,
            opt.contextCapacity, model);
        if (FAILED(loadHr))
        {
            wprintf(L"ERROR: failed to load %s (0x%08X).\n", opt.modelPath.c_str(), loadHr);
            return 1;
        }

        WINML_EXECUTION_TARGET_KIND resolvedKind{};
        const HRESULT resolvedKindHr =
            winmlsamples::language::GetResolvedTargetKind(*model, resolvedKind);
        if (FAILED(resolvedKindHr))
        {
            wprintf(L"ERROR: failed to inspect the resolved target (0x%08X).\n", resolvedKindHr);
            return 1;
        }

        wprintf(L"Resolved target: %s\n", DeviceTypeName(resolvedKind));

        std::vector<std::pair<std::wstring, std::wstring>> history;
        return RunConversation(
            opt,
            [&](const std::wstring& prompt) {
                return GenerateLanguageModel(*model, history, prompt, opt);
            },
            [&]() {
                const HRESULT hr = model->Reset();
                if (SUCCEEDED(hr))
                {
                    history.clear();
                }

                return hr;
            });
    }

    ComPtr<IWinMLRuntime> runtime;
    if (FAILED(WinMLCreateRuntime(__uuidof(IWinMLRuntime),
                                  reinterpret_cast<void**>(runtime.GetAddressOf()))))
    {
        wprintf(L"ERROR: failed to create the Windows ML Runtime.\n");
        return 1;
    }

    wprintf(L"Loading split ONNX pipeline through ONNX Runtime (decoder on %s)...\n",
            DeviceTypeName(opt.device));
    // BuildLlmPipeline (winml_llm_pipeline.h) loads emb/decoder/head, connects
    // the stages, declares decoder state pairs, requests logits, and builds.
    LlmPipeline pipeline;
    if (FAILED(BuildLlmPipeline(runtime.Get(), opt.modelDir, opt.deviceArgs, pipeline)))
    {
        wprintf(L"ERROR: failed to build the pipeline.\n");
        return 1;
    }

    wprintf(L"Ready: %u-token context%s.\n\n", pipeline.contextLength,
            opt.raw ? L", raw mode" : L"");

    const std::wstring tokenizerPath = winml_llm_detail::JoinPath(opt.modelDir, L"tokenizer.json");
    ComPtr<IWinMLTokenizer> tokenizer;
    const HRESULT tokHr =
        WinMLCreateTokenizerFromFile(tokenizerPath.c_str(), tokenizer.GetAddressOf());
    if (FAILED(tokHr))
    {
        wprintf(L"ERROR: failed to create the tokenizer from %s (0x%08X)\n", tokenizerPath.c_str(),
                tokHr);
        return 1;
    }

    // LlmSession (winml_llm_session.h) owns chat history plus the per-token
    // bind/run/logits/decode loop over the split Runtime pipeline.
    LlmSession session(pipeline, tokenizer.Get(), opt.maxNewTokens, opt.raw);
    const HRESULT sessionHr = session.Initialize();
    if (FAILED(sessionHr))
    {
        wprintf(L"ERROR: failed to initialize language generation (0x%08X).\n", sessionHr);
        return 1;
    }

    return RunConversation(
        opt,
        [&](const std::wstring& prompt) {
            return session.Generate(prompt, [](const wchar_t* fragment) {
                return winmlsamples::language::StreamFragment(fragment);
            });
        },
        [&]() {
            return session.Reset();
        });
}
