// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Hello LanguageModel: stream one greedy response from a unified decoder.
//
// Load an ONNX/ORT or GGUF language model, send it one prompt, and print the
// reply as it is generated.
//
//   LoadLanguageModel (shared/language_model_loader.h) loads the artifact,
//   which selects ONNX Runtime or llama.cpp, as one "decoder" stage. For
//   ONNX it declares the past/present pairs with AddStateTensorPair, so the
//   Runtime carries the KV cache between runs; a GGUF stage keeps its own.
//   Generation resets execution state, applies the chat template (or plain
//   encoding with -Raw), and runs the whole prompt once. Each step then reads
//   the last logits row on a CPU target, takes the argmax, prints the
//   decoded fragment, and runs that token, until end of sequence, 256 new
//   tokens, or the sequence capacity.
//
// Run it
//   .\run_hello_language_model.ps1 [-Backend ort|llama] [-ModelPath <path>]
//                                   [-TokenizerSource <path>] [-Device cpu|gpu|npu]
//                                   [-Ep <name>] [-Prompt <text>] [-Raw]
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../../docs/Runtime/tutorials/07-gguf-language-models.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 3 and 9)
//   ../../../../docs/api-reference/IWinMLStatefulStage.md
//   ../../../../docs/api-reference/IWinMLTokenizer.md

#include <cstdio>
#include <cwchar>
#include <fcntl.h>
#include <iostream>
#include <io.h>
#include <memory>

#include <WinMLRuntime.h>
#include <WinMLTokenizer.h>

#include "common.h"
#include "language_args.h"
#include "language_console.h"
#include "language_model_loader.h"

int wmain(int argc, wchar_t** argv)
{
    _setmode(_fileno(stdout), _O_U8TEXT);

    winmlsamples::language_args::HelloOptions options;
    if (!winmlsamples::language_args::ParseHelloOptions(argc, argv, options))
    {
        winmlsamples::language_args::PrintHelloUsage();
        return (argc > 1 && (wcscmp(argv[1], L"--help") == 0 || wcscmp(argv[1], L"-h") == 0)) ? 0
                                                                                              : 2;
    }

    if (options.modelPath.empty())
    {
        options.modelPath = FindModelPath(L"llm\\model.onnx");
    }

    if (options.modelPath.empty() || !FileExists(options.modelPath))
    {
        fwprintf(stderr, L"Could not find the language model. Run check_artifacts.ps1 for "
                         L"the selected language artifact or pass --model.\n");
        return 1;
    }

    const auto artifactKind = winmlsamples::language::GetLanguageArtifactKind(options.modelPath);
    if (artifactKind == winmlsamples::language::LanguageArtifactKind::Unknown)
    {
        fwprintf(stderr, L"Unsupported language artifact: %s\n", options.modelPath.c_str());
        return 2;
    }

    if (artifactKind == winmlsamples::language::LanguageArtifactKind::Gguf &&
        !options.device.epName.empty())
    {
        fwprintf(stderr, L"--ep selects an ORT provider and cannot be used with GGUF.\n");
        return 2;
    }

    if (artifactKind == winmlsamples::language::LanguageArtifactKind::Onnx)
    {
        if (FAILED(winmlsamples::language_args::PrepareOnnxProviderIfNeeded(artifactKind,
                                                                            options.device)))
        {
            return 1;
        }
    }

    std::wcout << L"Artifact: " << winmlsamples::language::LanguageArtifactName(artifactKind)
               << L"\nLoading " << options.modelPath << L"...\n"
               << std::flush;

    // LoadLanguageModel (shared/language_model_loader.h) performs the Runtime
    // setup: LoadModelFromFile -> CreatePipelineBuilder -> AddModelStage ->
    // RequestOutput -> state options -> Build -> tokenizer/decoder creation.
    std::unique_ptr<winmlsamples::language::LanguageModel> model;
    const HRESULT loadResult = winmlsamples::language::LoadLanguageModel(
        options.modelPath.c_str(),
        options.tokenizerSource.empty() ? nullptr : options.tokenizerSource.c_str(), options.device,
        options.contextCapacity, model);
    if (FAILED(loadResult))
    {
        fwprintf(stderr,
                 L"LoadModel failed with HRESULT 0x%08X. Verify the artifact, tokenizer, "
                 L"target, and backend files.\n",
                 static_cast<unsigned int>(loadResult));
        return 1;
    }

    WINML_EXECUTION_TARGET_KIND resolvedKind{};
    const HRESULT resolvedKindHr =
        winmlsamples::language::GetResolvedTargetKind(*model, resolvedKind);
    if (FAILED(resolvedKindHr))
    {
        fwprintf(stderr, L"Failed to inspect the resolved target (0x%08X).\n",
                 static_cast<unsigned int>(resolvedKindHr));
        return 1;
    }

    std::wcout << L"Resolved target: " << DeviceTypeName(resolvedKind) << L"\n";

    // The helper owns the manual loop from CommonPatterns.md: encode prompt,
    // bind token tensors, Run, read logits, argmax, and DecodeToken fragments.
    winmlsamples::language::GenerationResult result;
    if (options.raw)
    {
        result = model->GenerateTokens(options.prompt.c_str(), 0,
                                       winmlsamples::language::StreamFragment);
    }
    else
    {
        const WINML_CONVERSATION_MESSAGE message = {L"user", options.prompt.c_str()};
        result =
            model->GenerateConversation(&message, 1, 0, winmlsamples::language::StreamFragment);
    }

    if (result.hr == S_FALSE && result.stats.generatedTokenCount != 0)
    {
        std::wcout << L"\n[stopped at the model sequence capacity]\n";
        return 0;
    }

    if (result.hr == S_FALSE)
    {
        fwprintf(stderr, L"The prompt does not fit the model sequence capacity.\n");
        return 1;
    }

    if (FAILED(result.hr))
    {
        fwprintf(stderr, L"%s failed with HRESULT 0x%08X.\n",
                 options.raw ? L"GenerateTokens" : L"GenerateConversation",
                 static_cast<unsigned int>(result.hr));
        if (!options.raw)
        {
            fwprintf(stderr,
                     L"Use --raw to distinguish model execution from chat-template formatting.\n");
        }

        return 1;
    }

    std::wcout << L"\n";
    return 0;
}
