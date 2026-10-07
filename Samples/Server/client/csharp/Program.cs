// Copyright (C) Microsoft Corporation. All rights reserved.
//
// C# client: call the server's OpenAI-compatible endpoint with the OpenAI
// library for .NET.
//
// Connect to a running Windows ML Server, list its models, request a plain
// and a streamed reply, and complete one tool call.
//
//   The base URL and key come from WINMLSERVER_BASE_URL and
//   WINMLSERVER_ACCESS_KEY. The model list is read as raw JSON because the
//   library's typed list drops the server's winml field with each model's
//   limits and tool support. CompleteChat and CompleteChatStreaming send the
//   plain and streamed requests. If tools are supported, a required tool
//   choice gets a multiply call that the client runs itself before asking
//   again with tool choice none. Every request asks for at most 256 tokens
//   or the model's lower limit.
//
// Run it
//   ..\..\run_in_process_server.ps1 -Client csharp
//   ..\..\run_server_executable.ps1 -Client csharp
//
// Learn more (paths relative to this file)
//   ../README.md
//   ../../../../docs/api-reference/IWinMLServer.md

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

internal static class Program
{
    // The host sets these variables when it starts the client.
    private const string BaseUrlVariable = "WINMLSERVER_BASE_URL";
    private const string AccessKeyVariable = "WINMLSERVER_ACCESS_KEY";

    // Each request asks for at most this many tokens, or the model's own limit
    // if that is lower. A request above the model's limit is rejected.
    private const int RequestedOutputTokens = 256;

    // Bit 0 of capability_flags in the model list means the model supports
    // tool calls.
    private const long ToolCallsCapability = 0x1;

    // The parameters are a JSON schema. When the model supports tool calls,
    // the server makes its arguments match the schema.
    private static readonly ChatTool MultiplyTool = ChatTool.CreateFunctionTool(
        functionName: "multiply",
        functionDescription: "Multiply two numbers.",
        functionParameters: BinaryData.FromString("""
            {
                "type": "object",
                "properties": {
                    "a": { "type": "number" },
                    "b": { "type": "number" }
                },
                "required": [ "a", "b" ],
                "additionalProperties": false
            }
            """));

    private sealed record ModelInfo(string Id, int OutputLimit, bool SupportsTools);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? requestedModel = null;
        if (args.Length == 2 && args[0] == "--model")
        {
            requestedModel = args[1];
        }
        else if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: ServerClient.CSharp.exe [--model <id>]");
            return 2;
        }

        string? baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable);
        string? accessKey = Environment.GetEnvironmentVariable(AccessKeyVariable);
        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(accessKey))
        {
            Console.Error.WriteLine(
                $"Set {BaseUrlVariable} and {AccessKeyVariable}, or start this client from a run script.");
            return 2;
        }

        try
        {
            // The server listens only on this computer, so requests skip any
            // proxy, which would otherwise receive the access key. Like the
            // library's default client, this one doesn't follow redirects and
            // leaves timeouts to the library.
            using HttpClient httpClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            // A local model can take longer than the library's default timeout
            // to finish a reply, so wait up to 10 minutes for each response.
            OpenAIClientOptions options = new()
            {
                Endpoint = new Uri(baseUrl),
                NetworkTimeout = TimeSpan.FromMinutes(10),
                Transport = new HttpClientPipelineTransport(httpClient),
            };
            ApiKeyCredential credential = new(accessKey);
            Console.WriteLine($"Server: {baseUrl}");
            Console.WriteLine();

            ModelInfo model = ListModels(new OpenAIModelClient(credential, options), requestedModel);
            ChatClient chat = new(model.Id, credential, options);
            CompleteChat(chat, model);
            StreamChat(chat, model);
            if (model.SupportsTools)
            {
                CallTool(chat, model);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"Tool calling: skipped, because {model.Id} does not support tool calls.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            // An error from the server includes the HTTP status and the
            // server's message.
            Console.Error.WriteLine($"Server client failed: {exception.Message}");
            return 1;
        }
    }

    // The model list follows the OpenAI shape. The winml field on each model
    // is specific to this server and describes the limits requests are
    // checked against.
    private static ModelInfo ListModels(OpenAIModelClient client, string? requestedId)
    {
        ClientResult result = client.GetModels(new RequestOptions());
        using JsonDocument document = JsonDocument.Parse(result.GetRawResponse().Content);
        Console.WriteLine("Models");
        ModelInfo? selected = null;
        foreach (JsonElement model in document.RootElement.GetProperty("data").EnumerateArray())
        {
            string id = model.GetProperty("id").GetString() ?? string.Empty;
            JsonElement details = model.TryGetProperty("winml", out JsonElement winml) ? winml : default;
            int outputLimit = (int)ReadNumber(details, "maximum_output_tokens");
            bool supportsTools = (ReadNumber(details, "capability_flags") & ToolCallsCapability) != 0;
            Console.WriteLine(
                $"  {id}: context window {ReadNumber(details, "context_window_tokens")} tokens, " +
                $"output limit {outputLimit} tokens, " +
                $"tool calls {(supportsTools ? "supported" : "not supported")}");

            if ((selected is null && requestedId is null) || id == requestedId)
            {
                selected = new ModelInfo(id, outputLimit, supportsTools);
            }
        }

        if (selected is null)
        {
            throw new InvalidOperationException($"The server has no model named '{requestedId}'.");
        }

        // A model that does not report its limit keeps the sample's own.
        return selected.OutputLimit is > 0 and <= RequestedOutputTokens
            ? selected
            : selected with { OutputLimit = RequestedOutputTokens };
    }

    private static long ReadNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static string Text(ChatMessageContent content) =>
        string.Concat(content.Select(part => part.Text));

    // InputTokenCount counts the whole formatted prompt. When the server can
    // reuse the start of a prompt it already processed, CachedTokenCount
    // reports how much of it was reused.
    private static void PrintUsage(ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        string reused = usage.InputTokenDetails is null
            ? string.Empty
            : $" ({usage.InputTokenDetails.CachedTokenCount} reused)";
        Console.WriteLine(
            $"  Usage: {usage.InputTokenCount} prompt tokens{reused}, " +
            $"{usage.OutputTokenCount} completion tokens");
    }

    private static void CompleteChat(ChatClient chat, ModelInfo model)
    {
        const string prompt = "Name three primary colors.";
        Console.WriteLine();
        Console.WriteLine("Chat completion");
        Console.WriteLine($"  User: {prompt}");

        List<ChatMessage> messages =
        [
            new SystemChatMessage("You are a helpful assistant. Answer briefly."),
            new UserChatMessage(prompt),
        ];
        ChatCompletion completion = chat.CompleteChat(
            messages, new ChatCompletionOptions { MaxOutputTokenCount = model.OutputLimit });
        Console.WriteLine($"  Assistant: {Text(completion.Content)}");
        PrintUsage(completion.Usage);
    }

    // A streamed reply arrives as updates, each with text to append. The
    // library asks the server to include usage, which arrives last.
    private static void StreamChat(ChatClient chat, ModelInfo model)
    {
        const string prompt = "Count from one to five in words.";
        Console.WriteLine();
        Console.WriteLine("Streaming");
        Console.WriteLine($"  User: {prompt}");
        Console.Write("  Assistant: ");

        List<ChatMessage> messages = [new UserChatMessage(prompt)];
        ChatTokenUsage? usage = null;
        foreach (StreamingChatCompletionUpdate update in chat.CompleteChatStreaming(
                     messages, new ChatCompletionOptions { MaxOutputTokenCount = model.OutputLimit }))
        {
            foreach (ChatMessageContentPart part in update.ContentUpdate)
            {
                Console.Write(part.Text);
            }

            usage = update.Usage ?? usage;
        }

        Console.WriteLine();
        PrintUsage(usage);
    }

    // The model writes the arguments as JSON text, which the client parses
    // before it runs the function.
    private static string RunTool(ChatToolCall call)
    {
        if (call.FunctionName != "multiply")
        {
            return "error: unknown tool";
        }

        try
        {
            using JsonDocument arguments = JsonDocument.Parse(call.FunctionArguments.ToMemory());
            double product = arguments.RootElement.GetProperty("a").GetDouble() *
                             arguments.RootElement.GetProperty("b").GetDouble();
            return product.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                          InvalidOperationException or FormatException)
        {
            return "error: invalid arguments";
        }
    }

    // A tool call takes two requests. The first offers the tool and requires
    // the model to call it. The client runs the call and adds the model's
    // request and the result to the conversation. The second request returns
    // the answer. An agent sends ChatToolChoice.CreateAutoChoice() instead and
    // repeats until the model answers without calling a tool; this sample
    // takes one call and then asks for the answer, so it always finishes in
    // two requests.
    private static void CallTool(ChatClient chat, ModelInfo model)
    {
        const string prompt = "What is 12 times 34? Use the multiply tool.";
        Console.WriteLine();
        Console.WriteLine("Tool calling");
        Console.WriteLine($"  User: {prompt}");

        List<ChatMessage> messages = [new UserChatMessage(prompt)];
        ChatCompletionOptions options = new()
        {
            MaxOutputTokenCount = model.OutputLimit,
            Tools = { MultiplyTool },
            ToolChoice = ChatToolChoice.CreateRequiredChoice(),
        };
        ChatCompletion first = chat.CompleteChat(messages, options);
        messages.Add(new AssistantChatMessage(first));
        foreach (ChatToolCall call in first.ToolCalls)
        {
            string result = RunTool(call);
            Console.WriteLine($"  Tool call: {call.FunctionName}({call.FunctionArguments}) returned {result}");
            messages.Add(new ToolChatMessage(call.Id, result));
        }

        options.ToolChoice = ChatToolChoice.CreateNoneChoice();
        ChatCompletion second = chat.CompleteChat(messages, options);
        Console.WriteLine($"  Assistant: {Text(second.Content)}");
        PrintUsage(second.Usage);
    }
}
