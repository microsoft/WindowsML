// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Windows.Management.Deployment;

internal static class OptionalPackage
{
    private const long MaximumPackageBytes = 64 * 1024 * 1024;

    internal static async Task InstallAsync(DemoCatalog.PackageDownload package)
    {
        var directory = Directory.CreateTempSubdirectory("OptionalMsixDownload-");
        var path = Path.Combine(directory.FullName, "Optional.msix");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            await DownloadAsync(client, package.Uri, path, timeout.Token);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, timeout.Token));
            if (!string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded MSIX SHA-256 does not match the catalog.");
            }
            // Keep the hash-checked file read-locked; Windows validates the package during deployment.
            await new PackageManager().AddPackageAsync(new Uri(path), null, DeploymentOptions.None);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static async Task DownloadAsync(
        HttpClient client, Uri uri, string path, CancellationToken cancellationToken)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            {
                throw new InvalidDataException("The optional MSIX and redirects must use HTTPS without credentials.");
            }
            using var response = await client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= 5 || response.Headers.Location is not { } location)
                {
                    throw new HttpRequestException("The MSIX download has too many redirects or no redirect location.");
                }
                uri = new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumPackageBytes)
            {
                throw new InvalidDataException("The optional MSIX exceeds 64 MiB.");
            }
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
            {
                total += count;
                if (total > MaximumPackageBytes)
                {
                    throw new InvalidDataException("The optional MSIX exceeds 64 MiB.");
                }
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            return;
        }
    }

}
