// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Runtime.InteropServices;
using Microsoft.Windows.AI.MachineLearning.Runtime;
using Windows.Win32.Foundation;

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

    public static unsafe int Run(string ggufPath, string folderTokenizer)
    {
        IWinMLTokenizer* gguf = null;
        IWinMLTokenizer* folder = null;
        int failures = 0;
        try
        {
            PInvoke.WinMLCreateTokenizerFromFile(ggufPath, &gguf).ThrowOnFailure();
            Console.WriteLine($"PASS: tokenizer created from {Path.GetFileName(ggufPath)} (no tokenizer_config.json).");
            PInvoke.WinMLCreateTokenizerFromFile(folderTokenizer, &folder).ThrowOnFailure();
            foreach (var flags in new[] { WINML_TOKENIZER_ENCODE_FLAGS.WINML_TOKENIZER_ENCODE_FLAG_NONE, WINML_TOKENIZER_ENCODE_FLAGS.WINML_TOKENIZER_ENCODE_FLAG_ADD_SPECIAL_TOKENS })
            {
                foreach (string text in Texts)
                {
                    uint[] a = Encode(gguf, text, flags), b = Encode(folder, text, flags);
                    if (a.SequenceEqual(b)) continue;
                    failures++;
                    int at = 0;
                    while (at < a.Length && at < b.Length && a[at] == b[at]) at++;
                    Console.WriteLine($"FAIL: encode ({flags}) differs at token {at} for {Quote(text)}: gguf [{Slice(a, at)}] vs folder [{Slice(b, at)}]");
                }
            }
            if (failures == 0) Console.WriteLine($"PASS: encode matches the folder tokenizer on {Texts.Length} texts, with and without special tokens.");

            foreach (var thinking in new[] { WINML_CONVERSATION_ENABLE_THINKING.WINML_CONVERSATION_ENABLE_THINKING_MODEL_DEFAULT,
                WINML_CONVERSATION_ENABLE_THINKING.WINML_CONVERSATION_ENABLE_THINKING_DISABLED })
            {
                foreach (bool system in new[] { false, true })
                {
                    (string promptA, uint[] idsA) = Format(gguf, thinking, system);
                    (string promptB, uint[] idsB) = Format(folder, thinking, system);
                    string label = $"{thinking.ToString().Replace("WINML_CONVERSATION_ENABLE_THINKING_", "").ToLowerInvariant()} thinking, {(system ? "with" : "no")} system";
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
        finally
        {
            if (gguf != null) gguf->Release();
            if (folder != null) folder->Release();
        }
    }

    public static unsafe int PrintIds(string tokenizerPath, string text)
    {
        IWinMLTokenizer* tokenizer = null;
        try
        {
            PInvoke.WinMLCreateTokenizerFromFile(tokenizerPath, &tokenizer).ThrowOnFailure();
            Console.WriteLine(string.Join(",", Encode(tokenizer, text.Replace("\\n", "\n"), WINML_TOKENIZER_ENCODE_FLAGS.WINML_TOKENIZER_ENCODE_FLAG_NONE)));
            return 0;
        }
        finally { if (tokenizer != null) tokenizer->Release(); }
    }

    private static unsafe uint[] Encode(IWinMLTokenizer* tokenizer, string text, WINML_TOKENIZER_ENCODE_FLAGS flags)
    {
        uint* ids = null;
        try
        {
            tokenizer->Encode(text, flags, out uint count, &ids);
            return new ReadOnlySpan<uint>(ids, (int)count).ToArray();
        }
        finally { Marshal.FreeCoTaskMem((nint)ids); }
    }

    private static unsafe (string Prompt, uint[] Ids) Format(IWinMLTokenizer* tokenizer, WINML_CONVERSATION_ENABLE_THINKING thinking, bool system)
    {
        IWinMLStructuredConversationFormatter* formatter = null;
        IWinMLConversationFormatResult* result = null;
        uint* tokens = null;
        var strings = new List<nint>();
        nint S(string value) { nint p = Marshal.StringToCoTaskMemUni(value); strings.Add(p); return p; }
        try
        {
            tokenizer->QueryInterface(out formatter).ThrowOnFailure();
            var list = new List<WINML_CONVERSATION_MESSAGE>();
            if (system) list.Add(new() { role = new PCWSTR((char*)S("system")), content = new PCWSTR((char*)S("You are a helpful assistant.")) });
            list.Add(new() { role = new PCWSTR((char*)S("user")), content = new PCWSTR((char*)S("Give me a banana bread recipe please")) });
            list.Add(new() { role = new PCWSTR((char*)S("assistant")), content = new PCWSTR((char*)S("Here is a banana bread recipe.")) });
            list.Add(new() { role = new PCWSTR((char*)S("user")), content = new PCWSTR((char*)S("Can I use less sugar?")) });
            var messages = list.ToArray();
            fixed (WINML_CONVERSATION_MESSAGE* pointer = messages)
            {
                var request = new WINML_CONVERSATION_REQUEST
                {
                    messageCount = (uint)messages.Length, messages = pointer, addGenerationPrompt = true, enableThinking = thinking
                };
                result = formatter->FormatConversation(&request);
            }
            result->GetTokenIds(out uint count, &tokens);
            PCWSTR prompt;
            result->GetGenerationPrompt(&prompt);
            return (prompt.ToString(), new ReadOnlySpan<uint>(tokens, (int)count).ToArray());
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)tokens);
            if (result != null) result->Release();
            if (formatter != null) formatter->Release();
            foreach (nint p in strings) Marshal.FreeCoTaskMem(p);
        }
    }

    private static string Quote(string text) => "\"" + text.Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    private static string Slice(uint[] ids, int at) => string.Join(",", ids.Skip(Math.Max(0, at - 2)).Take(6));
}
