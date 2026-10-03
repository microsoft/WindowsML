// Copyright (C) Microsoft Corporation. All rights reserved.
//
// C++ client: call the server's OpenAI-compatible endpoint over HTTP with
// WinHTTP, and read and write JSON with Windows.Data.Json.
//
// Connect to a running Windows ML Server, list its models, request a plain
// and a streamed reply, and complete one tool call.
//
//   The base URL and key come from WINMLSERVER_BASE_URL and
//   WINMLSERVER_ACCESS_KEY; the key goes in the bearer header. GET /models
//   reads each model's limits and tool support from the server's winml field.
//   POST /chat/completions then sends a plain request, a streamed request
//   read as server-sent events until [DONE], and, if tools are supported, a
//   request with tool_choice "required"; the client runs multiply itself and
//   returns the result with tool_choice "none". Every request asks for at
//   most 256 tokens or the model's lower limit.
//
// Run it
//   ..\..\run_in_process_server.ps1 -Client cpp
//   ..\..\run_server_executable.ps1 -Client cpp
//
// Learn more (paths relative to this file)
//   ../README.md
//   ../../../../docs/api-reference/IWinMLServer.md

#include <windows.h>
#include <winhttp.h>

#include <wil/cppwinrt.h>
#include <wil/resource.h>
#include <wil/result.h>

#include <winrt/Windows.Data.Json.h>
#include <winrt/Windows.Foundation.Collections.h>

#include <fcntl.h>
#include <io.h>

#include <cstdio>
#include <cwchar>
#include <string>
#include <string_view>
#include <vector>

namespace
{

using winrt::Windows::Data::Json::IJsonValue;
using winrt::Windows::Data::Json::JsonArray;
using winrt::Windows::Data::Json::JsonObject;
using winrt::Windows::Data::Json::JsonValue;
using winrt::Windows::Data::Json::JsonValueType;

// The host sets these variables when it starts the client.
constexpr wchar_t BaseUrlVariable[] = L"WINMLSERVER_BASE_URL";
constexpr wchar_t AccessKeyVariable[] = L"WINMLSERVER_ACCESS_KEY";

// Each request asks for at most this many tokens, or the model's own limit if
// that is lower. A request above the model's limit is rejected.
constexpr UINT32 RequestedOutputTokens = 256;

// Bit 0 of capability_flags in the model list means the model supports tool
// calls.
constexpr UINT32 ToolCallsCapability = 0x1;

std::wstring ReadEnvironment(LPCWSTR name)
{
    const DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
    if (length == 0)
    {
        return {};
    }

    std::wstring value(length, L'\0');
    value.resize(GetEnvironmentVariableW(name, value.data(), length));
    return value;
}

// One connection to the server. Every request carries the access key in its
// Authorization header; the server answers 401 without it.
class ServerConnection
{
public:
    ServerConnection(const std::wstring& baseUrl, const std::wstring& accessKey)
    {
        URL_COMPONENTS parts{};
        parts.dwStructSize = sizeof(parts);
        parts.dwHostNameLength = static_cast<DWORD>(-1);
        parts.dwUrlPathLength = static_cast<DWORD>(-1);
        THROW_IF_WIN32_BOOL_FALSE(WinHttpCrackUrl(baseUrl.c_str(), 0, 0, &parts));
        THROW_HR_IF(E_INVALIDARG, parts.nScheme != INTERNET_SCHEME_HTTP);
        const std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
        m_basePath.assign(parts.lpszUrlPath, parts.dwUrlPathLength);

        // The server listens only on this computer, so requests never go
        // through a proxy.
        m_session.reset(WinHttpOpen(L"WindowsML-Server-Sample/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY,
                                    WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
        THROW_LAST_ERROR_IF_NULL(m_session.get());

        // A local model can take longer than WinHTTP's 30-second default to
        // finish a reply, so wait up to 10 minutes for response data.
        THROW_IF_WIN32_BOOL_FALSE(WinHttpSetTimeouts(m_session.get(), 0, 60000, 30000, 600000));
        m_connection.reset(WinHttpConnect(m_session.get(), host.c_str(), parts.nPort, 0));
        THROW_LAST_ERROR_IF_NULL(m_connection.get());

        // Build the header in place, so no temporary copy of the key is left
        // in memory.
        constexpr std::wstring_view authorization = L"Authorization: Bearer ";
        constexpr std::wstring_view contentType = L"\r\nContent-Type: application/json\r\n";
        m_headers.reserve(authorization.size() + accessKey.size() + contentType.size());
        m_headers.append(authorization).append(accessKey).append(contentType);
    }

    ~ServerConnection()
    {
        SecureZeroMemory(m_headers.data(), m_headers.size() * sizeof(wchar_t));
    }

    ServerConnection(const ServerConnection&) = delete;
    ServerConnection& operator=(const ServerConnection&) = delete;

    JsonObject Get(std::wstring_view path)
    {
        return ReadJson(Send(L"GET", path, {}));
    }

    JsonObject Post(std::wstring_view path, const JsonObject& body)
    {
        return ReadJson(Send(L"POST", path, winrt::to_string(body.Stringify())));
    }

    // Send a streaming request and pass the JSON in each server-sent event to
    // onEvent. The stream ends with an event whose data is [DONE].
    template <typename Handler>
    void PostStreaming(std::wstring_view path, const JsonObject& body, Handler&& onEvent)
    {
        auto request = Send(L"POST", path, winrt::to_string(body.Stringify()));
        if (QueryStatus(request.get()) != HTTP_STATUS_OK)
        {
            // An error response is one JSON body rather than a stream.
            ReadJson(std::move(request));
            return;
        }

        // An event is one or more "data:" lines followed by a blank line.
        std::string pending;
        std::string eventData;
        ReadBody(request.get(), [&](std::string_view chunk) {
            pending.append(chunk);
            size_t lineEnd = 0;
            while ((lineEnd = pending.find('\n')) != std::string::npos)
            {
                std::string_view line(pending.data(), lineEnd);
                if (!line.empty() && line.back() == '\r')
                {
                    line.remove_suffix(1);
                }

                if (line.empty() && !eventData.empty())
                {
                    if (eventData != "[DONE]")
                    {
                        const JsonObject event = JsonObject::Parse(winrt::to_hstring(eventData));
                        ThrowIfError(HTTP_STATUS_OK, event);
                        onEvent(event);
                    }

                    eventData.clear();
                }
                else if (line.starts_with("data:"))
                {
                    line.remove_prefix(line.starts_with("data: ") ? 6 : 5);
                    if (!eventData.empty())
                    {
                        eventData.push_back('\n');
                    }

                    eventData.append(line);
                }

                pending.erase(0, lineEnd + 1);
            }
        });
    }

private:
    wil::unique_winhttp_hinternet Send(LPCWSTR verb, std::wstring_view path, std::string body)
    {
        const std::wstring target = m_basePath + std::wstring(path);
        wil::unique_winhttp_hinternet request(
            WinHttpOpenRequest(m_connection.get(), verb, target.c_str(), nullptr,
                               WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0));
        THROW_LAST_ERROR_IF_NULL(request.get());
        THROW_IF_WIN32_BOOL_FALSE(WinHttpSendRequest(
            request.get(), m_headers.c_str(), static_cast<DWORD>(m_headers.size()),
            body.empty() ? WINHTTP_NO_REQUEST_DATA : body.data(), static_cast<DWORD>(body.size()),
            static_cast<DWORD>(body.size()), 0));
        THROW_IF_WIN32_BOOL_FALSE(WinHttpReceiveResponse(request.get(), nullptr));
        return request;
    }

    static DWORD QueryStatus(HINTERNET request)
    {
        DWORD status = 0;
        DWORD size = sizeof(status);
        THROW_IF_WIN32_BOOL_FALSE(WinHttpQueryHeaders(
            request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX));
        return status;
    }

    // Pass the response body to consume as it arrives, so a streamed reply
    // can be shown before it is complete.
    template <typename Consumer>
    static void ReadBody(HINTERNET request, Consumer&& consume)
    {
        std::vector<char> buffer;
        for (;;)
        {
            DWORD available = 0;
            THROW_IF_WIN32_BOOL_FALSE(WinHttpQueryDataAvailable(request, &available));
            if (available == 0)
            {
                return;
            }

            buffer.resize(available);
            DWORD read = 0;
            THROW_IF_WIN32_BOOL_FALSE(WinHttpReadData(request, buffer.data(), available, &read));
            consume(std::string_view(buffer.data(), read));
        }
    }

    static JsonObject ReadJson(wil::unique_winhttp_hinternet request)
    {
        const DWORD status = QueryStatus(request.get());
        std::string body;
        ReadBody(request.get(), [&](std::string_view chunk) {
            body.append(chunk);
        });

        JsonObject json;
        if (!JsonObject::TryParse(winrt::to_hstring(body), json))
        {
            std::fwprintf(stderr, L"The server returned HTTP %lu without a JSON body.\n", status);
            THROW_HR(HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
        }

        ThrowIfError(status, json);
        return json;
    }

    // Errors use the OpenAI shape: {"error": {"message": ..., "code": ...}}.
    static void ThrowIfError(DWORD status, const JsonObject& json)
    {
        if (status == HTTP_STATUS_OK && !json.HasKey(L"error"))
        {
            return;
        }

        const JsonObject error = json.GetNamedObject(L"error", JsonObject());
        std::fwprintf(stderr, L"The server returned HTTP %lu: %ls\n", status,
                      error.GetNamedString(L"message", L"no message").c_str());
        THROW_HR(status == HTTP_STATUS_DENIED ? E_ACCESSDENIED : E_FAIL);
    }

    std::wstring m_basePath;
    std::wstring m_headers;
    wil::unique_winhttp_hinternet m_session;
    wil::unique_winhttp_hinternet m_connection;
};

struct ModelInfo
{
    std::wstring id;
    UINT32 outputLimit = RequestedOutputTokens;
    bool supportsTools = false;
};

double ReadNumber(const JsonObject& object, LPCWSTR name)
{
    const IJsonValue value = object.TryLookup(name);
    return value && value.ValueType() == JsonValueType::Number ? value.GetNumber() : 0.0;
}

// The model list follows the OpenAI shape. The winml field on each model is
// specific to this server and describes the limits requests are checked
// against.
ModelInfo ListModels(ServerConnection& server, const std::wstring& requestedId)
{
    const JsonArray models = server.Get(L"/models").GetNamedArray(L"data");
    std::wprintf(L"Models\n");
    ModelInfo selected;
    for (const IJsonValue& entry : models)
    {
        const JsonObject model = entry.GetObject();
        const JsonObject details = model.GetNamedObject(L"winml", JsonObject());
        ModelInfo info;
        info.id = model.GetNamedString(L"id");
        info.outputLimit = static_cast<UINT32>(ReadNumber(details, L"maximum_output_tokens"));
        info.supportsTools = (static_cast<UINT32>(ReadNumber(details, L"capability_flags")) &
                              ToolCallsCapability) != 0;
        std::wprintf(L"  %ls: context window %.0f tokens, output limit %u tokens, tool calls %ls\n",
                     info.id.c_str(), ReadNumber(details, L"context_window_tokens"),
                     info.outputLimit, info.supportsTools ? L"supported" : L"not supported");

        if ((selected.id.empty() && requestedId.empty()) || info.id == requestedId)
        {
            selected = info;
        }
    }

    if (selected.id.empty())
    {
        std::fwprintf(stderr, L"The server has no model named '%ls'.\n", requestedId.c_str());
        THROW_HR(HRESULT_FROM_WIN32(ERROR_NOT_FOUND));
    }

    // A model that does not report its limit keeps the sample's own.
    if (selected.outputLimit == 0 || selected.outputLimit > RequestedOutputTokens)
    {
        selected.outputLimit = RequestedOutputTokens;
    }

    return selected;
}

JsonObject Message(LPCWSTR role, std::wstring_view content)
{
    JsonObject message;
    message.Insert(L"role", JsonValue::CreateStringValue(role));
    message.Insert(L"content", JsonValue::CreateStringValue(content));
    return message;
}

JsonObject ChatRequest(const ModelInfo& model, const JsonArray& messages)
{
    JsonObject request;
    request.Insert(L"model", JsonValue::CreateStringValue(model.id));
    request.Insert(L"messages", messages);
    request.Insert(L"max_completion_tokens", JsonValue::CreateNumberValue(model.outputLimit));
    return request;
}

// prompt_tokens counts the whole formatted prompt. When the server can reuse
// the start of a prompt it already processed, cached_tokens reports how much
// of it was reused.
void PrintUsage(const JsonObject& usage)
{
    std::wprintf(L"  Usage: %.0f prompt tokens", ReadNumber(usage, L"prompt_tokens"));
    const JsonObject details = usage.GetNamedObject(L"prompt_tokens_details", JsonObject());
    if (details.HasKey(L"cached_tokens"))
    {
        std::wprintf(L" (%.0f reused)", ReadNumber(details, L"cached_tokens"));
    }

    std::wprintf(L", %.0f completion tokens\n", ReadNumber(usage, L"completion_tokens"));
}

std::wstring ReadContent(const JsonObject& message)
{
    const IJsonValue content = message.TryLookup(L"content");
    return content && content.ValueType() == JsonValueType::String
               ? std::wstring(content.GetString())
               : std::wstring();
}

void CompleteChat(ServerConnection& server, const ModelInfo& model)
{
    constexpr wchar_t prompt[] = L"Name three primary colors.";
    std::wprintf(L"\nChat completion\n  User: %ls\n", prompt);

    JsonArray messages;
    messages.Append(Message(L"system", L"You are a helpful assistant. Answer briefly."));
    messages.Append(Message(L"user", prompt));
    const JsonObject response = server.Post(L"/chat/completions", ChatRequest(model, messages));

    const JsonObject choice = response.GetNamedArray(L"choices").GetObjectAt(0);
    std::wprintf(L"  Assistant: %ls\n", ReadContent(choice.GetNamedObject(L"message")).c_str());
    PrintUsage(response.GetNamedObject(L"usage"));
}

// A streamed reply arrives as chunks, each with a delta to append. With
// include_usage set, a final chunk with no choices carries the usage.
void StreamChat(ServerConnection& server, const ModelInfo& model)
{
    constexpr wchar_t prompt[] = L"Count from one to five in words.";
    std::wprintf(L"\nStreaming\n  User: %ls\n  Assistant: ", prompt);

    JsonArray messages;
    messages.Append(Message(L"user", prompt));
    JsonObject request = ChatRequest(model, messages);
    request.Insert(L"stream", JsonValue::CreateBooleanValue(true));
    JsonObject streamOptions;
    streamOptions.Insert(L"include_usage", JsonValue::CreateBooleanValue(true));
    request.Insert(L"stream_options", streamOptions);

    JsonObject usage;
    server.PostStreaming(L"/chat/completions", request, [&](const JsonObject& chunk) {
        for (const IJsonValue& choice : chunk.GetNamedArray(L"choices", JsonArray()))
        {
            const JsonObject delta = choice.GetObject().GetNamedObject(L"delta", JsonObject());
            std::wprintf(L"%ls", ReadContent(delta).c_str());
            std::fflush(stdout);
        }

        if (chunk.HasKey(L"usage"))
        {
            usage = chunk.GetNamedObject(L"usage");
        }
    });

    std::wprintf(L"\n");
    PrintUsage(usage);
}

JsonObject MultiplyTool()
{
    // The parameters are a JSON schema. When the model supports tool calls,
    // the server makes its arguments match the schema.
    return JsonObject::Parse(LR"({
        "type": "function",
        "function": {
            "name": "multiply",
            "description": "Multiply two numbers.",
            "parameters": {
                "type": "object",
                "properties": {
                    "a": { "type": "number" },
                    "b": { "type": "number" }
                },
                "required": [ "a", "b" ],
                "additionalProperties": false
            }
        }
    })");
}

// The model writes the arguments as JSON text, which the client parses before
// it runs the function.
std::wstring RunTool(const JsonObject& function)
{
    const std::wstring name(function.GetNamedString(L"name"));
    JsonObject arguments;
    if (name != L"multiply" ||
        !JsonObject::TryParse(function.GetNamedString(L"arguments"), arguments))
    {
        return L"error: unknown tool or invalid arguments";
    }

    const double product = ReadNumber(arguments, L"a") * ReadNumber(arguments, L"b");
    return std::wstring(JsonValue::CreateNumberValue(product).Stringify());
}

// A tool call takes two requests. The first offers the tool and requires the
// model to call it. The client runs the call and adds the model's request and
// the result to the conversation. The second request returns the answer.
// An agent sends tool_choice "auto" instead and repeats until the model
// answers without calling a tool; this sample takes one call and then asks
// for the answer, so it always finishes in two requests.
void CallTool(ServerConnection& server, const ModelInfo& model)
{
    constexpr wchar_t prompt[] = L"What is 12 times 34? Use the multiply tool.";
    std::wprintf(L"\nTool calling\n  User: %ls\n", prompt);

    JsonArray messages;
    messages.Append(Message(L"user", prompt));
    JsonArray tools;
    tools.Append(MultiplyTool());
    JsonObject request = ChatRequest(model, messages);
    request.Insert(L"tools", tools);
    request.Insert(L"tool_choice", JsonValue::CreateStringValue(L"required"));

    const JsonObject first = server.Post(L"/chat/completions", request);
    const JsonObject reply =
        first.GetNamedArray(L"choices").GetObjectAt(0).GetNamedObject(L"message");
    messages.Append(reply);
    for (const IJsonValue& entry : reply.GetNamedArray(L"tool_calls", JsonArray()))
    {
        const JsonObject call = entry.GetObject();
        const JsonObject function = call.GetNamedObject(L"function");
        const std::wstring result = RunTool(function);
        std::wprintf(L"  Tool call: %ls(%ls) returned %ls\n",
                     function.GetNamedString(L"name").c_str(),
                     function.GetNamedString(L"arguments").c_str(), result.c_str());

        JsonObject toolMessage = Message(L"tool", result);
        toolMessage.Insert(L"tool_call_id",
                           JsonValue::CreateStringValue(call.GetNamedString(L"id")));
        messages.Append(toolMessage);
    }

    // The request holds the messages array itself, not a copy, so the second
    // request includes the reply and the results appended above.
    request.SetNamedValue(L"tool_choice", JsonValue::CreateStringValue(L"none"));
    const JsonObject second = server.Post(L"/chat/completions", request);
    const JsonObject answer =
        second.GetNamedArray(L"choices").GetObjectAt(0).GetNamedObject(L"message");
    std::wprintf(L"  Assistant: %ls\n", ReadContent(answer).c_str());
    PrintUsage(second.GetNamedObject(L"usage"));
}

} // namespace

int wmain(int argumentCount, wchar_t** arguments)
try
{
    _setmode(_fileno(stdout), _O_U8TEXT);
    _setmode(_fileno(stderr), _O_U8TEXT);

    std::wstring requestedModel;
    if (argumentCount == 3 && std::wcscmp(arguments[1], L"--model") == 0)
    {
        requestedModel = arguments[2];
    }
    else if (argumentCount != 1)
    {
        std::fwprintf(stderr, L"Usage: server-client-cpp.exe [--model <id>]\n");
        return 2;
    }

    std::wstring baseUrl = ReadEnvironment(BaseUrlVariable);
    std::wstring accessKey = ReadEnvironment(AccessKeyVariable);
    auto clearAccessKey = wil::scope_exit([&]() noexcept {
        SecureZeroMemory(accessKey.data(), accessKey.size() * sizeof(wchar_t));
    });
    if (baseUrl.empty() || accessKey.empty())
    {
        std::fwprintf(stderr, L"Set %ls and %ls, or start this client from a run script.\n",
                      BaseUrlVariable, AccessKeyVariable);
        return 2;
    }

    winrt::init_apartment();
    ServerConnection server(baseUrl, accessKey);
    clearAccessKey.reset();
    std::wprintf(L"Server: %ls\n\n", baseUrl.c_str());

    const ModelInfo model = ListModels(server, requestedModel);
    CompleteChat(server, model);
    StreamChat(server, model);
    if (model.supportsTools)
    {
        CallTool(server, model);
    }
    else
    {
        std::wprintf(L"\nTool calling: skipped, because %ls does not support tool calls.\n",
                     model.id.c_str());
    }

    return 0;
}
catch (...)
{
    const HRESULT result = wil::ResultFromCaughtException();
    std::fwprintf(stderr, L"Server client failed: 0x%08X\n", result);
    return 1;
}
