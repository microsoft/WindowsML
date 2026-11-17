// Copyright (C) Microsoft Corporation. All rights reserved.

using WinMLSamples.Runtime;

internal static class ConversationSmoke
{
    public static int Run(string tokenizerPath)
    {
        using Tokenizer tokenizer = Tokenizer.CreateFromFile(tokenizerPath);
        using StructuredConversationFormatter formatter = tokenizer.As<StructuredConversationFormatter>();
        var messages = new ConversationMessage[]
        {
            new() { Role = "user", Content = "Give me a banana bread recipe please" },
            new() { Role = "assistant", Content = "Here is a banana bread recipe." },
            new() { Role = "user", Content = "Can I use less sugar?" }
        };
        foreach (int messageCount in new[] { 1, 3 })
        {
            using ConversationFormatResult result = formatter.FormatConversation(new ConversationRequest
            {
                Messages = messages[..messageCount], AddGenerationPrompt = true, EnableThinking = ConversationEnableThinking.Disabled
            });
            uint[] tokens = result.GetTokenIds();
            if (tokens.Length == 0) throw new InvalidOperationException("The formatted conversation contained no tokens.");
            if (string.IsNullOrWhiteSpace(result.GenerationPrompt)) throw new InvalidOperationException("No generation prompt was produced.");
            using ConversationOutputParser parser = result.CreateOutputParser();
            Console.WriteLine($"PASS: default formatter formatted {messageCount} messages into {tokens.Length} tokens with an output parser.");
        }
        return 0;
    }
}
