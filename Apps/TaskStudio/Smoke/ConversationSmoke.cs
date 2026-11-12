// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

internal static class ConversationSmoke
{
    public static unsafe int Run(string tokenizerPath)
    {
        IWinMLTokenizer* tokenizer = null;
        IWinMLStructuredConversationFormatter* formatter = null;
        nint role = Marshal.StringToCoTaskMemUni("user");
        nint content = Marshal.StringToCoTaskMemUni("Give me a banana bread recipe please");
        nint assistantRole = Marshal.StringToCoTaskMemUni("assistant");
        nint assistantContent = Marshal.StringToCoTaskMemUni("Here is a banana bread recipe.");
        nint followUp = Marshal.StringToCoTaskMemUni("Can I use less sugar?");
        try
        {
            PInvoke.WinMLCreateTokenizerFromFile(tokenizerPath, &tokenizer).ThrowOnFailure();
            tokenizer->QueryInterface(out formatter).ThrowOnFailure();
            var messages = new WINML_CONVERSATION_MESSAGE[]
            {
                new() { role = new PCWSTR((char*)role), content = new PCWSTR((char*)content) },
                new() { role = new PCWSTR((char*)assistantRole), content = new PCWSTR((char*)assistantContent) },
                new() { role = new PCWSTR((char*)role), content = new PCWSTR((char*)followUp) }
            };
            foreach (uint messageCount in new uint[] { 1, 3 })
            {
                IWinMLConversationFormatResult* result = null;
                IWinMLConversationOutputParser* parser = null;
                uint* tokens = null;
                try
                {
                    fixed (WINML_CONVERSATION_MESSAGE* messagePointer = messages)
                    {
                        var request = new WINML_CONVERSATION_REQUEST
                        {
                            messageCount = messageCount, messages = messagePointer, addGenerationPrompt = true,
                            enableThinking = WINML_CONVERSATION_ENABLE_THINKING.WINML_CONVERSATION_ENABLE_THINKING_DISABLED
                        };
                        result = formatter->FormatConversation(&request);
                    }
                    uint count;
                    result->GetTokenIds(&count, &tokens);
                    if (count == 0 || tokens == null) throw new InvalidOperationException("The formatted conversation contained no tokens.");
                    PCWSTR prompt;
                    result->GetGenerationPrompt(&prompt);
                    if (string.IsNullOrWhiteSpace(prompt.ToString())) throw new InvalidOperationException("No generation prompt was produced.");
                    parser = result->CreateOutputParser();
                    Console.WriteLine($"PASS: default formatter formatted {messageCount} messages into {count} tokens with an output parser.");
                }
                finally
                {
                    Marshal.FreeCoTaskMem((nint)tokens);
                    if (parser != null) parser->Release();
                    if (result != null) result->Release();
                }
            }
            return 0;
        }
        finally
        {
            if (formatter != null) formatter->Release();
            if (tokenizer != null) tokenizer->Release();
            Marshal.FreeCoTaskMem(role);
            Marshal.FreeCoTaskMem(content);
            Marshal.FreeCoTaskMem(assistantRole);
            Marshal.FreeCoTaskMem(assistantContent);
            Marshal.FreeCoTaskMem(followUp);
        }
    }
}
