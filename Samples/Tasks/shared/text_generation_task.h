// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Shared Text Generation Task helpers for the language and composition samples.
// They wrap Runtime pipeline construction, tokenizer loading, typed Text
// Generation configuration, streaming, and terminal-result validation.

#pragma once

#include "task_runtime.h"

#include <WinMLConversation.h>
#include <WinMLTokenizer.h>
#include <winml/tasks/text_generation/TextGeneration.hpp>
#include <winml/tasks/text_generation/onnx/TextGeneration.hpp>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cwctype>
#include <cwchar>
#include <filesystem>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace winmlsamples::tasks
{

struct TextGenerationObjects
{
    ComPtr<IWinMLModel> model;
    ComPtr<IWinMLModel> draftModel;
    ComPtr<IWinMLPipeline> draftPipeline;
    winml::tasks::text_generation::TextGeneration task;
    UINT64 effectiveSequenceCapacity = 0;
    WINML_SEQUENCE_CAPACITY_DISCLOSURE declaredCapacityDisclosure =
        WINML_SEQUENCE_CAPACITY_DISCLOSURE_UNKNOWN;
    UINT64 declaredSequenceCapacity = 0;
};

// The finish reason explains why generation stopped. Reporting it distinguishes
// a model that ended its own turn from one that ran out of room.
inline LPCWSTR DescribeFinishReason(WINML_TEXT_GENERATION_FINISH_REASON reason) noexcept
{
    switch (reason)
    {
    case WINML_TEXT_GENERATION_FINISH_REASON_EOS_TOKEN:
        return L"end-of-sequence token";
    case WINML_TEXT_GENERATION_FINISH_REASON_STOP_TOKEN:
        return L"stop token";
    case WINML_TEXT_GENERATION_FINISH_REASON_MAX_TOKENS:
        return L"reached max-new-tokens";
    case WINML_TEXT_GENERATION_FINISH_REASON_SEQUENCE_CAPACITY:
        return L"reached the model's sequence capacity";
    case WINML_TEXT_GENERATION_FINISH_REASON_CANCELED:
        return L"canceled";
    default:
        return L"error";
    }
}

// Sampling controls the Task API accepts. Greedy selection is the default, and
// a small model asked for a long continuation tends to repeat itself under it,
// so these let the sample demonstrate the alternative.
struct SamplingArguments
{
    bool hasTemperature = false;
    float temperature = 0.0f;
    bool hasTopK = false;
    UINT32 topK = 0;
    bool hasTopP = false;
    float topP = 0.0f;
    bool hasRepetitionPenalty = false;
    float repetitionPenalty = 0.0f;
    bool hasSeed = false;
    UINT64 seed = 0;
};

inline bool HasNegativeSign(LPCWSTR value) noexcept
{
    for (LPCWSTR cursor = value; *cursor != L'\0'; ++cursor)
    {
        if (std::iswspace(*cursor))
        {
            continue;
        }

        return *cursor == L'-';
    }

    return false;
}

inline bool ParsePositiveTokenCount(LPCWSTR value, UINT32& tokenCount) noexcept
{
    if (HasNegativeSign(value))
    {
        return false;
    }

    errno = 0;
    wchar_t* end = nullptr;
    const unsigned long long parsed = std::wcstoull(value, &end, 10);
    if (errno == ERANGE || end == value || *end != L'\0' || parsed == 0 || parsed > UINT32_MAX)
    {
        return false;
    }

    tokenCount = static_cast<UINT32>(parsed);
    return true;
}

inline bool ParseTokenCount(LPCWSTR value, UINT32& tokenCount) noexcept
{
    errno = 0;
    wchar_t* end = nullptr;
    const unsigned long parsed = std::wcstoul(value, &end, 10);
    if (errno == ERANGE || end == value || *end != L'\0' || parsed == 0 || parsed > UINT32_MAX)
    {
        return false;
    }

    tokenCount = static_cast<UINT32>(parsed);
    return true;
}

inline bool ParseFloatOption(LPCWSTR value, float minimum, float maximum, float& parsed) noexcept
{
    errno = 0;
    wchar_t* end = nullptr;
    const double converted = std::wcstod(value, &end);
    if (errno == ERANGE || end == value || *end != L'\0' || !std::isfinite(converted) ||
        converted < minimum || converted > maximum)
    {
        return false;
    }

    parsed = static_cast<float>(converted);
    return true;
}

inline bool ParseUInt32Option(LPCWSTR value, UINT32 maximum, UINT32& parsed) noexcept
{
    if (HasNegativeSign(value))
    {
        return false;
    }

    errno = 0;
    wchar_t* end = nullptr;
    const unsigned long long converted = std::wcstoull(value, &end, 10);
    if (errno == ERANGE || end == value || *end != L'\0' || converted > maximum)
    {
        return false;
    }

    parsed = static_cast<UINT32>(converted);
    return true;
}

inline bool ParseUInt64Option(LPCWSTR value, UINT64& parsed) noexcept
{
    if (HasNegativeSign(value))
    {
        return false;
    }

    errno = 0;
    wchar_t* end = nullptr;
    const unsigned long long converted = std::wcstoull(value, &end, 10);
    if (errno == ERANGE || end == value || *end != L'\0')
    {
        return false;
    }

    parsed = static_cast<UINT64>(converted);
    return true;
}

inline std::wstring GetLowercaseExtension(LPCWSTR path)
{
    std::wstring extension = std::filesystem::path(path).extension().wstring();
    std::transform(extension.begin(), extension.end(), extension.begin(), [](wchar_t value) {
        return static_cast<wchar_t>(std::towlower(value));
    });
    return extension;
}

// Convert command-line sampling choices into the immutable option values that
// IWinMLTextGenerationTask uses when starting generation.
inline WINML_TEXT_GENERATION_OPTIONS MakeSampledOptions(UINT32 maxNewTokens,
                                                        const SamplingArguments& sampling) noexcept
{
    WINML_TEXT_GENERATION_OPTIONS values = winml::tasks::MakeTextGenerationOptions(maxNewTokens);
    if (sampling.hasTemperature)
    {
        values.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELD_TEMPERATURE;
        values.temperature = sampling.temperature;
    }

    if (sampling.hasTopK)
    {
        values.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELD_TOP_K;
        values.topK = sampling.topK;
    }

    if (sampling.hasTopP)
    {
        values.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELD_TOP_P;
        values.topP = sampling.topP;
    }

    if (sampling.hasRepetitionPenalty)
    {
        values.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELD_REPETITION_PENALTY;
        values.repetitionPenalty = sampling.repetitionPenalty;
    }

    if (sampling.hasSeed)
    {
        values.presentFields |= WINML_TEXT_GENERATION_OPTION_FIELD_SEED;
        values.seed = sampling.seed;
    }

    return values;
}

// Explains a finish reason that commonly surprises people. A small instruct
// model continues raw text indefinitely; it ends its own turn only when the
// prompt uses the chat format it was trained on.
inline void PrintFinishReasonGuidance(WINML_TEXT_GENERATION_FINISH_REASON reason,
                                      UINT32 requestedMaxTokens, UINT32 generatedTokenCount,
                                      bool chat) noexcept
{
    if (reason == WINML_TEXT_GENERATION_FINISH_REASON_SEQUENCE_CAPACITY)
    {
        std::wprintf(L"  The prepared model is exported with a fixed sequence capacity, "
                     L"so prompt plus generated tokens cannot exceed it. %u of the "
                     L"requested %u tokens were produced. Re-export with a larger "
                     L"capacity to go further.\n",
                     generatedTokenCount, requestedMaxTokens);
    }
    else if (reason == WINML_TEXT_GENERATION_FINISH_REASON_MAX_TOKENS && !chat)
    {
        std::wprintf(L"  The model was still generating when the token limit was "
                     L"reached. Instruct models end their own turn only when the prompt "
                     L"uses their chat format; run with -Chat to apply it.\n");
    }
}

// Speculative decoding proposes several tokens per step and verifies them in
// one multi-position pass. The method changes throughput, not the sampled
// distribution, so it combines with every sampling setting.
struct SpeculativeArguments
{
    WINML_TEXT_GENERATION_SPECULATIVE_METHOD method = WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE;

    // Most draft tokens per step; zero uses kDefaultDraftTokenCount.
    UINT32 draftTokenCount = 0;

    // Smaller model that DRAFT_MODEL runs beside the target. It must share the
    // target's tokenizer.
    std::wstring draftModelPath;

    // Companion block draft model (DFlash, DFlash2, or DSpark) that MODEL runs
    // in place of built-in prediction layers. Empty uses the built-in layers.
    std::wstring draftPredictorPath;
};

// Draft tokens proposed per step when the command line does not choose.
inline constexpr UINT32 kDefaultDraftTokenCount = 3;

inline LPCWSTR DescribeSpeculativeMethod(WINML_TEXT_GENERATION_SPECULATIVE_METHOD method) noexcept
{
    switch (method)
    {
    case WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL:
        return L"model draft predictor";
    case WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL:
        return L"draft model";
    case WINML_TEXT_GENERATION_SPECULATIVE_METHOD_PROMPT_LOOKUP:
        return L"prompt lookup";
    default:
        return L"none";
    }
}

// Explains HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED) from a speculative pipeline
// Build or Task session creation, then returns the result unchanged.
inline HRESULT ReportSpeculativeSupport(HRESULT result,
                                        WINML_TEXT_GENERATION_SPECULATIVE_METHOD method) noexcept
{
    if (result == HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED))
    {
        std::fwprintf(stderr,
                      L"Speculative decoding (%ls) is not supported by this model or placement.\n",
                      DescribeSpeculativeMethod(method));
    }

    return result;
}

struct TextGenerationResult
{
    std::wstring text;
    UINT32 promptTokenCount = 0;
    UINT32 generatedTokenCount = 0;
    WINML_TEXT_GENERATION_FINISH_REASON finishReason = WINML_TEXT_GENERATION_FINISH_REASON_ERROR;
    double timeToFirstTokenMilliseconds = 0.0;
    double totalMilliseconds = 0.0;
    std::vector<UINT32> generatedTokenIds;
    WINML_TEXT_GENERATION_SPECULATIVE_STATISTICS speculative{};
};

// Read the terminal Task result. `check(hr, label)` reports a failing step; each
// caller decides which steps it reports.
template <typename Check>
inline HRESULT ReadCompletedTextGenerationResult(IWinMLTextGenerationPullStream* stream,
                                                 TextGenerationResult& generation,
                                                 Check&& check) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, stream);

    ComPtr<IWinMLTextGenerationResult> result;
    RETURN_IF_FAILED(check(stream->GetResult(result.GetAddressOf()), L"get result"));

    LPCWSTR text = nullptr;
    HRESULT error = E_UNEXPECTED;
    UINT32 tokenCount = 0;
    UINT32* tokenIds = nullptr;
    RETURN_IF_FAILED(check(result->GetText(&text), L"get text"));
    RETURN_HR_IF_NULL(E_UNEXPECTED, text);
    RETURN_IF_FAILED(check(result->GetTokenIds(&tokenCount, &tokenIds), L"get token ids"));
    RETURN_HR_IF(E_UNEXPECTED, tokenCount != 0 && tokenIds == nullptr);
    std::vector<UINT32> resultTokens;
    if (tokenCount != 0)
    {
        resultTokens.assign(tokenIds, tokenIds + tokenCount);
    }

    CoTaskMemFree(tokenIds);
    RETURN_IF_FAILED(
        check(result->GetFinishReason(&generation.finishReason), L"get finish reason"));
    RETURN_IF_FAILED(check(result->GetErrorCode(&error), L"get error code"));
    RETURN_IF_FAILED(check(error, L"generation error code"));
    RETURN_IF_FAILED(check(result->GetPromptTokenCount(&generation.promptTokenCount),
                           L"get prompt token count"));
    ComPtr<IWinMLTextGenerationSpeculativeResult> speculativeResult;
    if (SUCCEEDED(result.As(&speculativeResult)))
    {
        RETURN_IF_FAILED(check(speculativeResult->GetSpeculativeStatistics(&generation.speculative),
                               L"get speculative statistics"));
    }

    generation.text = text;
    generation.generatedTokenCount = tokenCount;
    generation.generatedTokenIds = std::move(resultTokens);
    return S_OK;
}
CATCH_RETURN()

// Read the sequence capacity exposed by the stateful Runtime stage that backs
// the Text Generation Task session.
inline HRESULT ReadTextGenerationCapacity(TextGenerationObjects& objects) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, objects.task.stateOwnerStage.Get());
    ComPtr<IWinMLStatefulStage> statefulStage;
    RETURN_IF_FAILED(objects.task.stateOwnerStage.As(&statefulStage));
    RETURN_IF_FAILED(statefulStage->GetSequenceCapacity(&objects.effectiveSequenceCapacity));
    return statefulStage->GetDeclaredSequenceCapacity(&objects.declaredCapacityDisclosure,
                                                      &objects.declaredSequenceCapacity);
}

// Loads a GGUF model into a one-stage pipeline that can verify draft tokens.
// Stage options are fixed at Build: the draft token limit reserves room to roll
// back rejected tokens, and the model method also loads the model's draft
// predictor, either its built-in layers or a companion block draft model.
inline HRESULT BuildSpeculativeGgufPipeline(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                            LPCWSTR modelPath,
                                            const SpeculativeArguments& speculative,
                                            UINT32 draftTokenCount, ComPtr<IWinMLModel>& model,
                                            ComPtr<IWinMLPipeline>& pipeline,
                                            ComPtr<IWinMLStage>& stage) noexcept
try
{
    RETURN_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));
    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    RETURN_IF_FAILED(
        builder->AddModelStage(model.Get(), target, L"task-text-generation", stage.GetAddressOf()));

    ComPtr<IWinMLStatefulStageOptions> statefulOptions;
    RETURN_IF_FAILED(stage.As(&statefulOptions));
    RETURN_IF_FAILED(statefulOptions->SetDraftTokenLimitHint(draftTokenCount));
    if (speculative.method == WINML_TEXT_GENERATION_SPECULATIVE_METHOD_MODEL)
    {
        RETURN_IF_FAILED(statefulOptions->SetModelDraftPredictorEnabled(TRUE));
        if (!speculative.draftPredictorPath.empty())
        {
            RETURN_IF_FAILED(
                statefulOptions->SetDraftPredictorPath(speculative.draftPredictorPath.c_str()));
        }
    }

    return builder->Build(pipeline.GetAddressOf());
}
CATCH_RETURN()

// GGUF models expose a logical token input, logits output, tokenizer, and state
// owner directly. The Task remains independent of the llama.cpp implementation.
inline HRESULT BuildGgufTextGeneration(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                       LPCWSTR modelPath, UINT32 maxNewTokens,
                                       const SamplingArguments& sampling,
                                       const SpeculativeArguments& speculative,
                                       TextGenerationObjects& objects) noexcept
try
{
    objects = {};
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, modelPath);
    RETURN_HR_IF(E_INVALIDARG, maxNewTokens == 0);

    const bool speculate = speculative.method != WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE;
    UINT32 draftTokenCount = 0;
    ComPtr<IWinMLPipeline> pipeline;
    ComPtr<IWinMLStage> stage;
    if (speculate)
    {
        // The sample resolves the default count here because the stage reserves
        // rollback room for it at Build, before the Task is configured.
        draftTokenCount = speculative.draftTokenCount != 0 ? speculative.draftTokenCount
                                                           : kDefaultDraftTokenCount;
        RETURN_IF_FAILED(ReportSpeculativeSupport(
            BuildSpeculativeGgufPipeline(runtime, target, modelPath, speculative, draftTokenCount,
                                         objects.model, pipeline, stage),
            speculative.method));
    }
    else
    {
        RETURN_IF_FAILED(BuildSingleStagePipeline(runtime, target, modelPath,
                                                  L"task-text-generation", {}, objects.model,
                                                  pipeline, stage));
    }

    winml::tasks::text_generation::SpeculativeDecoding speculativeDecoding;
    speculativeDecoding.method = speculative.method;
    speculativeDecoding.draftTokenCount = draftTokenCount;
    ComPtr<IWinMLStage> draftStage;
    if (speculative.method == WINML_TEXT_GENERATION_SPECULATIVE_METHOD_DRAFT_MODEL)
    {
        // The draft model must share the target's tokenizer. It runs on the
        // same target so its proposals do not cross devices.
        RETURN_HR_IF(E_INVALIDARG, speculative.draftModelPath.empty());
        RETURN_IF_FAILED(BuildSingleStagePipeline(
            runtime, target, speculative.draftModelPath.c_str(), L"task-text-generation-draft", {},
            objects.draftModel, objects.draftPipeline, draftStage));
        speculativeDecoding.draftPipeline = objects.draftPipeline.Get();
        speculativeDecoding.draftTokenInput = {draftStage.Get(), 0};
        speculativeDecoding.draftOutput = {draftStage.Get(), 0};
        speculativeDecoding.draftStateOwnerStage = draftStage.Get();
    }

    ComPtr<IWinMLTokenizer> tokenizer;
    RETURN_IF_FAILED(WinMLCreateTokenizerFromFile(modelPath, tokenizer.GetAddressOf()));

    winml::tasks::text_generation::Options options;
    options.values = MakeSampledOptions(maxNewTokens, sampling);
    winml::tasks::text_generation::TextGenerationArguments arguments;
    arguments.runtime = runtime;
    arguments.pipeline = pipeline.Get();
    arguments.tokenInput = {stage.Get(), 0};
    arguments.output = {
        stage.Get(),
        0,
        WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS,
    };

    arguments.stateOwnerStage = stage.Get();
    arguments.tokenTarget = target;
    arguments.tokenizer = tokenizer.Get();
    arguments.eosPolicy = WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT;
    arguments.options = &options;
    arguments.speculative = speculate ? &speculativeDecoding : nullptr;
    const HRESULT taskResult =
        winml::tasks::text_generation::BuildTextGeneration(arguments, objects.task);
    RETURN_IF_FAILED(speculate ? ReportSpeculativeSupport(taskResult, speculative.method)
                               : taskResult);
    return ReadTextGenerationCapacity(objects);
}
CATCH_RETURN()

// Compiled artifacts preserve logical ordinals but do not expose ONNX names.
// Declare state and output behavior before Build, then use the same generic
// Text Generation Task constructor as the source-model path.
inline HRESULT BuildOrdinalTextGeneration(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                          IWinMLModel* model, LPCWSTR tokenizerSource,
                                          UINT32 maxNewTokens, const SamplingArguments& sampling,
                                          UINT64 sequenceCapacity,
                                          TextGenerationObjects& objects) noexcept
try
{
    objects = {};
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, model);
    RETURN_HR_IF_NULL(E_POINTER, tokenizerSource);
    RETURN_HR_IF(E_INVALIDARG, maxNewTokens == 0 || sequenceCapacity == 0);
    objects.model = model;

    ComPtr<IWinMLPipelineBuilder> builder;
    RETURN_IF_FAILED(runtime->CreatePipelineBuilder(builder.GetAddressOf()));
    ComPtr<IWinMLStage> stage;
    RETURN_IF_FAILED(builder->AddModelStage(model, target, L"task-compiled-text-generation",
                                            stage.GetAddressOf()));
    ComPtr<IWinMLStatefulStageOptions> statefulOptions;
    RETURN_IF_FAILED(stage.As(&statefulOptions));
    RETURN_IF_FAILED(statefulOptions->AddStateTensorPair(1, 1));
    RETURN_IF_FAILED(statefulOptions->SetSequenceCapacityHint(sequenceCapacity));
    RETURN_IF_FAILED(stage->RequestOutput(0));
    ComPtr<IWinMLPipeline> pipeline;
    RETURN_IF_FAILED(builder->Build(pipeline.GetAddressOf()));

    ComPtr<IWinMLTokenizer> tokenizer;
    RETURN_IF_FAILED(WinMLCreateTokenizerFromFile(tokenizerSource, tokenizer.GetAddressOf()));
    winml::tasks::text_generation::Options options;
    options.values = MakeSampledOptions(maxNewTokens, sampling);
    winml::tasks::text_generation::TextGenerationArguments arguments;
    arguments.runtime = runtime;
    arguments.pipeline = pipeline.Get();
    arguments.tokenInput = {stage.Get(), 0};
    arguments.output = {
        stage.Get(),
        0,
        WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS,
    };

    arguments.stateOwnerStage = stage.Get();
    arguments.tokenTarget = target;
    arguments.tokenizer = tokenizer.Get();
    arguments.eosPolicy = WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT;
    arguments.options = &options;
    RETURN_IF_FAILED(winml::tasks::text_generation::BuildTextGeneration(arguments, objects.task));
    return ReadTextGenerationCapacity(objects);
}
CATCH_RETURN()

// Capacity used only when a model does not declare one. The prepared Qwen export
// is built with this many positions.
inline constexpr UINT64 kDefaultSequenceCapacity = 128;

// The exported model declares its own capacity in the packed state it binds, so
// the sample reads it instead of assuming a value. A model that does not publish
// a usable capacity falls back to the documented default.
inline UINT64 ReadDeclaredSequenceCapacity(IWinMLModel* model, UINT64 fallbackCapacity) noexcept
{
    if (model == nullptr)
    {
        return fallbackCapacity;
    }

    ComPtr<IWinMLModelSchema> schema;
    if (FAILED(model->QueryInterface(IID_PPV_ARGS(schema.GetAddressOf()))))
    {
        return fallbackCapacity;
    }

    WINML_TENSOR_SCHEMA_DESC stateDesc = {};
    if (FAILED(schema->GetInputTensorDesc(1, &stateDesc)) || stateDesc.dimensionCount != 4)
    {
        return fallbackCapacity;
    }

    const UINT64 declared = stateDesc.dimensions[2];
    if (declared == 0 || declared == UINT64_MAX)
    {
        return fallbackCapacity;
    }

    return declared;
}

// The ONNX convenience builder resolves the token and logits endpoints by name.
// State pairs are still supplied by the caller because cache ownership is part
// of the model format, not something the Task can safely infer.
//
// Capacity is read from the model this function loads, so the caller never has
// to load the same graph twice to inspect it.
inline HRESULT BuildOnnxTextGeneration(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                       LPCWSTR modelPath, LPCWSTR tokenizerSource,
                                       UINT32 maxNewTokens, const SamplingArguments& sampling,
                                       UINT64 fallbackCapacity,
                                       TextGenerationObjects& objects) noexcept
try
{
    objects = {};
    RETURN_HR_IF_NULL(E_POINTER, runtime);
    RETURN_HR_IF_NULL(E_POINTER, target);
    RETURN_HR_IF_NULL(E_POINTER, modelPath);
    RETURN_HR_IF_NULL(E_POINTER, tokenizerSource);
    RETURN_HR_IF(E_INVALIDARG, maxNewTokens == 0 || fallbackCapacity == 0);

    const auto checkStep = [](HRESULT result, LPCWSTR step) noexcept {
        if (FAILED(result))
        {
            std::fwprintf(stderr, L"ONNX text generation failed at %ls: 0x%08X\n", step,
                          static_cast<unsigned int>(result));
        }

        return result;
    };

    RETURN_IF_FAILED(
        checkStep(runtime->LoadModelFromFile(modelPath, nullptr, objects.model.GetAddressOf()),
                  L"load model"));
    const UINT64 sequenceCapacity =
        ReadDeclaredSequenceCapacity(objects.model.Get(), fallbackCapacity);
    ComPtr<IWinMLTokenizer> tokenizer;
    RETURN_IF_FAILED(
        checkStep(WinMLCreateTokenizerFromFile(tokenizerSource, tokenizer.GetAddressOf()),
                  L"load tokenizer"));

    winml::tasks::text_generation::Options options;
    options.values = MakeSampledOptions(maxNewTokens, sampling);
    const winml::tasks::text_generation::onnx::StateTensorPair stateTensorPairs[] = {
        {1, 1},
    };

    winml::tasks::text_generation::onnx::BuildTextGenerationArguments arguments;
    arguments.runtime = runtime;
    arguments.model = objects.model.Get();
    arguments.target = target;
    arguments.tokenizer = tokenizer.Get();
    arguments.tokenInputName = L"input_ids";
    arguments.outputName = L"logits";
    arguments.debugName = L"task-onnx-text-generation";
    arguments.stateTensorPairs = stateTensorPairs;
    arguments.sequenceCapacityHint = sequenceCapacity;
    arguments.outputKind = WINML_TEXT_GENERATION_OUTPUT_KIND_LOGITS;
    arguments.eosPolicy = WINML_TEXT_GENERATION_EOS_POLICY_TOKENIZER_DEFAULT;
    arguments.options = &options;
    RETURN_IF_FAILED(
        checkStep(winml::tasks::text_generation::onnx::BuildTextGeneration(arguments, objects.task),
                  L"build Task"));
    return checkStep(ReadTextGenerationCapacity(objects), L"read capacity");
}
CATCH_RETURN()

// Select the artifact path, build the Runtime pipeline, configure the typed
// Text Generation Task, and create its default session.
inline HRESULT BuildTextGeneration(IWinMLRuntime* runtime, IWinMLExecutionTarget* target,
                                   LPCWSTR modelPath, LPCWSTR tokenizerSource, UINT32 maxNewTokens,
                                   const SamplingArguments& sampling,
                                   TextGenerationObjects& objects,
                                   const SpeculativeArguments& speculative = {}) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, modelPath);
    std::wstring extension = std::filesystem::path(modelPath).extension().wstring();
    std::transform(extension.begin(), extension.end(), extension.begin(), [](wchar_t value) {
        return static_cast<wchar_t>(std::towlower(value));
    });

    if (extension == L".gguf")
    {
        return BuildGgufTextGeneration(runtime, target, modelPath, maxNewTokens, sampling,
                                       speculative, objects);
    }

    // The sample demonstrates speculative decoding with GGUF models only.
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                 speculative.method != WINML_TEXT_GENERATION_SPECULATIVE_METHOD_NONE);
    RETURN_HR_IF_NULL(E_POINTER, tokenizerSource);
    if (extension == L".ort")
    {
        ComPtr<IWinMLModel> model;
        RETURN_IF_FAILED(runtime->LoadModelFromFile(modelPath, nullptr, model.GetAddressOf()));
        return BuildOrdinalTextGeneration(
            runtime, target, model.Get(), tokenizerSource, maxNewTokens, sampling,
            ReadDeclaredSequenceCapacity(model.Get(), kDefaultSequenceCapacity), objects);
    }

    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED), extension != L".onnx");
    return BuildOnnxTextGeneration(runtime, target, modelPath, tokenizerSource, maxNewTokens,
                                   sampling, kDefaultSequenceCapacity, objects);
}
CATCH_RETURN()

// Drain a Text Generation pull stream, then read the terminal result before the
// stream is closed.
inline HRESULT ReadTextGenerationStream(IWinMLTextGenerationPullStream* stream,
                                        std::chrono::steady_clock::time_point started,
                                        bool printFragments,
                                        TextGenerationResult& generation) noexcept
try
{
    generation = {};
    RETURN_HR_IF_NULL(E_POINTER, stream);

    const auto checkStep = [](HRESULT result, LPCWSTR step) noexcept {
        if (FAILED(result))
        {
            std::fwprintf(stderr, L"Text generation failed at %ls: 0x%08X\n", step,
                          static_cast<unsigned int>(result));
        }

        return result;
    };

    std::wstring streamedText;
    bool producedFirstToken = false;
    for (;;)
    {
        WINML_TEXT_GENERATION_READ_STATUS status{};
        UINT32 tokenId = 0;
        LPCWSTR fragment = nullptr;
        RETURN_IF_FAILED(checkStep(stream->ReadNext(&status, &tokenId, &fragment), L"read stream"));
        UNREFERENCED_PARAMETER(tokenId);

        if (status == WINML_TEXT_GENERATION_READ_STATUS_UPDATE)
        {
            RETURN_HR_IF_NULL(E_UNEXPECTED, fragment);
            if (!producedFirstToken)
            {
                generation.timeToFirstTokenMilliseconds =
                    std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() -
                                                              started)
                        .count();
                producedFirstToken = true;
            }

            streamedText += fragment;
            if (printFragments)
            {
                std::wprintf(L"%ls", fragment);
                std::fflush(stdout);
            }

            continue;
        }

        RETURN_HR_IF(E_UNEXPECTED, status != WINML_TEXT_GENERATION_READ_STATUS_COMPLETED);
        break;
    }

    // Only GetResult failures are reported here; other result failures return
    // to the caller, which reports the overall failure.
    const auto reportGetResult = [&](HRESULT hr, LPCWSTR label) {
        return wcscmp(label, L"get result") == 0 ? checkStep(hr, label) : hr;
    };
    RETURN_IF_FAILED(ReadCompletedTextGenerationResult(stream, generation, reportGetResult));
    generation.totalMilliseconds =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started)
            .count();
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), generation.text != streamedText);
    return S_OK;
}
CATCH_RETURN()

// A pull stream exposes partial text immediately and a final result with token
// counts and finish status. The sample verifies both views agree.
inline HRESULT GenerateText(IWinMLTextGenerationSession* session,
                            IWinMLTextGenerationOptions* options, LPCWSTR prompt,
                            IWinMLCancellationSource* cancellation, bool printFragments,
                            TextGenerationResult& generation) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, session);
    RETURN_HR_IF_NULL(E_POINTER, options);
    RETURN_HR_IF_NULL(E_POINTER, prompt);

    ComPtr<IWinMLTextGenerationPullStream> stream;
    const auto started = std::chrono::steady_clock::now();
    RETURN_IF_FAILED(session->GenerateText(prompt, options, cancellation, stream.GetAddressOf()));
    StreamCloser streamCloser(stream.Get());
    return ReadTextGenerationStream(stream.Get(), started, printFragments, generation);
}
CATCH_RETURN()

// An instruct model ends its reply with an end-of-sequence token when the prompt
// uses the chat format it was trained on. The structured conversation formatter
// renders the model's own chat template for one user turn and returns the
// prompt token IDs.
inline HRESULT EncodeChatPrompt(IWinMLTokenizer* tokenizer, LPCWSTR userMessage,
                                std::vector<UINT32>& promptTokens) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, tokenizer);
    RETURN_HR_IF_NULL(E_POINTER, userMessage);

    ComPtr<IWinMLStructuredConversationFormatter> formatter;
    RETURN_IF_FAILED(tokenizer->QueryInterface(IID_PPV_ARGS(formatter.GetAddressOf())));

    const WINML_CONVERSATION_MESSAGE message = {L"user", userMessage};
    WINML_CONVERSATION_REQUEST request = {};
    request.formatterMode = WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT;
    request.messageCount = 1;
    request.messages = &message;
    request.addGenerationPrompt = TRUE;

    ComPtr<IWinMLConversationFormatResult> formatted;
    RETURN_IF_FAILED(formatter->FormatConversation(&request, formatted.GetAddressOf()));

    UINT32 tokenCount = 0;
    UINT32* tokenIds = nullptr;
    RETURN_IF_FAILED(formatted->GetTokenIds(&tokenCount, &tokenIds));
    std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> ownedTokenIds(tokenIds, CoTaskMemFree);
    promptTokens.assign(tokenIds, tokenIds + tokenCount);
    return S_OK;
}
CATCH_RETURN()

// Encode a prompt as plain text, or as a chat turn when chat is set.
inline HRESULT EncodePrompt(IWinMLTokenizer* tokenizer, LPCWSTR prompt, bool chat,
                            std::vector<UINT32>& promptTokens) noexcept
try
{
    if (chat)
    {
        return EncodeChatPrompt(tokenizer, prompt, promptTokens);
    }

    RETURN_HR_IF_NULL(E_POINTER, tokenizer);
    RETURN_HR_IF_NULL(E_POINTER, prompt);

    UINT32 tokenCount = 0;
    UINT32* tokenIds = nullptr;
    RETURN_IF_FAILED(
        tokenizer->Encode(prompt, WINML_TOKENIZER_ENCODE_FLAG_NONE, &tokenCount, &tokenIds));
    std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> ownedTokenIds(tokenIds, CoTaskMemFree);
    promptTokens.assign(tokenIds, tokenIds + tokenCount);
    return S_OK;
}
CATCH_RETURN()

// GenerateTokens starts from caller-supplied prompt token IDs, such as a
// formatted chat turn. Streaming and the final result work as they do for
// GenerateText.
inline HRESULT GenerateTokens(IWinMLTextGenerationSession* session,
                              IWinMLTextGenerationOptions* options,
                              const std::vector<UINT32>& promptTokens,
                              IWinMLCancellationSource* cancellation, bool printFragments,
                              TextGenerationResult& generation) noexcept
try
{
    RETURN_HR_IF_NULL(E_POINTER, session);
    RETURN_HR_IF_NULL(E_POINTER, options);
    RETURN_HR_IF(E_INVALIDARG, promptTokens.empty());

    ComPtr<IWinMLTextGenerationPullStream> stream;
    const auto started = std::chrono::steady_clock::now();
    RETURN_IF_FAILED(session->GenerateTokens(static_cast<UINT32>(promptTokens.size()),
                                             promptTokens.data(), options, cancellation,
                                             stream.GetAddressOf()));
    StreamCloser streamCloser(stream.Get());
    return ReadTextGenerationStream(stream.Get(), started, printFragments, generation);
}
CATCH_RETURN()

} // namespace winmlsamples::tasks
