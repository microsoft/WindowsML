// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net.Http;
using System.Text.Json;

internal static class DemoCatalog
{
    private const string ModelName = "optional-msix-demo";
    private const int MaximumCatalogBytes = 64 * 1024;

    internal sealed record PackageDownload(Uri Uri, string Sha256);

    internal static async Task<PackageDownload> ReadPackageAsync(
        Uri catalogUri,
        string expectedPackageFamilyName)
    {
        string json;
        if (catalogUri.IsFile)
        {
            var file = new FileInfo(catalogUri.LocalPath);
            if (file.Length > MaximumCatalogBytes)
            {
                throw new InvalidDataException("The catalog exceeds 64 KiB.");
            }
            json = await File.ReadAllTextAsync(file.FullName);
        }
        else
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(1),
                MaxResponseContentBufferSize = MaximumCatalogBytes
            };
            json = await client.GetStringAsync(catalogUri);
        }

        return Validate(json, expectedPackageFamilyName);
    }

    private static PackageDownload Validate(string json, string expectedPackageFamilyName)
    {
        using var document = JsonDocument.Parse(json);
        var models = document.RootElement.GetProperty("models");
        if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() != 1)
        {
            throw new InvalidDataException("The demo catalog must contain exactly one model.");
        }

        var model = models[0];
        if (model.GetProperty("id").GetString() != ModelName ||
            model.GetProperty("name").GetString() != ModelName ||
            model.GetProperty("version").GetString() != "1.0.0.0" ||
            model.GetProperty("publisher").GetString() != "CN=OptionalMsixMinimal" ||
            model.TryGetProperty("files", out _))
        {
            throw new InvalidDataException("Use the package-backed optional-msix-demo catalog.");
        }

        var providers = model.GetProperty("executionProviders");
        if (providers.ValueKind != JsonValueKind.Array ||
            !providers.EnumerateArray().Any(provider =>
                string.Equals(provider.GetProperty("name").GetString(),
                    "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The demo model must support CPUExecutionProvider.");
        }

        var packages = model.GetProperty("packages");
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != 1)
        {
            throw new InvalidDataException("The demo catalog must specify exactly one MSIX.");
        }
        var package = packages[0];
        if (package.GetProperty("packageFamilyName").GetString() != expectedPackageFamilyName)
        {
            throw new InvalidDataException("The catalog references a different package family.");
        }
        if (!Uri.TryCreate(package.GetProperty("uri").GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException("The optional MSIX must have an absolute HTTPS URL.");
        }
        var hash = package.GetProperty("sha256").GetString();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Supply the SHA-256 of the signed Optional.msix.");
        }
        return new PackageDownload(uri, hash);
    }
}
