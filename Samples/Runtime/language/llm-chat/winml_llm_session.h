// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Split language session helper used by language/llm-chat/main.cpp.
//
// This file wraps the caller-driven generation loop over the LlmPipeline from
// winml_llm_pipeline.h: apply a tokenizer chat template, bind token/position/mask
// tensors, run the pipeline, read logits, choose a token, and stream
// IWinMLTokenizerDecoder fragments.
//
// Learn more (paths relative to this file)
//   ../../../../docs/Runtime/tutorials/03-language-models.md
//   ../../../../docs/api-reference/CommonPatterns.md (patterns 3 and 9)
//   ../../../../docs/api-reference/IWinMLStructuredConversationFormatter.md
//   ../../../../docs/api-reference/IWinMLTokenizerDecoder.md

#pragma once

#include <algorithm>
#include <functional>
#include <limits>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include <WinMLConversation.h>
#include <WinMLRuntime.h>
#include <WinMLTokenizer.h>

#include "llm_inference.h"      // CreateSingleTokenTensor, ReadLastTokenLogits, ArgmaxToken
#include "winml_llm_pipeline.h" // LlmPipeline, BindDecoderStepInputs

class LlmSession
{
public:
    LlmSession(LlmPipeline& pipeline, IWinMLTokenizer* tokenizer, UINT32 maxNewTokens, bool raw) :
        m_p(pipeline), m_tokenizer(tokenizer), m_maxNewTokens(maxNewTokens), m_raw(raw)
    {
    }

    // Initializes tokenizer metadata and streaming decoder objects needed by
    // each generated reply.
    HRESULT Initialize()
    {
        CHECK_HR_IF(!m_tokenizer, E_POINTER);
        CHECK_HR_IF(m_p.vocabSize == 0, HRESULT_FROM_WIN32(ERROR_INVALID_DATA));

        const HRESULT metadataHr =
            m_tokenizer->QueryInterface(IID_PPV_ARGS(m_tokenizerMetadata.GetAddressOf()));
        if (FAILED(metadataHr))
        {
            wprintf(L"ERROR: tokenizer does not expose required EOS metadata.\n");
            return metadataHr;
        }

        if (!m_raw)
        {
            const HRESULT formatterHr =
                m_tokenizer->QueryInterface(IID_PPV_ARGS(m_formatter.GetAddressOf()));
            if (FAILED(formatterHr))
            {
                wprintf(L"ERROR: tokenizer does not expose the required conversation formatter.\n");
                return formatterHr;
            }
        }

        // One IWinMLTokenizerDecoder owns incremental text state for this
        // session's generated stream.
        CHECK_HR(m_tokenizer->CreateDecoder(m_textDecoder.GetAddressOf()));

        UINT32 eosCount = 0;
        UINT32* eosTokens = nullptr;
        const HRESULT eosHr = m_tokenizerMetadata->GetEosTokenIds(&eosCount, &eosTokens);
        std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> ownedEosTokens(eosTokens, CoTaskMemFree);
        if (FAILED(eosHr))
        {
            wprintf(L"ERROR: failed to read required tokenizer EOS metadata.\n");
            return eosHr;
        }

        if (eosCount == 0 || eosTokens == nullptr)
        {
            wprintf(L"ERROR: tokenizer EOS metadata is empty.\n");
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }

        std::vector<UINT32> tokenizerEndTokens;
        tokenizerEndTokens.reserve(eosCount);
        for (UINT32 index = 0; index < eosCount; ++index)
        {
            if (eosTokens[index] >= m_p.vocabSize)
            {
                wprintf(L"ERROR: tokenizer EOS token id %u is outside the model vocab (%u).\n",
                        eosTokens[index], m_p.vocabSize);
                return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
            }

            tokenizerEndTokens.push_back(eosTokens[index]);
        }

        m_eosTokenIds = std::move(tokenizerEndTokens);

        m_initialized = true;
        return S_OK;
    }

    // Clears the conversation: history plus all per-sequence Runtime state.
    HRESULT Reset()
    {
        CHECK_HR_IF(!m_initialized, E_UNEXPECTED);
        m_history.clear();
        return ResetGenerationState();
    }

    // Produces one assistant reply for userPrompt, streaming text to `sink`.
    // Returns S_OK on success, S_FALSE if the fixed context is exhausted, and
    // ERROR_CANCELLED when the sink asks generation to stop.
    HRESULT Generate(const std::wstring& userPrompt, const LlmTokenSink& sink)
    {
        CHECK_HR_IF(!m_initialized, E_UNEXPECTED);
        CHECK_HR(ResetGenerationState());

        m_history.push_back({L"user", userPrompt});
        struct HistoryRollback
        {
            std::vector<std::pair<std::wstring, std::wstring>>& history;
            bool committed = false;

            ~HistoryRollback()
            {
                if (!committed)
                {
                    history.pop_back();
                }
            }
        } historyRollback{m_history};

        UINT32* rawIds = nullptr;
        UINT32 count = 0;
        CHECK_HR(BuildPromptTokens(userPrompt, &count, &rawIds));
        std::unique_ptr<UINT32, decltype(&CoTaskMemFree)> owned(rawIds, CoTaskMemFree);
        CHECK_HR_IF(count == 0 || rawIds == nullptr, E_INVALIDARG);

        for (UINT32 i = 0; i < count; ++i)
        {
            if (rawIds[i] >= m_p.vocabSize)
            {
                wprintf(L"ERROR: token id %u is outside the model vocab (%u). "
                        L"Tokenizer and model directory do not match.\n",
                        rawIds[i], m_p.vocabSize);
                return E_INVALIDARG;
            }
        }

        if (count >= m_p.contextLength)
        {
            return S_FALSE; // prompt does not fit; roll back
        }

        std::wstring reply;
        bool generatedAnyToken = false;
        const HRESULT generationHr =
            GenerateHostSampled(rawIds, count, sink, reply, generatedAnyToken);

        if (generationHr == S_OK || (generationHr == S_FALSE && generatedAnyToken))
        {
            m_history.push_back({L"assistant", reply});
            historyRollback.committed = true;
        }

        return generationHr;
    }

private:
    HRESULT ResetGenerationState()
    {
        CHECK_HR(m_p.pipeline->ResetExecutionState());
        CHECK_HR_IF(!m_textDecoder, E_NOINTERFACE);
        return m_textDecoder->Reset();
    }

    bool IsEndToken(UINT32 token) const noexcept
    {
        return std::find(m_eosTokenIds.begin(), m_eosTokenIds.end(), token) != m_eosTokenIds.end();
    }

    HRESULT BuildPromptTokens(const std::wstring& userPrompt, UINT32* count, UINT32** ids)
    {
        if (m_raw)
        {
            return m_tokenizer->Encode(userPrompt.c_str(),
                                       WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS, count, ids);
        }

        std::vector<WINML_CONVERSATION_MESSAGE> messages;
        messages.reserve(m_history.size());
        for (const auto& turn : m_history)
        {
            messages.push_back({turn.first.c_str(), turn.second.c_str()});
        }

        // The formatter renders the model's own chat template over the whole
        // history and ends the prompt with the assistant generation prompt.
        CHECK_HR_IF(!m_formatter, E_NOINTERFACE);
        WINML_CONVERSATION_REQUEST request = {};
        request.formatterMode = WINML_CONVERSATION_FORMATTER_MODE_MODEL_DEFAULT;
        request.messageCount = static_cast<UINT32>(messages.size());
        request.messages = messages.data();
        request.addGenerationPrompt = TRUE;

        ComPtr<IWinMLConversationFormatResult> formatted;
        CHECK_HR(m_formatter->FormatConversation(&request, formatted.GetAddressOf()));
        return formatted->GetTokenIds(count, ids);
    }

    HRESULT GenerateHostSampled(const UINT32* promptTokens, UINT32 promptTokenCount,
                                const LlmTokenSink& sink, std::wstring& reply,
                                bool& generatedAnyToken)
    {
        CHECK_HR_IF(!m_p.statefulDecoder, E_UNEXPECTED);
        for (UINT32 index = 0; index < promptTokenCount; ++index)
        {
            CHECK_HR(RunToken(promptTokens[index]));
        }

        UINT32 generated = 0;
        while (generated < m_maxNewTokens)
        {
            UINT64 position = 0;
            CHECK_HR(m_p.statefulDecoder->GetSequencePosition(&position));
            if (position >= m_p.contextLength)
            {
                return S_FALSE;
            }

            // Pattern 9: after a token run, read the latest logits row, select
            // the next token in the app, and decode it incrementally.
            std::vector<float> logits;
            CHECK_HR(ReadLastTokenLogits(m_p.headStage.Get(), m_p.headLogitsIndex, logits));

            const UINT32 tokenId = ArgmaxToken(logits);
            if (IsEndToken(tokenId))
            {
                return S_OK;
            }

            generatedAnyToken = true;
            ++generated;

            LPCWSTR text = nullptr;
            CHECK_HR(m_textDecoder->DecodeToken(tokenId, &text));
            if (text)
            {
                reply += text;
                if (sink && !sink(text))
                {
                    return HRESULT_FROM_WIN32(ERROR_CANCELLED);
                }
            }

            // Match the unified primitive runner: stop before feeding a token
            // that would leave no capacity for its resulting state.
            if (position + 1 >= m_p.contextLength)
            {
                return S_FALSE;
            }

            if (generated < m_maxNewTokens)
            {
                CHECK_HR(RunToken(tokenId));
            }
        }

        return S_OK;
    }

    // Runs one decode step for a single token: embed -> decode -> head. The app
    // binds input_ids and the per-step position_id / attention_mask; the Runtime
    // advances the KV cache.
    HRESULT RunToken(UINT32 tokenId)
    {
        UINT64 position = 0;
        CHECK_HR(m_p.statefulDecoder->GetSequencePosition(&position));
        CHECK_HR_IF(position >= m_p.contextLength, E_INVALIDARG);

        ComPtr<IWinMLTensor> tokenTensor;
        CHECK_HR(CreateSingleTokenTensor(m_p.cpuTarget.Get(), tokenId, tokenTensor.GetAddressOf()));
        CHECK_HR(m_p.embStage->BindInput(m_p.embInputIdsIndex, tokenTensor.Get()));

        CHECK_HR(BindDecoderStepInputs(m_p.decoderTensorTarget.Get(), m_p.decoderStage.Get(),
                                       m_p.decoderPositionIdIndex, m_p.decoderAttentionMaskIndex,
                                       m_p.contextLength, m_p.attentionMaskDataType, position));

        // Run advances the decoder stage's Runtime-managed sequence state.
        CHECK_HR(m_p.pipeline->Run());
        return S_OK;
    }

    LlmPipeline& m_p;
    IWinMLTokenizer* m_tokenizer;
    ComPtr<IWinMLTokenizerMetadata> m_tokenizerMetadata;
    ComPtr<IWinMLStructuredConversationFormatter> m_formatter;
    ComPtr<IWinMLTokenizerDecoder> m_textDecoder;
    UINT32 m_maxNewTokens;
    bool m_raw;
    bool m_initialized = false;
    std::vector<UINT32> m_eosTokenIds;
    std::vector<std::pair<std::wstring, std::wstring>> m_history; // (role, content)
};
