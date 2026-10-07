// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Unified language-model Runtime helper used by hello-language-model,
// llm-chat unified mode, and speech-to-language-model.
//
// This file wraps the backend-neutral API surface for one decoder artifact:
// LoadModelFromFile, target creation, pipeline builder/stage/output setup,
// IWinMLStatefulStageOptions for Runtime-managed KV state, tokenizer creation,
// conversation formatting, and the caller-driven manual decode loop.
//
// Learn more (paths relative to this file)
//   ../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../docs/Runtime/tutorials/07-gguf-language-models.md
//   ../../../docs/api-reference/CommonPatterns.md (patterns 3 and 9)
//   ../../../docs/api-reference/IWinMLStatefulStageOptions.md
//   ../../../docs/api-reference/IWinMLTokenizer.md
//   ../../../docs/api-reference/IWinMLStructuredConversationFormatter.md

#pragma once

#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cwctype>
#include <filesystem>
#include <functional>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

#include <wil/com.h>
#include <wil/result.h>

#include <WinMLConversation.h>
#include <WinMLRuntime.h>
#include <WinMLTokenizer.h>

#include "execution_target_utils.h"
#include "llm_inference.h"

namespace winmlsamples::language
{

// Two logical state tensors pair up only when their declared descriptors match.
inline bool StateTensorDescMatches(const WINML_TENSOR_SCHEMA_DESC& first,
                                   const WINML_TENSOR_SCHEMA_DESC& second) noexcept
{
    if (first.dataType != second.dataType || first.dimensionCount != second.dimensionCount)
    {
        return false;
    }

    for (UINT32 axis = 0; axis < first.dimensionCount; ++axis)
    {
        if (first.dimensions[axis] != second.dimensions[axis])
        {
            return false;
        }
    }

    return true;
}

// Declares trailing KV-cache tensor pairs through IWinMLStatefulStageOptions
// before Build so the Runtime, not the caller, owns sequence state.
// A stateful decoder must declare its key/value slots before Build. The Runtime
// deliberately never infers persistent state from tensor names, matching
// descriptors, or ordinal order, so an undeclared cache tensor stays ordinary
// model I/O that nobody binds and Run fails with E_INVALIDARG.
//
// Read the ordinals from IWinMLModelSchema on the model rather than
// IWinMLStageSchema on the stage: the stage schema is only materialized after
// Build, and these pairs have to be declared before it.
//
// A unified export lays the cache out as trailing tensors: the token input and
// the logits output come first, then each `past_*` input lines up with the
// `present_*` output at the same offset from the end. Walk back from the end
// while the declared descriptors agree and declare exactly that many pairs,
// which leaves any leading non-state input alone instead of guessing at it.
//
// A fixed-size cache also declares the sequence capacity: the sequence axis of a
// rank-4 `[batch, heads, sequence, head_dim]` state tensor. The loader passes it
// as the capacity hint so generation stops at the cache boundary. Zero means the
// cache did not declare a fixed capacity.
inline HRESULT DeclareDecoderStateTensorPairs(IWinMLModel* model,
                                              IWinMLStatefulStageOptions* options,
                                              UINT32* declaredPairCount,
                                              UINT64* declaredSequenceCapacity) noexcept
{
    *declaredPairCount = 0;
    *declaredSequenceCapacity = 0;

    wil::com_ptr<IWinMLModelSchema> schema;
    if (FAILED(model->QueryInterface(IID_PPV_ARGS(schema.put()))))
    {
        return S_OK;
    }

    UINT32 inputCount = 0;
    UINT32 outputCount = 0;
    if (FAILED(schema->GetInputCount(&inputCount)) ||
        FAILED(schema->GetOutputCount(&outputCount)) || inputCount < 2 || outputCount < 2)
    {
        return S_OK;
    }

    const UINT32 candidateCount = std::min(inputCount - 1, outputCount - 1);
    UINT32 matched = 0;
    while (matched < candidateCount)
    {
        WINML_TENSOR_SCHEMA_DESC stateInput{};
        WINML_TENSOR_SCHEMA_DESC stateOutput{};
        if (FAILED(schema->GetInputTensorDesc(inputCount - 1 - matched, &stateInput)) ||
            FAILED(schema->GetOutputTensorDesc(outputCount - 1 - matched, &stateOutput)) ||
            !StateTensorDescMatches(stateInput, stateOutput))
        {
            break;
        }

        ++matched;
    }

    for (UINT32 pair = 0; pair < matched; ++pair)
    {
        RETURN_IF_FAILED(
            options->AddStateTensorPair(inputCount - matched + pair, outputCount - matched + pair));
    }

    *declaredPairCount = matched;

    WINML_TENSOR_SCHEMA_DESC firstState{};
    if (matched != 0 && SUCCEEDED(schema->GetInputTensorDesc(inputCount - matched, &firstState)) &&
        firstState.dimensionCount == 4 && firstState.dimensions[2] > 0 &&
        firstState.dimensions[2] != UINT64_MAX)
    {
        *declaredSequenceCapacity = firstState.dimensions[2];
    }

    return S_OK;
}

enum class LanguageArtifactKind
{
    Unknown,
    Onnx,
    Gguf,
};

enum class StopReason
{
    EndOfSequence,
    MaxTokens,
    Canceled,
    Capacity,
    Error,
};

struct GenerationStats
{
    UINT32 promptTokenCount = 0;
    UINT32 generatedTokenCount = 0;
    double timeToFirstTokenInMilliseconds = 0.0;
    double decodeDurationInMilliseconds = 0.0;
    double totalDurationInMilliseconds = 0.0;
    double tokensPerSecond = 0.0;
};

struct GenerationResult
{
    HRESULT hr = S_OK;
    StopReason stopReason = StopReason::MaxTokens;
    // The generated token ids, excluding the end-of-sequence token that stops
    // the loop, so tokens.size() matches stats.generatedTokenCount and text.
    std::vector<UINT32> tokens;
    std::wstring text;
    GenerationStats stats;
};

inline bool EqualLanguageExtension(std::wstring_view extension, std::wstring_view expected) noexcept
{
    if (extension.size() != expected.size())
    {
        return false;
    }

    for (size_t index = 0; index < extension.size(); ++index)
    {
        if (towlower(extension[index]) != expected[index])
        {
            return false;
        }
    }

    return true;
}

inline LanguageArtifactKind GetLanguageArtifactKind(const std::wstring& path) noexcept
{
    const size_t separator = path.find_last_of(L"\\/");
    const size_t dot = path.find_last_of(L'.');
    if (dot == std::wstring::npos || (separator != std::wstring::npos && dot < separator))
    {
        return LanguageArtifactKind::Unknown;
    }

    const std::wstring_view extension(path.data() + dot, path.size() - dot);
    if (EqualLanguageExtension(extension, L".onnx") || EqualLanguageExtension(extension, L".ort"))
    {
        return LanguageArtifactKind::Onnx;
    }

    if (EqualLanguageExtension(extension, L".gguf"))
    {
        return LanguageArtifactKind::Gguf;
    }

    return LanguageArtifactKind::Unknown;
}

inline const wchar_t* LanguageArtifactName(LanguageArtifactKind kind)
{
    switch (kind)
    {
    case LanguageArtifactKind::Onnx:
        return L"ONNX / ORT";
    case LanguageArtifactKind::Gguf:
        return L"GGUF / llama.cpp";
    default:
        return L"unknown";
    }
}

inline HRESULT ValidateLanguageArtifactRequest(_In_z_ PCWSTR modelPath, const DeviceArgs& device,
                                               _Out_ LanguageArtifactKind& artifactKind) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, modelPath);

    artifactKind = GetLanguageArtifactKind(modelPath);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                 artifactKind == LanguageArtifactKind::Unknown);
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                 artifactKind == LanguageArtifactKind::Gguf && !device.epName.empty());
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                 artifactKind == LanguageArtifactKind::Gguf &&
                     device.deviceType == WINML_EXECUTION_TARGET_KIND_NPU);
    return S_OK;
}

inline std::filesystem::path ResolveLanguageTokenizerSource(_In_z_ PCWSTR modelPath,
                                                            _In_opt_z_ PCWSTR tokenizerSource,
                                                            LanguageArtifactKind artifactKind)
{
    if (tokenizerSource != nullptr && *tokenizerSource != L'\0')
    {
        return tokenizerSource;
    }

    if (artifactKind == LanguageArtifactKind::Onnx)
    {
        std::filesystem::path directory = std::filesystem::path(modelPath).parent_path();
        return directory.empty() ? std::filesystem::current_path() : directory;
    }

    return modelPath;
}

// Reads the fixed logits vocabulary from the materialized stage, mirroring the
// split sample's TryReadVocabFromHeadStage. A zero result means the backend left
// the logits dimension free, or its vocabulary covers every UINT32 token id, so
// callers skip token-range checks rather than guess.
inline HRESULT GetStageVocabularySize(_In_ IWinMLStageSchema* schema,
                                      _Out_ UINT32* vocabularySize) noexcept
{
    RETURN_HR_IF_NULL(E_POINTER, schema);
    RETURN_HR_IF_NULL(E_POINTER, vocabularySize);
    *vocabularySize = 0;

    WINML_TENSOR_DESC logitsDescription{};
    RETURN_IF_FAILED(schema->GetOutputTensorDesc(0, &logitsDescription));
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA),
                 logitsDescription.dimensionCount == 0 || logitsDescription.dimensions == nullptr);

    const UINT64 vocabularyExtent =
        logitsDescription.dimensions[logitsDescription.dimensionCount - 1];
    RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_INVALID_DATA), vocabularyExtent == 0);
    if (vocabularyExtent == UINT64_MAX || vocabularyExtent > UINT32_MAX)
    {
        return S_OK;
    }

    *vocabularySize = static_cast<UINT32>(vocabularyExtent);
    return S_OK;
}

class LanguageModel
{
public:
    using FragmentCallback = std::function<bool(const wchar_t*)>;

    static HRESULT Load(_In_z_ PCWSTR modelPath, _In_opt_z_ PCWSTR tokenizerSource,
                        const DeviceArgs& device, UINT64 contextCapacityHint,
                        _Out_ std::unique_ptr<LanguageModel>& languageModel) noexcept
    try
    {
        languageModel.reset();

        LanguageArtifactKind artifactKind = LanguageArtifactKind::Unknown;
        RETURN_IF_FAILED(ValidateLanguageArtifactRequest(modelPath, device, artifactKind));

        auto created = std::make_unique<LanguageModel>();
        RETURN_IF_FAILED(WinMLCreateRuntime(IID_PPV_ARGS(created->m_runtime.put())));
        RETURN_IF_FAILED(
            CreateExecutionTarget(created->m_runtime.get(), device, created->m_stageTarget.put()));
        // Unified load: one call selects the backend from the artifact, then
        // the rest of the setup uses the same pipeline objects.
        RETURN_IF_FAILED(
            created->m_runtime->LoadModelFromFile(modelPath, nullptr, created->m_model.put()));

        wil::com_ptr<IWinMLPipelineBuilder> builder;
        RETURN_IF_FAILED(created->m_runtime->CreatePipelineBuilder(builder.put()));
        RETURN_IF_FAILED(builder->AddModelStage(created->m_model.get(),
                                                created->m_stageTarget.get(), L"decoder",
                                                created->m_stage.put()));
        RETURN_IF_FAILED(created->m_stage->RequestOutput(0));

        // Declare any ONNX KV-cache pairs before Build; GGUF-backed stages own
        // their state and report no declared pairs here.
        wil::com_ptr<IWinMLStatefulStageOptions> options;
        RETURN_IF_FAILED(created->m_stage->QueryInterface(IID_PPV_ARGS(options.put())));
        UINT32 declaredStatePairs = 0;
        UINT64 declaredCapacity = 0;
        RETURN_IF_FAILED(DeclareDecoderStateTensorPairs(created->m_model.get(), options.get(),
                                                        &declaredStatePairs, &declaredCapacity));
        const UINT64 capacityHint =
            contextCapacityHint != 0 ? contextCapacityHint : declaredCapacity;
        if (capacityHint != 0)
        {
            RETURN_IF_FAILED(options->SetSequenceCapacityHint(capacityHint));
        }

        RETURN_IF_FAILED(builder->Build(created->m_pipeline.put()));
        RETURN_IF_FAILED(
            created->m_stage->QueryInterface(IID_PPV_ARGS(created->m_statefulStage.put())));

        wil::com_ptr<IWinMLStageSchema> schema;
        RETURN_IF_FAILED(created->m_stage->QueryInterface(IID_PPV_ARGS(schema.put())));
        WINML_TENSOR_DESC tokenDescription{};
        RETURN_IF_FAILED(schema->GetInputTensorDesc(0, &tokenDescription));
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                     tokenDescription.dataType != WINML_TENSOR_DATA_TYPE_INT32 &&
                         tokenDescription.dataType != WINML_TENSOR_DATA_TYPE_INT64);
        RETURN_HR_IF(HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                     tokenDescription.dimensionCount != 1 && tokenDescription.dimensionCount != 2);
        created->m_tokenDataType = tokenDescription.dataType;
        created->m_tokenDimensionCount = tokenDescription.dimensionCount;

        const std::filesystem::path resolvedTokenizerSource =
            ResolveLanguageTokenizerSource(modelPath, tokenizerSource, artifactKind);
        // Tokenizer source is the ONNX model directory or the GGUF artifact
        // unless the caller supplies an override.
        RETURN_IF_FAILED(WinMLCreateTokenizerFromFile(resolvedTokenizerSource.c_str(),
                                                      created->m_tokenizer.put()));
        RETURN_IF_FAILED(created->m_tokenizer->CreateDecoder(created->m_decoder.put()));
        created->m_tokenizer->QueryInterface(IID_PPV_ARGS(created->m_formatter.put()));

        // The fixed logits vocabulary is a fact of the materialized stage, so it
        // is cached once here and used to range-check every tokenizer special
        // token and prompt token before the backend runs.
        RETURN_IF_FAILED(GetStageVocabularySize(schema.get(), &created->m_vocabularySize));
        RETURN_IF_FAILED(created->InitializeTokenizerMetadata());

        RETURN_IF_FAILED(
            created->m_runtime->CreateCpuExecutionTarget(created->m_tokenTarget.put()));
        languageModel = std::move(created);
        return S_OK;
    }
    CATCH_RETURN()

    HRESULT Reset() noexcept
    {
        RETURN_IF_FAILED(m_pipeline->ResetExecutionState());
        RETURN_IF_FAILED(m_decoder->Reset());
        return S_OK;
    }

    GenerationResult GenerateTokens(_In_z_ PCWSTR prompt, UINT32 maxNewTokens,
                                    const FragmentCallback& callback = {}) noexcept
    {
        GenerationResult result;
        result.hr = GenerateEncoded(
            [this, prompt](_Out_ UINT32* count, _Outptr_ UINT32** tokens) {
                return m_tokenizer->Encode(prompt, WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS,
                                           count, tokens);
            },
            maxNewTokens, callback, result);
        if (FAILED(result.hr) && result.stopReason == StopReason::MaxTokens)
        {
            result.stopReason = StopReason::Error;
        }

        return result;
    }

    GenerationResult GenerateConversation(_In_reads_(messageCount)
                                              const WINML_CONVERSATION_MESSAGE* messages,
                                          UINT32 messageCount, UINT32 maxNewTokens,
                                          const FragmentCallback& callback = {}) noexcept
    {
        GenerationResult result;
        if (!m_formatter)
        {
            result.hr = E_NOINTERFACE;
            result.stopReason = StopReason::Error;
            return result;
        }

        result.hr = GenerateEncoded(
            [this, messages, messageCount](_Out_ UINT32* count, _Outptr_ UINT32** tokens) {
                // The formatter renders the model's own chat template and ends
                // the prompt with the assistant generation prompt.
                WINML_CONVERSATION_REQUEST request = {};
                request.formatterMode = WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT;
                request.messageCount = messageCount;
                request.messages = messages;
                request.addGenerationPrompt = TRUE;

                wil::com_ptr<IWinMLConversationFormatResult> formatted;
                RETURN_IF_FAILED(m_formatter->FormatConversation(&request, formatted.put()));
                return formatted->GetTokenIds(count, tokens);
            },
            maxNewTokens, callback, result);
        if (FAILED(result.hr) && result.stopReason == StopReason::MaxTokens)
        {
            result.stopReason = StopReason::Error;
        }

        return result;
    }

    IWinMLStage* Stage() const noexcept
    {
        return m_stage.get();
    }

private:
    using EncodePrompt = std::function<HRESULT(UINT32*, UINT32**)>;

    // Reads the tokenizer metadata this loop depends on. EOS is required: a
    // greedy loop with no end-of-sequence token runs to the token budget on
    // every prompt. BOS stays optional - the chat template inserts it - but it
    // is range-checked when the tokenizer declares one.
    HRESULT InitializeTokenizerMetadata() noexcept
    {
        wil::com_ptr<IWinMLTokenizerMetadata> metadata;
        const HRESULT metadataHr = m_tokenizer->QueryInterface(IID_PPV_ARGS(metadata.put()));
        if (FAILED(metadataHr))
        {
            wprintf(L"ERROR: tokenizer does not expose required EOS metadata.\n");
            return metadataHr;
        }

        UINT32 eosCount = 0;
        UINT32* eosTokens = nullptr;
        const HRESULT eosHr = metadata->GetEosTokenIds(&eosCount, &eosTokens);
        std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> ownedEosTokens(eosTokens, CoTaskMemFree);
        if (FAILED(eosHr))
        {
            wprintf(L"ERROR: failed to read required tokenizer EOS metadata.\n");
            return eosHr;
        }

        // S_FALSE is the ABI's "no end-of-sequence token declared".
        if (eosHr == S_FALSE || eosCount == 0 || eosTokens == nullptr)
        {
            wprintf(L"ERROR: tokenizer EOS metadata is empty.\n");
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }

        RETURN_IF_FAILED(ValidateTokenIds(eosTokens, eosCount, L"tokenizer EOS"));
        m_eosTokens.assign(eosTokens, eosTokens + eosCount);

        UINT32 beginningToken = 0;
        const HRESULT bosHr = metadata->GetBosTokenId(&beginningToken);
        if (bosHr == S_OK)
        {
            RETURN_IF_FAILED(ValidateTokenIds(&beginningToken, 1, L"tokenizer BOS"));
        }

        return S_OK;
    }

    // Rejects token ids the model's vocabulary cannot represent. A model that
    // leaves its logits dimension free publishes no fixed vocabulary, so the
    // range check is skipped explicitly instead of guessed at.
    HRESULT ValidateTokenIds(_In_reads_(tokenCount) const UINT32* tokens, UINT32 tokenCount,
                             _In_z_ PCWSTR label) const noexcept
    {
        RETURN_HR_IF_NULL(E_POINTER, tokens);
        if (m_vocabularySize == 0)
        {
            return S_OK;
        }

        for (UINT32 index = 0; index < tokenCount; ++index)
        {
            if (tokens[index] >= m_vocabularySize)
            {
                wprintf(L"ERROR: %s token id %u is outside the model vocabulary "
                        L"(%u). Tokenizer and model do not match.\n",
                        label, tokens[index], m_vocabularySize);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }
        }

        return S_OK;
    }

    HRESULT CreateTokenTensor(_In_reads_(tokenCount) const UINT32* tokens, UINT32 tokenCount,
                              _COM_Outptr_ IWinMLTensor** tensor) noexcept
    {
        RETURN_HR_IF_NULL(E_POINTER, tokens);
        RETURN_HR_IF_NULL(E_POINTER, tensor);
        *tensor = nullptr;
        RETURN_HR_IF(E_INVALIDARG, tokenCount == 0);

        UINT64 dimensions[] = {tokenCount, tokenCount};
        if (m_tokenDimensionCount == 2)
        {
            dimensions[0] = 1;
        }

        WINML_TENSOR_DESC description{};
        description.dataType = m_tokenDataType;
        description.dimensionCount = m_tokenDimensionCount;
        description.dimensions = dimensions;
        if (m_tokenDataType == WINML_TENSOR_DATA_TYPE_INT32)
        {
            std::vector<INT32> values(tokens, tokens + tokenCount);
            return CreateTensorOnTarget(m_tokenTarget.get(), &description, values.data(),
                                        values.size() * sizeof(values[0]), tensor);
        }

        std::vector<INT64> values(tokens, tokens + tokenCount);
        return CreateTensorOnTarget(m_tokenTarget.get(), &description, values.data(),
                                    values.size() * sizeof(values[0]), tensor);
    }

    HRESULT RunTokens(_In_reads_(tokenCount) const UINT32* tokens, UINT32 tokenCount) noexcept
    {
        wil::com_ptr<IWinMLTensor> tensor;
        RETURN_IF_FAILED(CreateTokenTensor(tokens, tokenCount, tensor.put()));
        RETURN_IF_FAILED(m_stage->BindInput(0, tensor.get()));
        return m_pipeline->Run();
    }

    HRESULT RunToken(UINT32 token) noexcept
    {
        return RunTokens(&token, 1);
    }

    bool IsEndToken(UINT32 token) const noexcept
    {
        return std::find(m_eosTokens.begin(), m_eosTokens.end(), token) != m_eosTokens.end();
    }

    HRESULT GenerateEncoded(const EncodePrompt& encodePrompt, UINT32 maxNewTokens,
                            const FragmentCallback& callback, GenerationResult& result) noexcept
    try
    {
        RETURN_IF_FAILED(Reset());

        UINT32 promptTokenCount = 0;
        UINT32* promptTokens = nullptr;
        RETURN_IF_FAILED(encodePrompt(&promptTokenCount, &promptTokens));
        std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> ownedPromptTokens(promptTokens,
                                                                            CoTaskMemFree);
        RETURN_HR_IF(E_INVALIDARG, promptTokenCount == 0 || promptTokens == nullptr);
        RETURN_IF_FAILED(ValidateTokenIds(promptTokens, promptTokenCount, L"prompt"));
        result.stats.promptTokenCount = promptTokenCount;

        // Pattern 3: query the built stateful stage for the effective sequence
        // capacity before running the prompt.
        UINT64 capacity = 0;
        RETURN_IF_FAILED(m_statefulStage->GetSequenceCapacity(&capacity));
        if (capacity != 0 && promptTokenCount >= capacity)
        {
            result.hr = S_FALSE;
            result.stopReason = StopReason::Capacity;
            return S_FALSE;
        }

        const auto started = std::chrono::steady_clock::now();
        RETURN_IF_FAILED(RunTokens(promptTokens, promptTokenCount));

        const UINT32 tokenLimit = maxNewTokens == 0 ? 256 : maxNewTokens;
        auto firstTokenTime = started;
        bool producedFirstToken = false;

        for (UINT32 index = 0; index < tokenLimit; ++index)
        {
            UINT64 position = 0;
            RETURN_IF_FAILED(m_statefulStage->GetSequencePosition(&position));
            if (capacity != 0 && position >= capacity)
            {
                result.hr = S_FALSE;
                result.stopReason = StopReason::Capacity;
                break;
            }

            // Pattern 9: read logits from the retained output, choose the next
            // token in the app, and decode a streaming fragment.
            std::vector<float> logits;
            RETURN_IF_FAILED(ReadLastTokenLogits(m_stage.get(), 0, logits, m_tokenTarget.get()));
            const UINT32 token = ArgmaxToken(logits);
            if (IsEndToken(token))
            {
                result.stopReason = StopReason::EndOfSequence;
                break;
            }

            result.tokens.push_back(token);

            if (!producedFirstToken)
            {
                firstTokenTime = std::chrono::steady_clock::now();
                producedFirstToken = true;
            }

            LPCWSTR fragment = nullptr;
            RETURN_IF_FAILED(m_decoder->DecodeToken(token, &fragment));
            ++result.stats.generatedTokenCount;
            if (fragment != nullptr)
            {
                result.text += fragment;
                if (callback && !callback(fragment))
                {
                    result.hr = HRESULT_FROM_WIN32(ERROR_CANCELLED);
                    result.stopReason = StopReason::Canceled;
                    break;
                }
            }

            if (capacity != 0 && position + 1 >= capacity)
            {
                result.hr = S_FALSE;
                result.stopReason = StopReason::Capacity;
                break;
            }

            if (index + 1 < tokenLimit)
            {
                RETURN_IF_FAILED(RunToken(token));
            }
        }

        const auto completed = std::chrono::steady_clock::now();
        result.stats.totalDurationInMilliseconds =
            std::chrono::duration<double, std::milli>(completed - started).count();
        if (producedFirstToken)
        {
            result.stats.timeToFirstTokenInMilliseconds =
                std::chrono::duration<double, std::milli>(firstTokenTime - started).count();
            result.stats.decodeDurationInMilliseconds =
                std::chrono::duration<double, std::milli>(completed - firstTokenTime).count();
        }

        if (result.stats.totalDurationInMilliseconds > 0.0)
        {
            result.stats.tokensPerSecond = static_cast<double>(result.stats.generatedTokenCount) *
                                           1000.0 / result.stats.totalDurationInMilliseconds;
        }

        return result.hr;
    }
    CATCH_RETURN()

    wil::com_ptr<IWinMLRuntime> m_runtime;
    wil::com_ptr<IWinMLExecutionTarget> m_stageTarget;
    wil::com_ptr<IWinMLExecutionTarget> m_tokenTarget;
    wil::com_ptr<IWinMLModel> m_model;
    wil::com_ptr<IWinMLStage> m_stage;
    wil::com_ptr<IWinMLStatefulStage> m_statefulStage;
    wil::com_ptr<IWinMLPipeline> m_pipeline;
    wil::com_ptr<IWinMLTokenizer> m_tokenizer;
    wil::com_ptr<IWinMLStructuredConversationFormatter> m_formatter;
    wil::com_ptr<IWinMLTokenizerDecoder> m_decoder;
    WINML_TENSOR_DATA_TYPE m_tokenDataType = WINML_TENSOR_DATA_TYPE_UNDEFINED;
    UINT32 m_tokenDimensionCount = 0;
    UINT32 m_vocabularySize = 0; // 0 = the model leaves the vocabulary dynamic
    std::vector<UINT32> m_eosTokens;
};

// Convenience wrapper for samples that need the shared unified language runner.
inline HRESULT LoadLanguageModel(_In_z_ PCWSTR modelPath, _In_opt_z_ PCWSTR tokenizerSource,
                                 const DeviceArgs& device, UINT64 contextCapacityHint,
                                 _Out_ std::unique_ptr<LanguageModel>& languageModel) noexcept
{
    return LanguageModel::Load(modelPath, tokenizerSource, device, contextCapacityHint,
                               languageModel);
}

inline HRESULT GetResolvedTargetKind(LanguageModel& model,
                                     _Out_ WINML_EXECUTION_TARGET_KIND& kind) noexcept
{
    kind = {};
    RETURN_HR_IF_NULL(E_UNEXPECTED, model.Stage());

    wil::com_ptr<IWinMLExecutionTarget> target;
    RETURN_IF_FAILED(model.Stage()->GetExecutionTarget(target.put()));
    RETURN_HR_IF_NULL(E_UNEXPECTED, target);
    return target->GetKind(&kind);
}

} // namespace winmlsamples::language
