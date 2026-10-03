// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Whisper tokenizer helpers: special-token constants, vocab.json loading, and
// token-to-text decoding. These are model asset details rather than Runtime API
// usage, so they live next to the sample instead of the main inference flow.

#pragma once

#include <cerrno>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string>
#include <unordered_map>
#include <utility>

// Whisper special tokens used to prime the decoder for English transcription
// without timestamps, plus the fixed Whisper vocabulary size and a decode cap.
static constexpr int64_t kSOT = 50258;
static constexpr int64_t kEOT = 50257;
static constexpr int64_t kEnglish = 50259;
static constexpr int64_t kTranscribe = 50359;
static constexpr int64_t kNoTimestamps = 50363;
static constexpr uint32_t kVocabSize =
    51865; // Full logit dimension including special tokens; vocab.json contains fewer BPE entries
static constexpr uint32_t kMaxDecodeTokens = 128;

static bool TryParseInt64(const std::string& text, int64_t* value)
{
    if (!value || text.empty())
    {
        return false;
    }

    char* end = nullptr;
    errno = 0;
    long long parsed = std::strtoll(text.c_str(), &end, 10);
    if (errno == ERANGE || end == text.c_str() || *end != '\0')
    {
        return false;
    }

    *value = static_cast<int64_t>(parsed);
    return true;
}

static bool AppendUtf8CodePoint(unsigned int codePoint, std::string& output)
{
    if (codePoint < 0x80)
    {
        output += static_cast<char>(codePoint);
    }
    else if (codePoint < 0x800)
    {
        output += static_cast<char>(0xC0 | (codePoint >> 6));
        output += static_cast<char>(0x80 | (codePoint & 0x3F));
    }
    else if (codePoint < 0x10000)
    {
        output += static_cast<char>(0xE0 | (codePoint >> 12));
        output += static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F));
        output += static_cast<char>(0x80 | (codePoint & 0x3F));
    }
    else if (codePoint <= 0x10FFFF)
    {
        output += static_cast<char>(0xF0 | (codePoint >> 18));
        output += static_cast<char>(0x80 | ((codePoint >> 12) & 0x3F));
        output += static_cast<char>(0x80 | ((codePoint >> 6) & 0x3F));
        output += static_cast<char>(0x80 | (codePoint & 0x3F));
    }
    else
    {
        return false;
    }

    return true;
}

static bool ParseJsonHex4(const std::string& content, size_t firstDigit, unsigned int& value)
{
    if (firstDigit + 4 > content.size())
    {
        return false;
    }

    value = 0;
    for (size_t index = 0; index < 4; ++index)
    {
        const char hex = content[firstDigit + index];
        value <<= 4;
        if (hex >= '0' && hex <= '9')
        {
            value |= hex - '0';
        }
        else if (hex >= 'a' && hex <= 'f')
        {
            value |= hex - 'a' + 10;
        }
        else if (hex >= 'A' && hex <= 'F')
        {
            value |= hex - 'A' + 10;
        }
        else
        {
            return false;
        }
    }

    return true;
}

static bool ParseJsonString(const std::string& content, size_t openingQuote, std::string& value,
                            size_t& nextPosition)
{
    if (openingQuote >= content.size() || content[openingQuote] != '"')
    {
        return false;
    }

    value.clear();
    for (size_t pos = openingQuote + 1; pos < content.size(); ++pos)
    {
        const char c = content[pos];
        if (c == '"')
        {
            nextPosition = pos + 1;
            return true;
        }

        if (c != '\\')
        {
            value += c;
            continue;
        }

        if (++pos >= content.size())
        {
            return false;
        }

        switch (content[pos])
        {
        case '"':
            value += '"';
            break;
        case '\\':
            value += '\\';
            break;
        case '/':
            value += '/';
            break;
        case 'b':
            value += '\b';
            break;
        case 'f':
            value += '\f';
            break;
        case 'n':
            value += '\n';
            break;
        case 'r':
            value += '\r';
            break;
        case 't':
            value += '\t';
            break;
        case 'u':
        {
            unsigned int codePoint = 0;
            if (!ParseJsonHex4(content, pos + 1, codePoint))
            {
                return false;
            }

            if (codePoint >= 0xD800 && codePoint <= 0xDBFF)
            {
                if (pos + 10 >= content.size() || content[pos + 5] != '\\' ||
                    content[pos + 6] != 'u')
                {
                    return false;
                }

                unsigned int lowSurrogate = 0;
                if (!ParseJsonHex4(content, pos + 7, lowSurrogate) || lowSurrogate < 0xDC00 ||
                    lowSurrogate > 0xDFFF)
                {
                    return false;
                }

                codePoint = 0x10000 + ((codePoint - 0xD800) << 10) + (lowSurrogate - 0xDC00);
                pos += 10;
            }
            else
            {
                if (codePoint >= 0xDC00 && codePoint <= 0xDFFF)
                {
                    return false;
                }

                pos += 4;
            }

            if (!AppendUtf8CodePoint(codePoint, value))
            {
                return false;
            }

            break;
        }
        default:
            return false;
        }
    }

    return false;
}

// Parses vocab.json into an id -> token map. Accepts both id-to-token and
// token-to-id JSON layouts.
static std::unordered_map<int64_t, std::string> LoadVocab(const wchar_t* path)
{
    std::unordered_map<int64_t, std::string> vocab;
    std::ifstream f(std::filesystem::path(path), std::ios::binary);
    if (!f.is_open())
    {
        return vocab;
    }

    std::string content((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    size_t pos = 0;
    while ((pos = content.find('"', pos)) != std::string::npos)
    {
        std::string key;
        size_t afterKey = 0;
        if (!ParseJsonString(content, pos, key, afterKey))
        {
            break;
        }

        size_t colon = afterKey;
        while (colon < content.size() && (content[colon] == ' ' || content[colon] == '\t' ||
                                          content[colon] == '\r' || content[colon] == '\n'))
        {
            ++colon;
        }

        if (colon >= content.size() || content[colon] != ':')
        {
            pos = afterKey;
            continue;
        }

        size_t valueStart = colon + 1;
        while (valueStart < content.size() &&
               (content[valueStart] == ' ' || content[valueStart] == '\t' ||
                content[valueStart] == '\r' || content[valueStart] == '\n'))
        {
            ++valueStart;
        }

        if (valueStart < content.size() && content[valueStart] == '"')
        {
            std::string token;
            size_t afterValue = 0;
            if (!ParseJsonString(content, valueStart, token, afterValue))
            {
                break;
            }

            int64_t tokenId = 0;
            if (TryParseInt64(key, &tokenId))
            {
                vocab[tokenId] = std::move(token);
            }

            pos = afterValue;
        }
        else
        {
            size_t valueEnd = valueStart;
            while (valueEnd < content.size() &&
                   ((content[valueEnd] >= '0' && content[valueEnd] <= '9') ||
                    content[valueEnd] == '-'))
            {
                ++valueEnd;
            }

            int64_t value = 0;
            if (TryParseInt64(content.substr(valueStart, valueEnd - valueStart), &value))
            {
                vocab[value] = std::move(key);
            }

            pos = valueEnd;
        }
    }

    return vocab;
}

static bool TryReadUtf8CodePoint(const std::string& text, size_t& position, unsigned int& codePoint)
{
    if (position >= text.size())
    {
        return false;
    }

    const unsigned char lead = static_cast<unsigned char>(text[position]);
    size_t length = 0;
    unsigned int value = 0;
    if (lead < 0x80)
    {
        length = 1;
        value = lead;
    }
    else if ((lead & 0xE0) == 0xC0)
    {
        length = 2;
        value = lead & 0x1F;
    }
    else if ((lead & 0xF0) == 0xE0)
    {
        length = 3;
        value = lead & 0x0F;
    }
    else if ((lead & 0xF8) == 0xF0)
    {
        length = 4;
        value = lead & 0x07;
    }
    else
    {
        return false;
    }

    if (position + length > text.size())
    {
        return false;
    }

    for (size_t index = 1; index < length; ++index)
    {
        const unsigned char continuation = static_cast<unsigned char>(text[position + index]);
        if ((continuation & 0xC0) != 0x80)
        {
            return false;
        }

        value = (value << 6) | (continuation & 0x3F);
    }

    if ((length == 2 && value < 0x80) || (length == 3 && value < 0x800) ||
        (length == 4 && value < 0x10000) || (value >= 0xD800 && value <= 0xDFFF) ||
        value > 0x10FFFF)
    {
        return false;
    }

    position += length;
    codePoint = value;
    return true;
}

static bool IsDirectGpt2Byte(unsigned int value)
{
    return (value >= 33 && value <= 126) || (value >= 161 && value <= 172) ||
           (value >= 174 && value <= 255);
}

static bool TryMapGpt2CodePointToByte(unsigned int codePoint, unsigned char& value)
{
    if (IsDirectGpt2Byte(codePoint))
    {
        value = static_cast<unsigned char>(codePoint);
        return true;
    }

    if (codePoint < 256 || codePoint >= 324)
    {
        return false;
    }

    unsigned int missingIndex = codePoint - 256;
    for (unsigned int candidate = 0; candidate < 256; ++candidate)
    {
        if (IsDirectGpt2Byte(candidate))
        {
            continue;
        }

        if (missingIndex == 0)
        {
            value = static_cast<unsigned char>(candidate);
            return true;
        }

        --missingIndex;
    }

    return false;
}

class WhisperTokenDecoder
{
public:
    explicit WhisperTokenDecoder(const std::unordered_map<int64_t, std::string>& vocabulary) :
        m_vocabulary(vocabulary)
    {
    }

    std::string DecodeToken(int64_t id)
    {
        if (id >= kEOT)
        {
            return {};
        }

        const auto token = m_vocabulary.find(id);
        if (token == m_vocabulary.end())
        {
            return {};
        }

        size_t position = 0;
        while (position < token->second.size())
        {
            unsigned int codePoint = 0;
            unsigned char value = 0;
            if (!TryReadUtf8CodePoint(token->second, position, codePoint) ||
                !TryMapGpt2CodePointToByte(codePoint, value))
            {
                m_pending += "\xEF\xBF\xBD";
                break;
            }

            m_pending += static_cast<char>(value);
        }

        return Drain(false);
    }

    std::string Finish()
    {
        return Drain(true);
    }

private:
    static void AppendReplacement(std::string& output)
    {
        output += "\xEF\xBF\xBD";
    }

    std::string Drain(bool final)
    {
        std::string output;
        size_t position = 0;
        while (position < m_pending.size())
        {
            const unsigned char lead = static_cast<unsigned char>(m_pending[position]);
            size_t length = 0;
            unsigned int minimum = 0;
            unsigned int value = 0;
            if (lead < 0x80)
            {
                output += static_cast<char>(lead);
                ++position;
                continue;
            }
            else if ((lead & 0xE0) == 0xC0)
            {
                length = 2;
                minimum = 0x80;
                value = lead & 0x1F;
            }
            else if ((lead & 0xF0) == 0xE0)
            {
                length = 3;
                minimum = 0x800;
                value = lead & 0x0F;
            }
            else if ((lead & 0xF8) == 0xF0)
            {
                length = 4;
                minimum = 0x10000;
                value = lead & 0x07;
            }
            else
            {
                AppendReplacement(output);
                ++position;
                continue;
            }

            if (position + length > m_pending.size())
            {
                break;
            }

            bool valid = true;
            for (size_t index = 1; index < length; ++index)
            {
                const unsigned char continuation =
                    static_cast<unsigned char>(m_pending[position + index]);
                if ((continuation & 0xC0) != 0x80)
                {
                    valid = false;
                    break;
                }

                value = (value << 6) | (continuation & 0x3F);
            }

            if (!valid || value < minimum || (value >= 0xD800 && value <= 0xDFFF) ||
                value > 0x10FFFF)
            {
                AppendReplacement(output);
                ++position;
                continue;
            }

            output.append(m_pending, position, length);
            position += length;
        }

        m_pending.erase(0, position);
        if (final && !m_pending.empty())
        {
            AppendReplacement(output);
            m_pending.clear();
        }

        return output;
    }

    const std::unordered_map<int64_t, std::string>& m_vocabulary;
    std::string m_pending;
};

enum class WhisperStopReason
{
    EndOfTranscript,
    MaxTokens,
    Capacity,
};

static const wchar_t* WhisperStopReasonText(WhisperStopReason reason)
{
    switch (reason)
    {
    case WhisperStopReason::EndOfTranscript:
        return L"EOT";
    case WhisperStopReason::MaxTokens:
        return L"maximum token limit";
    case WhisperStopReason::Capacity:
        return L"decoder capacity";
    default:
        return L"unknown";
    }
}
