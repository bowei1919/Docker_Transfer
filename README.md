# Docker Transfer

Docker Transfer 是一个跨平台 Docker 容器迁移工具。直接运行程序会打开
中文交互式向导，也可以使用命令行模式自动化执行。

English documentation: [README.en.md](README.en.md)

## 当前能力

- 自动发现运行中和已停止的容器
- 自动发现镜像、tag、digest、named volume、anonymous volume 和 bind mount
- 自动识别 Docker Compose 项目与 standalone `docker run` 容器
- 自动读取端口、环境变量、网络、restart policy、healthcheck 和容器配置
- 缺失镜像容错：保留容器配置并记录 warning，不中断全机扫描
- 交互式中文菜单
- 创建包含镜像、卷、bind mount、manifest 和 SHA-256 的备份 ZIP
- Windows、Linux、macOS 的 amd64/arm64 自包含单文件程序

当前备份阶段不会自动停止容器。数据库一致性逻辑备份、上传下载和目标机恢复正在继续实现。

## 交互式使用

不带参数运行：

```powershell
docker-transfer.exe
```

菜单包含：

1. 扫描当前 Docker 主机
2. 创建备份包
3. 查看命令帮助
0. 退出

## 命令行使用

扫描并保存 manifest：

```powershell
docker-transfer discover --output docker-manifest.json
```

直接输出 JSON：

```powershell
docker-transfer discover --stdout
```

创建备份包：

```powershell
docker-transfer backup --output docker-backup.zip
```

Docker 连接地址可以通过 `DOCKER_HOST` 或 `--endpoint` 指定。Windows 默认尝试
Docker Desktop named pipe；Linux/macOS 默认使用 `/var/run/docker.sock`。

## 下载发布包

运行 `.\release.ps1 -Version v0.1.0` 会生成六个平台 ZIP 发布包。每个包都包含
对应平台的可执行文件、双语 README 和 `SHA256SUMS`；运行前请先核对校验值。
这些 ZIP 可以作为 GitHub Release 资产上传。

## 从源码构建

需要 .NET 10 SDK。使用配置好的代理执行：

```powershell
.\build.ps1
```

输出目录为 `artifacts/<runtime>/`，包含：

- `win-x64`、`win-arm64`
- `linux-x64`、`linux-arm64`
- `osx-x64`、`osx-arm64`

## 现成项目复用

项目调研和复用边界见 [RESEARCH.md](RESEARCH.md)。本项目参考并复用了
`ctool`、`BackupDock`、`docker-autocompose` 和 `Repliqate` 的设计，避免重复实现已有能力。

## License

本项目当前代码尚未单独声明最终开源许可证；发布前需要确定许可证。
