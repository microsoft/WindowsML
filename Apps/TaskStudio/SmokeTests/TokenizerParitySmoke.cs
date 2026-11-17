// Copyright (C) Microsoft Corporation. All rights reserved.

using TaskStudio.Runtime;
using WinMLSamples.Runtime;

// Compares the tokenizer a GGUF file carries with its publisher's checkpoint tokenizer: plain encoding (with and
// without special tokens, including whitespace around added tokens) and the formatted chat conversation.
internal static class TokenizerParitySmoke
{
    private static readonly string[] Texts =
    [
        "Hello world! The quick brown fox jumps over the lazy dog.",
        "  leading spaces, trailing spaces   ",
        "def area(width, height):\n    return width * height\n",
        "Numbers 1234567 and 3.14159, emoji \U0001F642, accents caf\u00E9 na\u00EFve, CJK \u4E2D\u6587, Devanagari \u0915\u093F\u0924\u093E\u092C.",
        "Before a special token <|im_start|>user\nHi <|im_end|> and <start_of_turn> <end_of_turn> after.",
        "line one\n\n\nline two\ttab",
    ];

    public static int Run(string ggufPath, string folderTokenizer)
    {
        int failures = 0;
        using Tokenizer gguf = Tokenizer.CreateFromFile(ggufPath);
        Console.WriteLine($"PASS: tokenizer created from {Path.GetFileName(ggufPath)} (no tokenizer_config.json).");
        using Tokenizer folder = Tokenizer.CreateFromFile(folderTokenizer);
        foreach (var flags in new[] { TokenizerEncodeFlags.None, TokenizerEncodeFlags.AddSpecialTokens })
        {
            foreach (string text in Texts)
            {
                uint[] a = gguf.Encode(text, flags), b = folder.Encode(text, flags);
                if (a.SequenceEqual(b)) continue;
                failures++;
                int at = 0;
                while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
                Console.WriteLine($"FAIL: encode ({EnumNames.Native("WINML_TOKENIZER_ENCODE_FLAG_", flags)}) differs at token {at} for {Quote(text)}: gguf [{Slice(a, at)}] vs folder [{Slice(b, at)}]");
            }
        }
        if (failures == 0) Console.WriteLine($"PASS: encode matches the folder tokenizer on {Texts.Length} texts, with and without special tokens.");

        foreach (var thinking in new[] { ConversationEnableThinking.ModelDefault, ConversationEnableThinking.Disabled })
        {
            foreach (bool system in new[] { false, true })
            {
                (string promptA, uint[] idsA) = Format(gguf, thinking, system);
                (string promptB, uint[] idsB) = Format(folder, thinking, system);
                string label = $"{EnumNames.Words(thinking).Replace(' ', '_')} thinking, {(system ? "with" : "no")} system";
                if (promptA == promptB && idsA.SequenceEqual(idsB))
                {
                    Console.WriteLine($"PASS: chat format matches ({label}): {idsA.Length} tokens.");
                    continue;
                }
                failures++;
                int at = 0;
                while (at < promptA.Length && at < promptB.Length && promptA[at] == promptB[at]) at++;
                Console.WriteLine($"FAIL: chat format differs ({label}): text {(promptA == promptB ? "same" : $"differs at char {at}: gguf {Quote(promptA[at..Math.Min(promptA.Length, at + 60)])} vs folder {Quote(promptB[at..Math.Min(promptB.Length, at + 60)])}")}" +
                    $"; ids {idsA.Length} vs {idsB.Length}{(idsA.SequenceEqual(idsB) ? " (same)" : "")}");
            }
        }
        Console.WriteLine(failures == 0 ? "PASS: GGUF tokenizer parity." : $"FAIL: {failures} parity difference(s).");
        return failures == 0 ? 0 : 1;
    }

    public static int PrintIds(string tokenizerPath, string text)
    {
        using Tokenizer tokenizer = Tokenizer.CreateFromFile(tokenizerPath);
        Console.WriteLine(string.Join(",", tokenizer.Encode(text.Replace("\\n", "\n"))));
        return 0;
    }

    private static (string Prompt, uint[] Ids) Format(Tokenizer tokenizer, ConversationEnableThinking thinking, bool system)
    {
        using StructuredConversationFormatter formatter = tokenizer.As<StructuredConversationFormatter>();
        var messages = new List<ConversationMessage>();
        if (system) messages.Add(new() { Role = "system", Content = "You are a helpful assistant." });
        messages.Add(new() { Role = "user", Content = "Give me a banana bread recipe please" });
        messages.Add(new() { Role = "assistant", Content = "Here is a banana bread recipe." });
        messages.Add(new() { Role = "user", Content = "Can I use less sugar?" });
        using ConversationFormatResult result = formatter.FormatConversation(new ConversationRequest
        {
            Messages = messages, AddGenerationPrompt = true, EnableThinking = thinking
        });
        return (result.GenerationPrompt, result.GetTokenIds());
    }

    private static string Quote(string text) => "\"" + text.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    private static string Slice(uint[] ids, int at) => string.Join(",", ids.Skip(Math.Max(0, at - 2)).Take(6));
}
