# Dsh Windows Launcher（DeepSeek Harness 服务启动器）

一个 Windows 桌面 GUI 小工具，用 C# / WinForms 编写，用于启动、停止、重启当前 harness 的 Web 服务（默认 `pnpm dsh web`，监听 `http://127.0.0.1:3080`）。

## 功能

- **启动 / 停止 / 重启** harness 服务，界面实时显示运行状态与运行时长。
- **日志面板**：实时捕获子进程的 stdout / stderr。
- **优雅关闭**：停止时先发送 Ctrl+C（对应 harness 的 SIGINT 优雅退出），未在预期时间内退出则回退 `taskkill /T /F` 强杀进程树。默认命令 `pnpm dsh web` 走 `pnpm.cmd`（Windows 批处理），cmd 层会吞掉 Ctrl+C，因此对 pnpm 实际是「短等待后强杀」；直连 node 等非批处理命令才能真正优雅退出。
- **Web UI 健康检查**：每 2 秒探测一次 Web UI 地址，显示「在线 / 离线」。
- **打开 Web UI**：用默认浏览器打开服务地址。
- **设置持久化**：工作目录、启动命令、Web UI 地址保存到 `%APPDATA%\DshServiceLauncher\settings.json`。

## 构建

需要 Windows + .NET 10 SDK（本机已装 `10.0.301`）：

```powershell
cd tools/dsh-windows-launcher
dotnet build -c Release
```

产物：`bin\Release\net10.0-windows\DshServiceLauncher.exe`。

发布为自包含单文件（目标电脑无需安装 .NET，可直接拷贝运行）：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物：`bin\Release\net10.0-windows\win-x64\publish\DshServiceLauncher.exe`（约 100 MB，已内置运行时）。要求目标机器为 Windows 10/11 x64；启动器本身免依赖，但要真正拉起服务，目标机器仍需有 harness 仓库与 `pnpm`（在「工作目录 / 启动命令」中配置）。

## 使用

1. 运行 `DshServiceLauncher.exe`。
2. 「工作目录」默认自动探测到 dsh 仓库根目录（向上查找 `package.json` 中的 `@deepseek-ai/dsh-root`）。
3. 「启动命令」默认为 `pnpm dsh web`，与手动启动一致。
4. 点击「启动」，待状态变为「运行中」且「Web UI：在线」后点「打开 Web UI」。

## 说明

- 启动命令在子进程 `cmd.exe` 中执行，工作目录为上方的「工作目录」。
- 关闭启动器窗口时，若服务仍在运行，会提示并一并停止服务。
- 停止服务会关闭对应 Web UI 及其中的会话，请在停止前确认无需保留的对话已保存。
