#!/usr/bin/env dotnet

// Pre-seeds the ONNX embedding model into the per-machine cache the app reads from.
//
// The app downloads this on its own at startup (EmbeddingModelProvisioningService), so this
// script is a convenience: it warms the cache before the first run, and it is the supported
// way to stage the model for an air-gapped deployment (copy the resulting directory across
// and set Embeddings__ModelPath to point at it).
//
// The constants below mirror src/Infrastructure/Services/EmbeddingModelOptions.cs, which is
// the source of truth. Keep them in sync when bumping the pinned revision.

using System.Security.Cryptography;

const string Revision = "d4aa6901d3a41ba39fb536a557fa166f842b0e09";
const string BaseUrl = "https://huggingface.co/BAAI/bge-large-en-v1.5/resolve";
const string ModelDirectoryName = "BgeLargeEnV15";
const string MarkerFileName = ".provisioned";

(string FileName, string RemotePath, long Size, string Sha256)[] files =
[
    ("model.onnx", "onnx/model.onnx", 1_336_854_281L, "69ed3f810d3b6d13f70dff9ca89966f39c0a0e877fb88211be7bcc070df2a2ce"),
    ("vocab.txt", "vocab.txt", 231_508L, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
];

string modelDir = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0]
    : Environment.GetEnvironmentVariable("Embeddings__ModelPath") is { Length: > 0 } fromEnv
        ? fromEnv
        : ResolveDefaultDirectory();

Directory.CreateDirectory(modelDir);
Console.WriteLine($"Model directory: {modelDir}");

using HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

foreach ((string fileName, string remotePath, long size, string sha256) in files)
{
    string finalPath = Path.Combine(modelDir, fileName);

    FileInfo existing = new(finalPath);
    if (existing.Exists && existing.Length == size)
    {
        Console.WriteLine($"{fileName} already present and the expected size, skipping.");
        continue;
    }

    string tempPath = finalPath + ".tmp";
    Uri uri = new($"{BaseUrl}/{Revision}/{remotePath}");

    Console.WriteLine($"Downloading {fileName} ({size:N0} bytes) from {uri}...");

    try
    {
        string actualHash;
        long actualSize;

        using (HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
        {
            // Without this an HTTP error would be written into the file and reported as success.
            response.EnsureSuccessStatusCode();

            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using FileStream target = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];

            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read));
                total += read;
            }

            actualSize = total;
            actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        if (actualSize != size)
        {
            throw new InvalidOperationException($"{fileName} is {actualSize:N0} bytes, expected {size:N0}.");
        }

        if (!string.Equals(actualHash, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{fileName} has SHA-256 {actualHash}, expected {sha256}.");
        }

        File.Move(tempPath, finalPath, overwrite: true);
        Console.WriteLine($"{fileName} verified.");
    }
    catch
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        throw;
    }
}

// Matches what the app writes, so it will not re-verify on first start.
await File.WriteAllTextAsync(Path.Combine(modelDir, MarkerFileName), Revision);

Console.WriteLine($"ONNX model files ready at {modelDir}");
return 0;

static string ResolveDefaultDirectory()
{
    string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    if (string.IsNullOrWhiteSpace(root))
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        root = string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "Receipts")
            : Path.Combine(home, ".local", "share");
    }

    return Path.Combine(root, "Receipts", "models", ModelDirectoryName);
}
