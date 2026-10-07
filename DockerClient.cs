using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DockerTransfer;

internal sealed class DockerException : Exception
{
    public DockerException(string message) : base(message) { }
}

internal static class JsonNodeExtensions
{
    public static JsonNode DeepClone(this JsonNode node)
    {
        return JsonNode.Parse(node.ToJsonString()) ?? throw new InvalidOperationException("Unable to clone JSON node");
    }
}

internal sealed class DockerClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpointDescription;

    private DockerClient(HttpClient http, string endpointDescription)
    {
        _http = http;
        _endpointDescription = endpointDescription;
    }

    public static async Task<DockerClient> ConnectAsync(string? endpoint)
    {
        var candidates = Endpoint.ParseCandidates(endpoint);
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = candidate.ConnectAsync,
            };
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://docker.local") };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = new DockerClient(http, candidate.Description);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.GetJsonAsync("/version", timeout.Token);
                return client;
            }
            catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or DockerException or OperationCanceledException)
            {
                last = ex;
                await client.DisposeAsync();
            }
        }

        throw new DockerException($"Unable to connect to Docker daemon. Tried {string.Join(", ", candidates.Select(c => c.Description))}. {last?.Message}");
    }

    public Task<JsonNode> GetJsonAsync(string path) => GetJsonAsync(path, CancellationToken.None);

    public async Task<JsonNode> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new DockerException($"Docker API GET {path} returned {(int)response.StatusCode} {response.StatusCode}: {body.Trim()}");
        try
        {
            return JsonNode.Parse(body) ?? throw new DockerException($"Docker API returned empty JSON for {path}");
        }
        catch (JsonException ex)
        {
            throw new DockerException($"Docker API returned invalid JSON for {path}: {ex.Message}");
        }
    }

    public async Task<JsonObject> DiscoverAsync()
    {
        var version = await GetJsonAsync("/version");
        var listedContainers = (await GetJsonAsync("/containers/json?all=1")).AsArray();
        var containers = new JsonArray();
        var warnings = new JsonArray();
        var imageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var volumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var composeProjects = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var listed in listedContainers)
        {
            var id = StringValue(listed?["Id"]) ?? throw new DockerException("Docker returned a container without an Id");
            JsonObject inspect;
            try
            {
                inspect = (await GetJsonAsync($"/containers/{Uri.EscapeDataString(id)}/json")).AsObject();
            }
            catch (DockerException ex) when (IsNotFound(ex))
            {
                warnings.Add($"container {id} disappeared during discovery");
                continue;
            }
            var config = inspect["Config"]?.AsObject();
            var hostConfig = inspect["HostConfig"]?.AsObject();
            var labels = config?["Labels"]?.AsObject() ?? new JsonObject();
            var imageId = StringValue(inspect["Image"]);
            if (!string.IsNullOrWhiteSpace(imageId)) imageIds.Add(imageId);

            var mounts = new JsonArray();
            foreach (var mount in inspect["Mounts"]?.AsArray() ?? [])
            {
                var mountObject = mount?.AsObject() ?? new JsonObject();
                var type = StringValue(mountObject["Type"]) ?? "unknown";
                var name = StringValue(mountObject["Name"]);
                var kind = type switch
                {
                    "volume" when string.IsNullOrWhiteSpace(name) => "anonymous_volume",
                    "volume" => "named_volume",
                    "bind" => "bind_mount",
                    _ => type,
                };
                mounts.Add(new JsonObject
                {
                    ["kind"] = kind,
                    ["type"] = type,
                    ["name"] = name,
                    ["source"] = StringValue(mountObject["Source"]),
                    ["destination"] = StringValue(mountObject["Destination"]),
                    ["mode"] = StringValue(mountObject["Mode"]),
                    ["rw"] = mountObject["RW"]?.DeepClone(),
                    ["driver"] = StringValue(mountObject["Driver"]) ?? "local",
                    ["volume_options"] = mountObject["VolumeOptions"]?.DeepClone() ?? new JsonObject(),
                });
                if (kind is "named_volume" or "anonymous_volume" && !string.IsNullOrWhiteSpace(name)) volumeNames.Add(name);
            }

            var compose = ComposeMetadata(labels);
            var container = new JsonObject
            {
                ["id"] = id,
                ["short_id"] = id[..Math.Min(12, id.Length)],
                ["name"] = StringValue(inspect["Name"])?.TrimStart('/'),
                ["state"] = inspect["State"]?.DeepClone() ?? new JsonObject(),
                ["image"] = new JsonObject
                {
                    ["reference"] = StringValue(config?["Image"]),
                    ["id"] = imageId,
                },
                ["config"] = new JsonObject
                {
                    ["hostname"] = StringValue(config?["Hostname"]),
                    ["user"] = StringValue(config?["User"]),
                    ["working_dir"] = StringValue(config?["WorkingDir"]),
                    ["entrypoint"] = config?["Entrypoint"]?.DeepClone(),
                    ["command"] = config?["Cmd"]?.DeepClone(),
                    ["environment"] = config?["Env"]?.DeepClone() ?? new JsonArray(),
                    ["labels"] = labels.DeepClone(),
                    ["healthcheck"] = config?["Healthcheck"]?.DeepClone(),
                },
                ["host_config"] = new JsonObject
                {
                    ["restart_policy"] = hostConfig?["RestartPolicy"]?.DeepClone() ?? new JsonObject(),
                    ["network_mode"] = StringValue(hostConfig?["NetworkMode"]),
                    ["port_bindings"] = hostConfig?["PortBindings"]?.DeepClone() ?? new JsonObject(),
                    ["privileged"] = hostConfig?["Privileged"]?.DeepClone() ?? false,
                    ["devices"] = hostConfig?["Devices"]?.DeepClone() ?? new JsonArray(),
                    ["cap_add"] = hostConfig?["CapAdd"]?.DeepClone() ?? new JsonArray(),
                    ["cap_drop"] = hostConfig?["CapDrop"]?.DeepClone() ?? new JsonArray(),
                },
                ["mounts"] = mounts,
                ["networks"] = inspect["NetworkSettings"]?["Networks"]?.DeepClone() ?? new JsonObject(),
                ["compose"] = compose,
                ["compose_labels"] = LabelsWithPrefix(labels, "com.docker.compose."),
                ["inspect"] = inspect.DeepClone(),
            };
            containers.Add(container);

            var project = StringValue(compose["project"]);
            if (!string.IsNullOrWhiteSpace(project))
            {
                if (!composeProjects.TryGetValue(project, out var group))
                {
                    group = new JsonObject
                    {
                        ["name"] = project,
                        ["working_dir"] = compose["working_dir"]?.DeepClone(),
                        ["config_files"] = compose["config_files"]?.DeepClone(),
                        ["services"] = new JsonArray(),
                    };
                    composeProjects[project] = group;
                }
                var service = StringValue(compose["service"]);
                if (!string.IsNullOrWhiteSpace(service))
                {
                    var services = group["services"]!.AsArray();
                    if (!services.Any(item => StringValue(item) == service)) services.Add(service);
                }
            }
        }

        var images = new JsonArray();
        foreach (var imageId in imageIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject? inspect = null;
            try
            {
                inspect = (await GetJsonAsync($"/images/{Uri.EscapeDataString(imageId)}/json")).AsObject();
            }
            catch (DockerException ex) when (IsNotFound(ex))
            {
                warnings.Add($"image {imageId} is missing; its container configuration was preserved but the image cannot be exported");
            }
            if (inspect is null)
            {
                images.Add(new JsonObject
                {
                    ["id"] = imageId,
                    ["missing"] = true,
                    ["error"] = "image not found on Docker host",
                    ["repo_tags"] = new JsonArray(),
                    ["repo_digests"] = new JsonArray(),
                });
                continue;
            }
            images.Add(new JsonObject
            {
                ["id"] = StringValue(inspect["Id"]) ?? imageId,
                ["created"] = StringValue(inspect["Created"]),
                ["repo_tags"] = inspect["RepoTags"]?.DeepClone() ?? new JsonArray(),
                ["repo_digests"] = inspect["RepoDigests"]?.DeepClone() ?? new JsonArray(),
                ["architecture"] = StringValue(inspect["Architecture"]),
                ["os"] = StringValue(inspect["Os"]),
                ["size"] = inspect["Size"]?.DeepClone(),
                ["inspect"] = inspect.DeepClone(),
            });
        }

        var volumes = new JsonArray();
        foreach (var volumeName in volumeNames.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            JsonObject? inspect = null;
            try
            {
                inspect = (await GetJsonAsync($"/volumes/{Uri.EscapeDataString(volumeName)}")).AsObject();
            }
            catch (DockerException ex) when (IsNotFound(ex))
            {
                warnings.Add($"volume {volumeName} disappeared during discovery");
            }
            if (inspect is null)
            {
                volumes.Add(new JsonObject
                {
                    ["name"] = volumeName,
                    ["missing"] = true,
                    ["error"] = "volume not found on Docker host",
                });
                continue;
            }
            volumes.Add(new JsonObject
            {
                ["name"] = StringValue(inspect["Name"]) ?? volumeName,
                ["driver"] = StringValue(inspect["Driver"]),
                ["mountpoint"] = StringValue(inspect["Mountpoint"]),
                ["scope"] = StringValue(inspect["Scope"]),
                ["labels"] = inspect["Labels"]?.DeepClone() ?? new JsonObject(),
                ["options"] = inspect["Options"]?.DeepClone() ?? new JsonObject(),
                ["inspect"] = inspect.DeepClone(),
            });
        }

        var networks = new JsonArray();
        foreach (var network in (await GetJsonAsync("/networks")).AsArray())
        {
            var id = StringValue(network?["Id"]);
            if (string.IsNullOrWhiteSpace(id)) continue;
            JsonObject inspect;
            try
            {
                inspect = (await GetJsonAsync($"/networks/{Uri.EscapeDataString(id)}")).AsObject();
            }
            catch (DockerException ex) when (IsNotFound(ex))
            {
                warnings.Add($"network {id} disappeared during discovery");
                continue;
            }
            networks.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = StringValue(inspect["Name"]),
                ["driver"] = StringValue(inspect["Driver"]),
                ["scope"] = StringValue(inspect["Scope"]),
                ["internal"] = inspect["Internal"]?.DeepClone(),
                ["labels"] = inspect["Labels"]?.DeepClone() ?? new JsonObject(),
                ["inspect"] = inspect.DeepClone(),
            });
        }

        return new JsonObject
        {
            ["schema_version"] = 2,
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["endpoint"] = _endpointDescription,
            ["docker"] = new JsonObject
            {
                ["server_version"] = StringValue(version["Version"]),
                ["server_api_version"] = StringValue(version["ApiVersion"]),
            },
            ["containers"] = containers,
            ["images"] = images,
            ["volumes"] = volumes,
            ["networks"] = networks,
            ["compose_projects"] = new JsonArray(composeProjects.Values.Select(group => group.DeepClone()).ToArray()),
            ["warnings"] = warnings,
        };
    }

    private static bool IsNotFound(DockerException exception) =>
        exception.Message.Contains(" 404 ", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ComposeMetadata(JsonObject labels)
    {
        var result = new JsonObject();
        var keys = new Dictionary<string, string>
        {
            ["project"] = "com.docker.compose.project",
            ["service"] = "com.docker.compose.service",
            ["working_dir"] = "com.docker.compose.project.working_dir",
            ["config_files"] = "com.docker.compose.project.config_files",
            ["version"] = "com.docker.compose.version",
        };
        foreach (var pair in keys)
            if (labels[pair.Value] is not null) result[pair.Key] = labels[pair.Value]!.DeepClone();
        return result;
    }

    private static JsonObject LabelsWithPrefix(JsonObject labels, string prefix)
    {
        var result = new JsonObject();
        foreach (var pair in labels)
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) result[pair.Key] = pair.Value?.DeepClone();
        return result;
    }

    private static string? StringValue(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<string>(); }
        catch (InvalidOperationException) { return node.ToJsonString().Trim('"'); }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record Endpoint(string Description, Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectAsync)
{
    public static IReadOnlyList<Endpoint> ParseCandidates(string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
            return [Parse(endpoint ?? Environment.GetEnvironmentVariable("DOCKER_HOST")!)];
        if (OperatingSystem.IsWindows())
            return [NamedPipe("dockerDesktopLinuxEngine"), NamedPipe("docker_engine")];
        var socket = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";
        return [Unix(socket)];
    }

    private static Endpoint Parse(string value)
    {
        if (value.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = value[(value.LastIndexOf('/') + 1)..];
            return NamedPipe(pipe);
        }
        if (value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)) return Unix(value[7..]);
        if (value.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri("http://" + value[6..]);
            return new Endpoint(value, async (context, cancellationToken) => await new TcpClient().ConnectNetworkStreamAsync(uri.Host, uri.Port, cancellationToken));
        }
        return Unix(value);
    }

    private static Endpoint Unix(string path) => new($"unix://{path}", async (_, cancellationToken) =>
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    });

    private static Endpoint NamedPipe(string name) => new($"npipe://./pipe/{name}", async (_, cancellationToken) =>
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        return pipe;
    });
}

internal static class TcpClientExtensions
{
    public static async ValueTask<Stream> ConnectNetworkStreamAsync(this TcpClient client, string host, int port, CancellationToken cancellationToken)
    {
        await client.ConnectAsync(host, port, cancellationToken);
        return client.GetStream();
    }
}
