// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text.Json;

namespace TaskStudio.Runtime;

internal sealed record GenerationDefaults(GenerationSettings Settings, uint[] StopTokenIds, string Source)
{
    public static GenerationDefaults TaskDefaults { get; } =
        new(new GenerationSettings(), [], "Task API defaults / 256-token app limit");

    public static GenerationDefaults Load(string modelDirectory)
    {
        // A GGUF file has no generation_config.json; a neighbouring one may belong to another model.
        if (TaskRuntime.IsGgufFile(modelDirectory)) return TaskDefaults with { Source = "GGUF / Task API defaults / 256-token app limit" };
        string path = Path.Combine(modelDirectory, "generation_config.json");
        FileStream input;
        try { input = File.OpenRead(path); }
        catch (FileNotFoundException) { return TaskDefaults; }
        using (input)
        {
            try
            {
                using var document = JsonDocument.Parse(input);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Expected a JSON object.");

                bool Has(string key, out JsonElement value) =>
                    root.TryGetProperty(key, out value) && value.ValueKind != JsonValueKind.Null;

                float Float(string key, float fallback)
                {
                    if (!Has(key, out var value)) return fallback;
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float number) || !float.IsFinite(number))
                        throw new InvalidDataException($"'{key}' must be a finite number.");
                    return number;
                }

                uint UInt(string key, uint fallback)
                {
                    if (!Has(key, out var value)) return fallback;
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint number))
                        throw new InvalidDataException($"'{key}' must be an unsigned 32-bit integer.");
                    return number;
                }

                var fallback = TaskDefaults.Settings;
                float temperature = Float("temperature", fallback.Temperature);
                bool greedy = false;
                if (Has("do_sample", out var sampling))
                {
                    if (sampling.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("'do_sample' must be true or false.");
                    greedy = !sampling.GetBoolean();
                    temperature = greedy ? 0 : Float("temperature", 1);
                }
                ulong seed = fallback.Seed;
                if (Has("seed", out var seedValue) &&
                    (seedValue.ValueKind != JsonValueKind.Number || !seedValue.TryGetUInt64(out seed)))
                    throw new InvalidDataException("'seed' must be an unsigned 64-bit integer.");

                var settings = new GenerationSettings(
                    UInt("max_new_tokens", fallback.MaxTokens), temperature,
                    UInt("top_k", fallback.TopK), Float("top_p", fallback.TopP), Float("min_p", fallback.MinP),
                    Float("repetition_penalty", fallback.RepetitionPenalty),
                    UInt("repetition_penalty_window", fallback.RepetitionWindow),
                    Float("presence_penalty", fallback.PresencePenalty),
                    Float("frequency_penalty", fallback.FrequencyPenalty), seed);
                settings.Validate();

                var stops = new List<uint>();
                foreach (string key in new[] { "eos_token_id", "stop_token_ids" })
                {
                    if (!Has(key, out var value)) continue;
                    void Add(JsonElement token)
                    {
                        if (token.ValueKind != JsonValueKind.Number || !token.TryGetUInt32(out uint id))
                            throw new InvalidDataException($"'{key}' must contain unsigned 32-bit token IDs.");
                        stops.Add(id);
                    }
                    if (value.ValueKind == JsonValueKind.Array)
                        foreach (var token in value.EnumerateArray()) Add(token);
                    else if (key == "eos_token_id") Add(value);
                    else throw new InvalidDataException("'stop_token_ids' must be an array.");
                }
                return new(settings, stops.Distinct().ToArray(),
                    "Model generation_config.json + Task defaults" + (greedy ? " / do_sample=false: greedy" : ""));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            {
                throw new InvalidDataException($"Cannot load generation defaults from '{path}': {error.Message}", error);
            }
        }
    }
}
