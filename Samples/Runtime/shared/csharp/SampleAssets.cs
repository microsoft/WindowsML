// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Finds sample assets that check_artifacts.ps1 places under a catalog's
// models directory.

namespace WinMLSamples;

internal static class SampleAssets
{
    // Returns models\<relativePath> from the first directory above the
    // executable or the current directory that has it, or the relative path
    // itself when none does.
    public static string FindModel(string relativePath)
    {
        string modelPath = Path.Combine("models", relativePath);
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, modelPath);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return modelPath;
    }
}
