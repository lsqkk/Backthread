# 回线 · Backthread

**插曲结束，接着刚才做。** 回线是 Windows 11 x64 的窗口回访工具。回复消息、查完资料之后，用键盘回到中断前那扇仍然打开的窗口。

[下载 Windows 版](https://github.com/lsqkk/Backthread/releases/latest) · [在线演示](https://lsqkk.github.io/Backthread/) · [1.1.1 更新说明](docs/release-1.1.1.md)

## 两个快捷键

| 默认快捷键 | 用法 |
| --- | --- |
| `Ctrl+Alt+J` | 直接切换到要返回的窗口。优先回到你标记的窗口。 |
| `Ctrl+Alt+K` | 标记当前窗口。再次标记会更新目标，成功返回后清除标记。 |

没有可用标记时，J 优先返回最近 15 分钟内停留至少 45 秒的窗口，否则返回上一个可用窗口。

托盘图标可以打开工作台。最近窗口列表可搜索标题或应用，标记就显示在这张列表里。设置可修改快捷键、外观和后台启动方式，关于页可检查更新。

窗口标题、停留时长和标记只保存在内存中。托盘可暂停自动记录。连续两分钟无输入后暂停累计时长。Windows 可能限制某些虚拟桌面或更高权限窗口的前台切换。
## 安装与构建

从发行页下载 `Backthread-1.1.1-win-x64-Setup.exe`。安装面向当前用户，自带运行时。安装包尚未做 Authenticode 签名，发行页同时提供 SHA-256 校验文件。卸载前先从托盘退出回线。

使用 Windows 11 x64 和 .NET SDK 8.0.419 构建。本地打包需 Inno Setup 6.7.4。

```powershell
dotnet restore .\src\Backthread\Backthread.csproj --locked-mode
dotnet run --project .\tests\Backthread.Tests -c Release
dotnet publish .\src\Backthread\Backthread.csproj -c Release -r win-x64 --no-restore -o .\artifacts\publish
.\installer\Build-Installer.ps1 -PublishDir .\artifacts\publish -OutputDir .\artifacts\release
```

GitHub Actions 在推送 `vX.Y.Z` 标签时构建并发布安装包与校验文件，标签须与项目版本一致。官网源文件位于 `site/`，通过 GitHub Pages Actions 部署。

[打包说明](installer) · [产品设计](docs/architecture.md) · [桌面验收流程](docs/acceptance.md)
