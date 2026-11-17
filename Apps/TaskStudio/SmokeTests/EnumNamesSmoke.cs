// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Windows.AI.MachineLearning.Runtime;
using Microsoft.Windows.AI.MachineLearning.Runtime.Tasks;
using TaskStudio.Runtime;
using WinMLSamples.Runtime;
using WinMLSamples.Runtime.Tasks;

// The app shows and exports Runtime enumerations by their native names, which it builds from the shared wrapper's
// enum members. This compares every value of each native enum, as CsWin32 generates it from the package, with the
// string the app builds for the same value, formatted the way the app did when it called the native enums itself.
// A wrapper member that is renamed, missing, or out of step with the Runtime fails here, before it changes the UI or
// the CSV export.
internal static class EnumNamesSmoke
{
    public static int Run()
    {
        int failures = 0;
        void Check<TNative, TWrapper>(string what, Func<TNative, string> expected, Func<TWrapper, string> actual)
            where TNative : struct, Enum where TWrapper : struct, Enum
        {
            foreach (TNative native in Enum.GetValues<TNative>())
            {
                var wrapper = (TWrapper)Enum.ToObject(typeof(TWrapper), Convert.ToInt64(native));
                if (!Enum.IsDefined(wrapper)) { Console.WriteLine($"FAIL: {what}: the wrapper has no member for {native}."); failures++; }
                else if (actual(wrapper) != expected(native)) { Console.WriteLine($"FAIL: {what}: {wrapper} is '{actual(wrapper)}', expected '{expected(native)}'."); failures++; }
            }
            foreach (TWrapper wrapper in Enum.GetValues<TWrapper>())
            {
                if (!Enum.IsDefined(typeof(TNative), Enum.ToObject(typeof(TNative), Convert.ToInt64(wrapper))))
                {
                    Console.WriteLine($"FAIL: {what}: the wrapper member {wrapper} has no native value.");
                    failures++;
                }
            }
        }
        static string After(string name, string marker) => name[(name.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        static string Words(string name, string marker) => After(name, marker).Replace('_', ' ').ToLowerInvariant();

        Check<WINML_TEXT_GENERATION_FINISH_REASON, TextGenerationFinishReason>("text finish reason",
            n => n.ToString(), w => EnumNames.Native("WINML_TEXT_GENERATION_FINISH_REASON_", w));
        Check<WINML_CHAT_COMPLETION_FINISH_REASON, ChatCompletionFinishReason>("chat finish reason",
            n => n.ToString(), w => EnumNames.Native("WINML_CHAT_COMPLETION_FINISH_REASON_", w));
        Check<WINML_TEXT_GENERATION_FINISH_REASON, TextGenerationFinishReason>("benchmark finish reason",
            n => After(n.ToString(), "_REASON_"), EnumNames.Constant);
        Check<WINML_CHAT_COMPLETION_PROMPT_CACHE_MODE, ChatCompletionPromptCacheMode>("prompt cache mode",
            n => Words(n.ToString(), "_MODE_"), EnumNames.Words);
        Check<WINML_TEXT_GENERATION_SPECULATIVE_METHOD, TextGenerationSpeculativeMethod>("speculative method",
            n => Words(n.ToString(), "_METHOD_"), EnumNames.Words);
        Check<WINML_TEXT_GENERATION_DRAFT_SOURCE, TextGenerationDraftSource>("draft source",
            n => Words(n.ToString(), "_SOURCE_"), EnumNames.Words);
        Check<WINML_TOKENIZER_ENCODE_FLAGS, TokenizerEncodeFlags>("encode flag",
            n => n.ToString(), w => EnumNames.Native("WINML_TOKENIZER_ENCODE_FLAG_", w));
        Check<WINML_CONVERSATION_ENABLE_THINKING, ConversationEnableThinking>("thinking label",
            n => n.ToString().Replace("WINML_CONVERSATION_ENABLE_THINKING_", "").ToLowerInvariant(), w => EnumNames.Words(w).Replace(' ', '_'));
        Console.WriteLine(failures == 0 ? "PASS: Runtime enumeration names for the UI and the CSV export." : $"FAIL: {failures} enumeration name(s).");
        return failures == 0 ? 0 : 1;
    }
}
