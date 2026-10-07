// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared console presentation for the Runtime language samples.
//
// hello-language-model, llm-chat, and speech-to-language-model use these helpers
// only for streaming fragments, stop-reason names, generation statistics, and
// console input; Runtime model loading and pipeline composition stay elsewhere.

#pragma once

#include <cstdio>
#include <iostream>

#include "language_model_loader.h"
#include "sample_args.h"

namespace winmlsamples::language
{

// Token callbacks pass decoder-owned fragments here; the helper prints without
// taking ownership of the pointer.
inline bool StreamFragment(const wchar_t* fragment)
{
    if (fragment != nullptr)
    {
        std::wcout << fragment << std::flush;
    }

    return true;
}

inline const wchar_t* StopReasonName(StopReason reason)
{
    switch (reason)
    {
    case StopReason::EndOfSequence:
        return L"end of sequence";
    case StopReason::MaxTokens:
        return L"maximum tokens";
    case StopReason::Canceled:
        return L"cancelled";
    case StopReason::Capacity:
        return L"capacity";
    case StopReason::Error:
        return L"error";
    default:
        return L"unknown";
    }
}

// Prints client-side timing and stop information collected by the generation
// loop; it does not query Runtime diagnostics.
inline void PrintGenerationStats(const GenerationResult& result)
{
    const double decodeTokensPerSecond =
        result.stats.generatedTokenCount > 1 && result.stats.decodeDurationInMilliseconds > 0.0
            ? (static_cast<double>(result.stats.generatedTokenCount - 1) * 1000.0) /
                  result.stats.decodeDurationInMilliseconds
            : 0.0;
    if (decodeTokensPerSecond > 0.0)
    {
        fwprintf(
            stderr,
            L"[stop: %s, prompt: %u, generated: %u, TTFT: %.1f ms, total: %.2f tok/s, decode: %.2f tok/s]\n",
            StopReasonName(result.stopReason), result.stats.promptTokenCount,
            result.stats.generatedTokenCount, result.stats.timeToFirstTokenInMilliseconds,
            result.stats.tokensPerSecond, decodeTokensPerSecond);
    }
    else
    {
        fwprintf(stderr,
                 L"[stop: %s, prompt: %u, generated: %u, TTFT: %.1f ms, total: %.2f tok/s]\n",
                 StopReasonName(result.stopReason), result.stats.promptTokenCount,
                 result.stats.generatedTokenCount, result.stats.timeToFirstTokenInMilliseconds,
                 result.stats.tokensPerSecond);
    }
}

inline bool ReadConsoleLine(std::wstring& line)
{
    return winml_args::ReadConsoleLineWide(line);
}

} // namespace winmlsamples::language
