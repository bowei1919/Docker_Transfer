using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DockerTransfer;

internal static class BackupRunner
{
    public static async Task<string> CreateAsync(JsonObject manifest, string outputPath)
    {
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? Directory.GetCurrentDirectory());
        var staging = Path.Combine(Path.GetTempPath(), "docker-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.Combine(staging, "images"));
        Directory.CreateDirectory(Path.Combine(staging, "volumes"));
        Directory.CreateDirectory(Path.Combine(staging, "binds"));
        var files = new JsonArray();

        try
        {
            foreach (var imageNode in manifest["images"]?.AsArray() ?? [])
            {
                var image = imageNode?.AsObject() ?? new JsonObject();
                if (image["missing"]?.GetValue<bool>() == true) continue;
                var reference = FirstString(image["repo_tags"]?.AsArray()) ?? StringValue(image["id"]);
                if (string.IsNullOrWhiteSpace(reference)) continue;
                var filename = SafeName(reference) + ".tar";
                var relative = Path.Combine("images", filename);
                var fullPath = Path.Combine(staging, relative);
                await RunDockerAsync(["save", "-o", fullPath, reference]);
                await AddFileAsync(files, relative, "image", reference, fullPath);
            }

            foreach (var volumeNode in manifest["volumes"]?.AsArray() ?? [])
            {
                var volume = volumeNode?.AsObject() ?? new JsonObject();
                if (volume["missing"]?.GetValue<bool>() == true) continue;
                var name = StringValue(volume["name"]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                var filename = SafeName(name) + ".tar.gz";
                var relative = Path.Combine("volumes", filename);
                var fullPath = Path.Combine(staging, relative);
                await RunDockerAsync([
                    "run", "--rm", "--user", "0",
                    "--mount", $"type=volume,source={name},target=/source,readonly",
                    "--mount", $"type=bind,source={staging},target=/backup",
                    "alpine", "tar", "czf", "/backup/" + relative.Replace('\\', '/'), "-C", "/source", "."
                ]);
                await AddFileAsync(files, relative, "volume", name, fullPath);
            }

            foreach (var containerNode in manifest["containers"]?.AsArray() ?? [])
            {
                var container = containerNode?.AsObject() ?? new JsonObject();
                var containerName = StringValue(container["name"]) ?? "container";
                foreach (var mountNode in container["mounts"]?.AsArray() ?? [])
                {
                    var mount = mountNode?.AsObject() ?? new JsonObject();
                    if (StringValue(mount["kind"]) != "bind_mount") continue;
                    var source = StringValue(mount["source"]);
                    if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) continue;
                    var filename = SafeName(containerName + "-" + (StringValue(mount["destination"]) ?? "mount")) + ".tar.gz";
                    var relative = Path.Combine("binds", filename);
                    var fullPath = Path.Combine(staging, relative);
                    await RunDockerAsync([
                        "run", "--rm", "--user", "0",
                        "--mount", $"type=bind,source={source},target=/source,readonly",
                        "--mount", $"type=bind,source={staging},target=/backup",
                        "alpine", "tar", "czf", "/backup/" + relative.Replace('\\', '/'), "-C", "/source", "."
                    ]);
                    await AddFileAsync(files, relative, "bind_mount", source, fullPath);
                }
            }

            manifest["backup_files"] = files;
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            if (File.Exists(destination)) File.Delete(destination);
            ZipFile.CreateFromDirectory(staging, destination, CompressionLevel.Fastest, includeBaseDirectory: false);
            return destination;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* preserve the primary backup error */ }
        }
    }

    private static async Task AddFileAsync(JsonArray files, string relative, string kind, string source, string fullPath)
    {
        var hash = await HashAsync(fullPath);
        files.Add(new JsonObject
        {
            ["path"] = relative.Replace('\\', '/'),
            ["kind"] = kind,
            ["source"] = source,
            ["bytes"] = new FileInfo(fullPath).Length,
            ["sha256"] = hash,
        });
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task RunDockerAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new DockerException("Unable to start Docker CLI for backup");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new DockerException($"docker {string.Join(' ', arguments)} failed: {error.Trim()}");
        _ = await stdout;
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray();
        var result = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "item" : result;
    }

    private static string? FirstString(JsonArray? values)
    {
        return values?.Select(StringValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? StringValue(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<string>(); }
        catch (InvalidOperationException) { return node.ToJsonString().Trim('"'); }
    }
}
