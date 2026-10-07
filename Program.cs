using System.Text.Json;
using System.Text.Json.Nodes;

namespace DockerTransfer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return await Interactive.RunAsync();
            }
            var options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintHelp();
                return 0;
            }

            if (options.Command is not ("discover" or "backup" or "version"))
            {
                Console.Error.WriteLine($"Unknown command: {options.Command}");
                Options.PrintHelp();
                return 2;
            }

            await using var client = await DockerClient.ConnectAsync(options.Endpoint);
            if (options.Command == "version")
            {
                var version = await client.GetJsonAsync("/version");
                Console.WriteLine(version.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            var manifest = await client.DiscoverAsync();
            if (options.Command == "backup")
            {
                var backupPath = await BackupRunner.CreateAsync(manifest, options.Output);
                Console.WriteLine($"backup created: {backupPath}");
                return 0;
            }
            var json = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            if (options.Stdout)
            {
                Console.Write(json);
            }
            else
            {
                await File.WriteAllTextAsync(options.Output, json);
                var containers = manifest["containers"]!.AsArray().Count;
                var images = manifest["images"]!.AsArray().Count;
                var volumes = manifest["volumes"]!.AsArray().Count;
                var projects = manifest["compose_projects"]!.AsArray().Count;
                Console.WriteLine($"discovered {containers} containers, {images} images, {volumes} volumes, {projects} compose projects -> {options.Output}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is DockerException or IOException or HttpRequestException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }
}

internal sealed record Options
{
    public string Command { get; private init; } = "discover";
    public string Output { get; private init; } = "docker-manifest.json";
    public bool Stdout { get; private init; }
    public bool Help { get; private init; }
    public string? Endpoint { get; private init; }

    public static Options Parse(string[] args)
    {
        var result = new Options();
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    result = result with { Help = true };
                    break;
                case "--stdout":
                    result = result with { Stdout = true };
                    break;
                case "-o":
                case "--output":
                    if (++i >= args.Length) throw new ArgumentException("--output requires a path");
                    result = result with { Output = args[i] };
                    break;
                case "--endpoint":
                    if (++i >= args.Length) throw new ArgumentException("--endpoint requires a Docker endpoint");
                    result = result with { Endpoint = args[i] };
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }
        if (positional.Count > 0) result = result with { Command = positional[0] };
        return result;
    }

    public static void PrintHelp() => Console.WriteLine("""
Docker Transfer - cross-platform Docker inventory and migration tool

Usage:
  docker-transfer discover [--output docker-manifest.json]
  docker-transfer backup --output backup.zip
  docker-transfer discover --stdout
  docker-transfer version

Run `docker-transfer` without arguments to open the interactive Chinese wizard.

Options:
  -o, --output PATH       Output manifest or backup ZIP path
      --stdout            Print the manifest instead of writing a file
      --endpoint VALUE    Override DOCKER_HOST (unix:///..., npipe:////./pipe/... or tcp://...)
  -h, --help              Show this help

The discovery command is read-only. It inspects containers, images, volumes,
bind mounts, networks, and Compose labels without stopping or modifying Docker resources.
""");
}

internal static class Interactive
{
    public static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("============================================");
            Console.WriteLine(" Docker Transfer  Docker 容器迁移向导");
            Console.WriteLine("============================================");
            Console.WriteLine("1. 扫描当前 Docker 主机");
            Console.WriteLine("2. 创建备份包");
            Console.WriteLine("3. 查看命令帮助");
            Console.WriteLine("0. 退出");
            Console.Write("请选择操作 [1]: ");
            var choice = (Console.ReadLine() ?? "1").Trim();
            try
            {
                switch (choice)
                {
                    case "1":
                        await DiscoverAsync();
                        break;
                    case "2":
                        await BackupAsync();
                        break;
                    case "3":
                        Options.PrintHelp();
                        break;
                    case "0":
                        return 0;
                    default:
                        Console.WriteLine("无效选项，请重新选择。");
                        break;
                }
            }
            catch (Exception ex) when (ex is DockerException or IOException or HttpRequestException)
            {
                Console.WriteLine($"操作失败：{ex.Message}");
            }
            if (choice is "1" or "2")
            {
                Console.WriteLine();
                Console.Write("按 Enter 返回主菜单...");
                Console.ReadLine();
            }
        }
    }

    private static async Task DiscoverAsync()
    {
        Console.WriteLine();
        Console.WriteLine("正在连接 Docker 并扫描资源，请稍候...");
        await using var client = await DockerClient.ConnectAsync(null);
        var manifest = await client.DiscoverAsync();
        var containers = manifest["containers"]!.AsArray();
        var images = manifest["images"]!.AsArray();
        var volumes = manifest["volumes"]!.AsArray();
        var projects = manifest["compose_projects"]!.AsArray();
            var warnings = manifest["warnings"]?.AsArray() ?? new JsonArray();
        Console.WriteLine($"发现容器：{containers.Count}");
        Console.WriteLine($"发现镜像：{images.Count}");
        Console.WriteLine($"发现卷：{volumes.Count}");
        Console.WriteLine($"发现 Compose 项目：{projects.Count}");
        if (warnings.Count > 0)
        {
            Console.WriteLine($"警告：{warnings.Count} 项资源在扫描期间不可用");
            foreach (var warning in warnings) Console.WriteLine($"  ! {Text(warning)}");
        }
        if (containers.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("容器列表：");
            foreach (var item in containers)
            {
                var name = Text(item?["name"]);
                var state = Text(item?["state"]?["Status"]);
                var image = Text(item?["image"]?["reference"]);
                var project = Text(item?["compose"]?["project"]);
                Console.WriteLine($"  - {name,-24} {state,-10} {image} {(string.IsNullOrWhiteSpace(project) ? "[standalone]" : $"[compose:{project}]")}");
            }
        }
        var output = AskPath("保存 manifest 路径", "docker-manifest.json");
        await File.WriteAllTextAsync(output, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        Console.WriteLine($"manifest 已保存：{Path.GetFullPath(output)}");
    }

    private static async Task BackupAsync()
    {
        Console.WriteLine();
        Console.WriteLine("备份会读取镜像、卷和 bind mount，并生成一个 ZIP 文件。");
        Console.WriteLine("当前版本不会自动停止容器；数据库一致性备份将在后续向导步骤加入。");
        if (!AskYesNo("继续创建备份", true)) return;
        await using var client = await DockerClient.ConnectAsync(null);
        var manifest = await client.DiscoverAsync();
        var warnings = manifest["warnings"]?.AsArray() ?? new JsonArray();
        Console.WriteLine($"将备份 {manifest["containers"]!.AsArray().Count} 个容器、{manifest["images"]!.AsArray().Count} 个镜像、{manifest["volumes"]!.AsArray().Count} 个卷。");
        if (warnings.Count > 0) Console.WriteLine($"注意：发现阶段有 {warnings.Count} 条警告，缺失资源会被跳过并写入 manifest。");
        var defaultName = "docker-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
        var output = AskPath("备份 ZIP 路径", defaultName);
        Console.WriteLine("正在导出镜像并归档卷，请稍候...");
        var result = await BackupRunner.CreateAsync(manifest, output);
        Console.WriteLine($"备份完成：{result}");
        Console.WriteLine($"文件大小：{new FileInfo(result).Length:N0} bytes");
    }

    private static string AskPath(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        var value = (Console.ReadLine() ?? "").Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static bool AskYesNo(string question, bool defaultYes)
    {
        var suffix = defaultYes ? "[Y/n]" : "[y/N]";
        Console.Write($"{question} {suffix}: ");
        var value = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return defaultYes;
        return value is "y" or "yes" or "是";
    }

    private static string Text(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>() ?? ""; }
        catch (InvalidOperationException) { return node.ToJsonString().Trim('"'); }
    }
}
