// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared command-line helpers for Runtime language samples.
//
// Common parsing for the model, tokenizer, device, prompt, and context options
// used by hello-language-model, llm-chat, and speech-to-language-model, plus
// backend/provider validation, so each sample's main() stays focused on its
// Runtime flow.

#pragma once

#include <cerrno>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

#include <wil/result.h>

#include "common.h"
#include "ep_catalog_utils.h"
#include "language_model_loader.h"
#include "sample_args.h"

namespace winmlsamples::language_args
{
constexpr UINT32 kDefaultMaxNewTokens = 256;
constexpr UINT64 kMaximumContextCapacity = 1'048'576;

inline std::wstring JoinPath(const std::wstring& dir, const wchar_t* file)
{
    std::wstring path = dir;
    if (!path.empty() && path.back() != L'\\' && path.back() != L'/')
    {
        path += L'\\';
    }

    return path + file;
}

inline bool TryParseContextCapacity(const wchar_t* value, UINT64& contextCapacity)
{
    if (value == nullptr || *value == L'\0' || *value == L'-')
    {
        return false;
    }

    errno = 0;
    wchar_t* end = nullptr;
    const unsigned long long parsed = wcstoull(value, &end, 10);
    if (errno == ERANGE || end == value || *end != L'\0' || parsed > kMaximumContextCapacity)
    {
        return false;
    }

    contextCapacity = static_cast<UINT64>(parsed);
    return true;
}

inline HRESULT PrepareOnnxProviderIfNeeded(
    winmlsamples::language::LanguageArtifactKind artifactKind, const DeviceArgs& device) noexcept
{
    if (artifactKind != winmlsamples::language::LanguageArtifactKind::Onnx ||
        (device.deviceType != WINML_EXECUTION_TARGET_KIND_NPU && device.epName.empty()))
    {
        return S_OK;
    }

    return PrepareAndValidateExecutionProviders(
        device.deviceType, device.epName.empty() ? nullptr : device.epName.c_str());
}

struct HelloOptions
{
    std::wstring modelPath;
    std::wstring tokenizerSource;
    std::wstring prompt = L"Reply with one short greeting.";
    UINT64 contextCapacity = 0;
    DeviceArgs device;
    bool raw = false;
};

inline bool ParseHelloOptions(int argc, wchar_t** argv, HelloOptions& options)
{
    options.device = ParseDeviceArgs(argc, argv);
    std::vector<std::wstring> positional;
    for (int index = 1; index < argc; ++index)
    {
        const std::wstring argument = argv[index];
        const auto requireValue = [&](const wchar_t* name) -> const wchar_t* {
            if (index + 1 >= argc)
            {
                fwprintf(stderr, L"%s requires a value.\n", name);
                return nullptr;
            }

            return argv[++index];
        };

        if (argument == L"--help" || argument == L"-h")
        {
            return false;
        }

        if (argument == L"--model")
        {
            const wchar_t* value = requireValue(L"--model");
            if (!value)
                return false;
            options.modelPath = value;
        }
        else if (argument == L"--tokenizer")
        {
            const wchar_t* value = requireValue(L"--tokenizer");
            if (!value)
                return false;
            options.tokenizerSource = value;
        }
        else if (argument == L"--prompt")
        {
            const wchar_t* value = requireValue(L"--prompt");
            if (!value)
                return false;
            options.prompt = value;
        }
        else if (argument == L"--context")
        {
            const wchar_t* value = requireValue(L"--context");
            if (!value)
                return false;
            if (!TryParseContextCapacity(value, options.contextCapacity))
            {
                fwprintf(stderr, L"--context must be a decimal integer from 0 through %llu.\n",
                         static_cast<unsigned long long>(kMaximumContextCapacity));
                return false;
            }
        }
        else if (argument == L"--raw")
        {
            options.raw = true;
        }
        else if (argument == L"--device" || argument == L"--ep")
        {
            const wchar_t* value = requireValue(argument.c_str());
            if (!value)
                return false;
            if (*value == L'-')
            {
                fwprintf(stderr, L"%s requires a value; got '%s'.\n", argument.c_str(), value);
                return false;
            }
        }
        else if (argument == L"--performance" || argument == L"--efficiency")
        {
        }
        else if (!argument.empty() && argument[0] != L'-')
        {
            positional.push_back(argument);
        }
        else
        {
            fwprintf(stderr, L"Unknown argument: %s\n", argument.c_str());
            return false;
        }
    }

    if (options.modelPath.empty() && !positional.empty())
    {
        options.modelPath = positional[0];
    }

    if (positional.size() > 1)
    {
        options.prompt = positional[1];
    }

    return positional.size() <= 2;
}

inline void PrintHelloUsage()
{
    fwprintf(stderr, L"Usage: hello-language-model.exe [model] [\"prompt\"] [options]\n"
                     L"  --model PATH       Unified ONNX/ORT decoder or GGUF decoder.\n"
                     L"  --tokenizer PATH   Optional tokenizer directory/file override.\n"
                     L"  --prompt TEXT      Prompt text.\n"
                     L"  --device cpu|gpu|npu\n"
                     L"  --context TOKENS   Optional sequence-capacity hint.\n"
                     L"  --raw              Skip the model chat template.\n"
                     L"  --ep NAME          ORT execution-provider pin; invalid for GGUF.\n");
}

enum class ChatMode
{
    Unified,
    Split
};

struct ChatOptions
{
    std::wstring modelDir;
    std::wstring modelPath;
    std::wstring tokenizerSource;
    std::wstring prompt;
    std::wstring ep;
    DeviceArgs deviceArgs;
    WINML_EXECUTION_TARGET_KIND device = WINML_EXECUTION_TARGET_KIND_CPU;
    ChatMode mode = ChatMode::Unified;
    UINT32 maxNewTokens = kDefaultMaxNewTokens;
    UINT64 contextCapacity = 0;
    bool raw = false;
    bool streamFragments = true;
};

inline int ParseChatOptions(int argc, wchar_t** argv, ChatOptions& opt)
{
    SampleArgs args(argc, argv);
    opt.deviceArgs = args.Device();
    opt.device = opt.deviceArgs.deviceType;
    opt.ep = Utf8ToWide(args.Ep());
    opt.raw = args.Flag(L"--raw");
    opt.streamFragments = !args.Flag(L"--no-stream");

    const std::wstring mode = args.Text(L"--mode", -1, L"Mode (unified/split)", L"unified");
    if (mode == L"split")
    {
        opt.mode = ChatMode::Split;
    }
    else if (mode != L"unified")
    {
        wprintf(L"ERROR: --mode must be unified or split.\n");
        return 2;
    }

    const int requestedMaxTokens =
        args.Int(L"--max-tokens", L"Maximum generated tokens", kDefaultMaxNewTokens);
    if (requestedMaxTokens <= 0)
    {
        wprintf(L"ERROR: --max-tokens must be positive.\n");
        return 2;
    }

    opt.maxNewTokens = static_cast<UINT32>(requestedMaxTokens);
    const int requestedContext =
        args.Int(L"--context", L"Context capacity (0 = model-declared or Runtime default)", 0);
    if (!args.Valid())
    {
        return 2;
    }

    if (requestedContext < 0)
    {
        wprintf(L"ERROR: --context must not be negative.\n");
        return 2;
    }

    opt.contextCapacity = static_cast<UINT64>(requestedContext);
    opt.prompt = args.Interactive() ? std::wstring() : args.Text(L"--prompt", -1, L"Prompt", L"");
    opt.modelPath = args.Text(L"--model", -1, L"Unified ONNX/ORT or GGUF model", L"");
    opt.tokenizerSource = args.Text(L"--tokenizer", -1, L"Tokenizer override", L"");
    if (opt.modelPath.empty() || opt.mode == ChatMode::Split)
    {
        opt.modelDir = args.ModelDir(L"--model-dir", L"decoder.onnx");
    }

    if (opt.mode == ChatMode::Split && (!opt.modelPath.empty() || !opt.tokenizerSource.empty()))
    {
        wprintf(
            L"ERROR: split mode uses --model-dir; --model and --tokenizer are not supported.\n");
        return 2;
    }

    if (opt.mode == ChatMode::Split && opt.modelDir.empty())
    {
        wprintf(L"ERROR: could not locate the split ORT model directory.\n");
        wprintf(L"Export one first: .\\check_artifacts.ps1 -Sample llm-chat\n");
        return 1;
    }

    if (opt.mode == ChatMode::Split && opt.contextCapacity != 0)
    {
        wprintf(L"ERROR: --context applies to unified models, not split models.\n");
        return 2;
    }

    if (opt.mode == ChatMode::Split && !opt.streamFragments)
    {
        wprintf(L"ERROR: --no-stream applies to unified models, not split models.\n");
        return 2;
    }

    if (opt.mode != ChatMode::Split && opt.modelPath.empty())
    {
        if (opt.modelDir.empty())
        {
            wprintf(L"ERROR: could not locate a unified language model.\n");
            return 1;
        }

        opt.modelPath = JoinPath(opt.modelDir, L"model.onnx");
    }

    return 0;
}

struct SpeechToLanguageOptions
{
    std::wstring speechModelDirectory;
    std::wstring wavPath;
    std::wstring languageModelSource;
    std::wstring instruction;
    DeviceArgs device;
};

inline void PrintSpeechToLanguageUsage()
{
    fwprintf(stderr, L"Usage: speech-to-language-model.exe [whisper-model-dir] [speech.wav] "
                     L"[language-model-or-split-directory] [instruction] [options]\n"
                     L"  --device cpu|gpu|npu   Execution device (default: cpu)\n"
                     L"  --ep NAME              Pin an ORT execution provider\n");
}

inline HRESULT ParseSpeechToLanguageOptions(int argc, wchar_t** argv,
                                            SpeechToLanguageOptions& options,
                                            bool& handledHelp) noexcept
try
{
    handledHelp = false;
    options.device = ParseDeviceArgs(argc, argv);

    std::vector<std::wstring> positional;
    for (int i = 1; i < argc; ++i)
    {
        if (wcscmp(argv[i], L"--help") == 0 || wcscmp(argv[i], L"-h") == 0)
        {
            PrintSpeechToLanguageUsage();
            handledHelp = true;
            return S_OK;
        }

        if ((wcscmp(argv[i], L"--device") == 0 || wcscmp(argv[i], L"--ep") == 0) && i + 1 < argc)
        {
            ++i;
            continue;
        }

        if (wcscmp(argv[i], L"--performance") == 0 || wcscmp(argv[i], L"--efficiency") == 0)
        {
            continue;
        }

        if (wcsncmp(argv[i], L"--", 2) == 0)
        {
            fwprintf(stderr, L"Unknown option: %ls\n", argv[i]);
            return E_INVALIDARG;
        }

        positional.emplace_back(argv[i]);
    }

    RETURN_HR_IF(E_INVALIDARG, positional.size() > 4);

    if (!positional.empty())
    {
        options.speechModelDirectory = positional[0];
    }
    else
    {
        const std::wstring encoder =
            FindModelPath(L"whisper-medium-q4f16\\onnx\\encoder_model.onnx");
        const size_t separator = encoder.find_last_of(L"\\/");
        options.speechModelDirectory =
            separator == std::wstring::npos ? std::wstring() : encoder.substr(0, separator);
    }

    options.wavPath =
        positional.size() > 1 ? positional[1] : GetExecutableDirectory() + L"\\test_audio.wav";
    options.languageModelSource =
        positional.size() > 2 ? positional[2] : FindModelPath(L"llm\\model.onnx");
    options.instruction =
        positional.size() > 3
            ? positional[3]
            : L"What color is the fox in the transcript? Reply with only the color.";
    return S_OK;
}
CATCH_RETURN()

} // namespace winmlsamples::language_args
