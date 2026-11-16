# 打包回线安装程序

需要 Windows 和 [Inno Setup](https://jrsoftware.org/inno-setup/downloads) 编译器（已验证 6.7.4）。`PublishDir` 是项目以 `win-x64` 自包含发布后的整个目录，其中应带上 WinUI 和 .NET 运行时。以下命令从仓库根目录运行：

```powershell
dotnet publish .\src\Backthread\Backthread.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish\win-x64
.\installer\Build-Installer.ps1 `
  -PublishDir .\artifacts\publish\win-x64 `
  -OutputDir .\artifacts\release `
  -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

也可通过环境变量 `INNO_ISCC` 提供编译器路径；支持在 `PATH` 和常见安装位置查找已安装编译器。若只想获得本地编译器，可使用 [Inno Setup 官方便携模式](https://jrsoftware.org/inno-setup/documentation/topic_technotes.htm)，并将 `-IsccPath` 指向便携目录中的 `ISCC.exe`。`-Version` 默认是 `1.0.0`，发布新版时应与项目版本一起更新。输出目录须在发布目录之外。构建脚本同时生成安装 EXE 和对应的 `.sha256` 校验文件。

安装面向 Windows 11 x64 的当前账户，固定目录为 `%LOCALAPPDATA%\Programs\Backthread`，从开始菜单的「回线 Backthread」进入。该安装包不会设置开机启动。安装完成页面可以勾选启动回线；静默安装时不启动。覆盖安装时，安装向导可请求 Windows 正常关闭占用安装文件的回线；卸载时若回线还开着，请先关闭主窗口，再重试。会话中的轨迹和返回点随回线退出消失。

本地构建出的安装程序没有 Authenticode 签名。可用 `Get-FileHash -Algorithm SHA256 <安装程序>` 与生成的校验文件比对；哈希校验用于确认文件一致，不能替代发布者签名。
