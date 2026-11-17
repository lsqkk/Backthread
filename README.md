# 回线 · Backthread

**插曲结束，接着刚才做。** 回线是 Windows 11 x64 的窗口回访工具。回复消息、查完资料之后，用键盘回到中断前那扇仍然打开的窗口。

[下载 Windows 版](https://github.com/lsqkk/Backthread/releases/latest) · [在线试用浮窗](https://lsqkk.github.io/Backthread/) · [1.1.0 更新说明](docs/release-1.1.0.md)

## 两个快捷键

| 默认快捷键 | 用法 |
| --- | --- |
| `Ctrl+Alt+J` | 在当时使用的屏幕上呼出置顶浮窗。回车返回预选窗口，方向键挑选，输入应用名或标题过滤。按 Esc 或再次按快捷键收起。 |
| `Ctrl+Alt+K` | 立即把当前窗口压入复工栈。在浮窗内按 `Ctrl+Tab` 切到复工栈，回车返回栈顶。 |

浮窗优先选中最近 15 分钟内停留至少 45 秒的可用窗口。没有符合条件的窗口时，直接预选最近访问的可用窗口。快速切换按窗口去重，访问时间很短的窗口也能找到。

## 工作台与托盘

- **窗口轨迹**保留本次会话最近 60 段前台活动。同一个应用的多扇窗口分别识别。
- **复工栈**可加备注并管理栈顶，最多 12 层。成功回到原窗口后才出栈。
- **设置**可更改全局快捷键、切换外观、启用开机启动和选择关闭时是否驻留托盘，也可以清空本次会话。
- **关于**可检查 GitHub 上的稳定发行版，有更新时提供下载与版本说明。启动时自动检查可在设置中开启。
- **托盘**左键打开快速切换，右键可打开工作台、暂停记录或退出。若默认快捷键被占用，可从托盘进入设置修改。

窗口标题、停留时长和备注只保存在内存中。连续两分钟无输入后暂停累计时长。系统限制可能使某些虚拟桌面或更高权限的窗口无法返回，回线会保留对应的检查点。

## 安装与构建

从发行页下载 `Backthread-1.1.0-win-x64-Setup.exe`。安装面向当前用户，自带运行时。安装包尚未做 Authenticode 签名，发行页同时提供 SHA-256 校验文件。卸载前先从托盘退出回线。

使用 Windows 11 x64 和 .NET SDK 8.0.419 构建。本地打包需 Inno Setup 6.7.4。

```powershell
dotnet restore .\src\Backthread\Backthread.csproj --locked-mode
dotnet run --project .\tests\Backthread.Tests -c Release
dotnet publish .\src\Backthread\Backthread.csproj -c Release -r win-x64 --no-restore -o .\artifacts\publish
.\installer\Build-Installer.ps1 -PublishDir .\artifacts\publish -OutputDir .\artifacts\release
```

GitHub Actions 在推送 `vX.Y.Z` 标签时构建并发布安装包与校验文件，标签须与项目版本一致。官网源文件位于 `site/`，通过 GitHub Pages Actions 部署。

[打包说明](installer) · [产品设计](docs/architecture.md) · [桌面验收流程](docs/acceptance.md)
