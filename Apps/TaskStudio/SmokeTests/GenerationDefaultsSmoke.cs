// Copyright (C) Microsoft Corporation. All rights reserved.

using TaskStudio.Runtime;

internal static class GenerationDefaultsSmoke
{
    public static int Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"task-studio-defaults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "generation_config.json");
        try
        {
            var missing = GenerationDefaults.Load(directory);
            if (missing.Settings != new GenerationSettings() || missing.Settings.Temperature != 0 || missing.StopTokenIds.Length != 0)
                throw new InvalidOperationException("Missing config did not retain Task defaults.");

            File.WriteAllText(path, """
                {
                  "do_sample": true, "temperature": 0.7, "top_k": 20, "top_p": 0.8,
                  "min_p": 0.03, "repetition_penalty": 1.1, "repetition_penalty_window": 64,
                  "presence_penalty": 0.2, "frequency_penalty": -0.1, "max_new_tokens": 128,
                  "seed": 18446744073709551615, "eos_token_id": [151645, 151643],
                  "stop_token_ids": [151645, 42], "transformers_version": "test"
                }
                """);
            var model = GenerationDefaults.Load(directory);
            var expected = new GenerationSettings(128, 0.7f, 20, 0.8f, 0.03f, 1.1f, 64, 0.2f, -0.1f, ulong.MaxValue);
            if (model.Settings != expected || !model.StopTokenIds.SequenceEqual(new uint[] { 151645, 151643, 42 }))
                throw new InvalidOperationException("Model generation settings were not mapped exactly.");

            File.WriteAllText(path, """{"do_sample":false,"temperature":0.9,"top_p":0.8}""");
            var greedy = GenerationDefaults.Load(directory);
            if (greedy.Settings.Temperature != 0 || greedy.Settings.TopP != 0.8f || !greedy.Source.Contains("greedy"))
                throw new InvalidOperationException("do_sample=false did not select greedy decoding.");

            File.WriteAllText(path, """{"do_sample":true}""");
            if (GenerationDefaults.Load(directory).Settings.Temperature != 1)
                throw new InvalidOperationException("Sampling without a temperature should use 1.");
            File.WriteAllText(path, """{"temperature":3,"top_k":1000,"presence_penalty":-4,"eos_token_id":42}""");
            var extended = GenerationDefaults.Load(directory);
            if (extended.Settings.Temperature != 3 || extended.Settings.TopK != 1000 ||
                extended.Settings.PresencePenalty != -4 || !extended.StopTokenIds.SequenceEqual(new uint[] { 42 }))
                throw new InvalidOperationException("Legal values outside the initial slider range were not retained.");
            File.WriteAllText(path, """{"top_k":null}""");
            if (GenerationDefaults.Load(directory).Settings != new GenerationSettings())
                throw new InvalidOperationException("Unset fields did not retain Task defaults.");

            foreach (string invalid in new[]
            {
                "{", "[]", """{"top_p":0}""", """{"min_p":2}""",
                """{"top_k":-1}""", """{"temperature":"hot"}""", """{"temperature":1e100}""",
                """{"repetition_penalty":0}""", """{"do_sample":"true"}""",
                """{"seed":18446744073709551616}""", $"{{\"max_new_tokens\":{GenerationSettings.MaxNewTokensLimit + 1}}}",
                """{"eos_token_id":[-1]}""", """{"stop_token_ids":"stop"}"""
            })
            {
                File.WriteAllText(path, invalid);
                bool rejected = false;
                try { GenerationDefaults.Load(directory); }
                catch (InvalidDataException error)
                {
                    if (!error.Message.Contains(path)) throw;
                    rejected = true;
                }
                if (!rejected) throw new InvalidOperationException($"Invalid generation config was accepted: {invalid}");
            }
            Console.WriteLine("PASS: model defaults, Task fallback, sampling/greedy policy, EOS IDs, 64-bit seed, extended ranges and invalid config errors.");
            return 0;
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
