// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

internal static class DemoModel
{
    internal static float Run(string modelPath, float input)
    {
        using var options = new SessionOptions();
        using var session = new InferenceSession(modelPath, options);
        ValidateTensor(session.InputMetadata, "x");
        ValidateTensor(session.OutputMetadata, "y");

        var tensor = new DenseTensor<float>(new[] { input }, new[] { 1 });
        using var results = session.Run(
            new[] { NamedOnnxValue.CreateFromTensor("x", tensor) });
        var output = results.Single(result => result.Name == "y").AsTensor<float>();
        if (output.Rank != 1 || output.Dimensions[0] != 1)
        {
            throw new InvalidDataException("Expected model output y to be float[1].");
        }
        return output[0];
    }

    private static void ValidateTensor(
        IReadOnlyDictionary<string, NodeMetadata> metadata, string name)
    {
        if (metadata.Count != 1 ||
            !metadata.TryGetValue(name, out var node) ||
            !node.IsTensor ||
            node.ElementType != typeof(float) ||
            node.Dimensions.Length != 1 ||
            node.Dimensions[0] != 1)
        {
            throw new InvalidDataException($"Expected model tensor {name} to be float[1].");
        }
    }
}
