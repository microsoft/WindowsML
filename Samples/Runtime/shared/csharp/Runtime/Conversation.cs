// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Sample code, not a supported API. Marshals a ConversationRequest into the
// native WINML_CONVERSATION_REQUEST that the structured conversation
// formatter and chat completion sessions take. The Runtime does not retain the
// request, so the native copy lives only for the call.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

namespace WinMLSamples.Runtime;

// Owns unmanaged copies of the request's strings and arrays until disposed.
internal sealed unsafe class NativeConversationRequest : IDisposable
{
    private readonly List<nint> _allocations = [];
    private WINML_CONVERSATION_REQUEST* _request;

    public NativeConversationRequest(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        try
        {
            WINML_CONVERSATION_REQUEST* native = Allocate<WINML_CONVERSATION_REQUEST>(1);
            native->formatterMode = (WINML_CONVERSATION_FORMATTER_MODE)request.FormatterMode;
            native->messageCount = (uint)request.Messages.Count;
            native->messages = CopyMessages(request.Messages);
            IReadOnlyList<ConversationTool> tools = request.Tools ?? [];
            native->toolCount = (uint)tools.Count;
            native->tools = CopyTools(tools);
            native->toolChoice = (WINML_CONVERSATION_TOOL_CHOICE)request.ToolChoice;
            native->namedToolChoice = Copy(request.NamedToolChoice);
            native->parallelToolCalls = (WINML_CONVERSATION_PARALLEL_TOOL_CALLS)request.ParallelToolCalls;
            native->addGenerationPrompt = request.AddGenerationPrompt;
            native->responseFormat = (WINML_CONVERSATION_RESPONSE_FORMAT)request.ResponseFormat;
            native->responseSchemaName = Copy(request.ResponseSchemaName);
            native->responseSchemaDescription = Copy(request.ResponseSchemaDescription);
            native->responseSchemaJson = Copy(request.ResponseSchemaJson);
            native->responseSchemaStrict = request.ResponseSchemaStrict;
            native->enableThinking = (WINML_CONVERSATION_ENABLE_THINKING)request.EnableThinking;
            _request = native;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public WINML_CONVERSATION_REQUEST* Pointer =>
        _request != null ? _request : throw new ObjectDisposedException(nameof(NativeConversationRequest));

    public void Dispose()
    {
        _request = null;
        foreach (nint allocation in _allocations)
        {
            NativeMemory.Free((void*)allocation);
        }

        _allocations.Clear();
    }

    private WINML_CONVERSATION_MESSAGE* CopyMessages(IReadOnlyList<ConversationMessage> messages)
    {
        WINML_CONVERSATION_MESSAGE* native = Allocate<WINML_CONVERSATION_MESSAGE>(messages.Count);
        for (int i = 0; i < messages.Count; ++i)
        {
            ConversationMessage message = messages[i] ?? throw new ArgumentException("A conversation message is null.");
            IReadOnlyList<ConversationToolCall> toolCalls = message.ToolCalls ?? [];
            native[i].role = Copy(message.Role ?? throw new ArgumentException("A conversation message has no role."));
            native[i].content = Copy(message.Content);
            native[i].reasoningContent = Copy(message.ReasoningContent);
            native[i].toolCallCount = (uint)toolCalls.Count;
            native[i].toolCalls = CopyToolCalls(toolCalls);
            native[i].toolCallId = Copy(message.ToolCallId);
            native[i].toolName = Copy(message.ToolName);
            IReadOnlyList<ConversationContentPart> parts = message.ContentParts ?? [];
            native[i].contentPartCount = (uint)parts.Count;
            native[i].contentParts = CopyContentParts(parts);
        }

        return native;
    }

    private WINML_CONVERSATION_CONTENT_PART* CopyContentParts(IReadOnlyList<ConversationContentPart> parts)
    {
        WINML_CONVERSATION_CONTENT_PART* native = Allocate<WINML_CONVERSATION_CONTENT_PART>(parts.Count);
        for (int i = 0; i < parts.Count; ++i)
        {
            ConversationContentPart part = parts[i] ?? throw new ArgumentException("A content part is null.");
            native[i].type = Copy(part.Type ?? throw new ArgumentException("A content part has no type."));
            native[i].text = Copy(part.Text);
            native[i].jsonValue = Copy(part.JsonValue);
        }

        return native;
    }

    private WINML_CONVERSATION_TOOL_CALL* CopyToolCalls(IReadOnlyList<ConversationToolCall> toolCalls)
    {
        WINML_CONVERSATION_TOOL_CALL* native = Allocate<WINML_CONVERSATION_TOOL_CALL>(toolCalls.Count);
        for (int i = 0; i < toolCalls.Count; ++i)
        {
            native[i].id = Copy(toolCalls[i].Id);
            native[i].name = Copy(toolCalls[i].Name);
            native[i].argumentsJson = Copy(toolCalls[i].ArgumentsJson);
        }

        return native;
    }

    private WINML_CONVERSATION_TOOL* CopyTools(IReadOnlyList<ConversationTool> tools)
    {
        WINML_CONVERSATION_TOOL* native = Allocate<WINML_CONVERSATION_TOOL>(tools.Count);
        for (int i = 0; i < tools.Count; ++i)
        {
            native[i].name = Copy(tools[i].Name);
            native[i].description = Copy(tools[i].Description);
            native[i].parametersJson = Copy(tools[i].ParametersJson);
        }

        return native;
    }

    // Returns null for an empty array, as the native request expects.
    private T* Allocate<T>(int count)
        where T : unmanaged
    {
        if (count == 0)
        {
            return null;
        }

        T* memory = (T*)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
        _allocations.Add((nint)memory);
        return memory;
    }

    private PCWSTR Copy(string? value)
    {
        if (value is null)
        {
            return default;
        }

        char* copy = (char*)NativeMemory.Alloc((nuint)(value.Length + 1), sizeof(char));
        _allocations.Add((nint)copy);
        value.AsSpan().CopyTo(new Span<char>(copy, value.Length));
        copy[value.Length] = '\0';
        return new PCWSTR(copy);
    }
}
