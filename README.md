# OptiScaler for Playnite

在 Playnite 侧边栏中管理 [OptiScaler](https://github.com/optiscaler/OptiScaler) 的 Playnite Generic 插件（Windows Desktop 模式）。

A Playnite generic plugin that manages OptiScaler installations for your installed games directly from the Playnite sidebar.

## 功能

- 直接使用 Playnite 数据库中的已安装游戏和安装目录，不重复扫描各游戏平台；可选自定义游戏列表。
- 获取 OptiScaler 发布信息，本地缓存下载包，离线时使用缓存的元数据。
- 支持 `.zip`、`.7z` 和已解压目录形式的安装包。
- 可选注入 DLL；事务式安装 / 更新，外部备份，基于哈希的冲突检查，失败自动回滚，以及卸载。
- 为已管理的安装提供 `OptiScaler.ini` 配置编辑器。
- 只读检测 OptiScaler Client 的外部备份和旧版 `OptiScalerBackup` manifest，并可无损导入。

## 安装

在 [Releases](../../releases) 页面下载 `.pext` 文件，拖到 Playnite 窗口中或双击安装。安装或更新 OptiScaler 前，请先关闭要处理的游戏。

## 构建

项目目标框架为 `.NET Framework 4.6.2`，引用 Playnite 安装目录中的 `Playnite.SDK.dll`、`SharpCompress.dll` 和 `Newtonsoft.Json.dll`。默认路径为 `D:\Program\Playnite`，其他位置可通过 MSBuild 属性指定：

```powershell
dotnet build src\OptiScaler.Playnite\OptiScaler.Playnite.csproj `
  -p:CreateExtensionPackage=true `
  -p:PlayniteSdkPath="C:\Playnite\Playnite.SDK.dll" `
  -p:SharpCompressPath="C:\Playnite\SharpCompress.dll" `
  -p:NewtonsoftJsonPath="C:\Playnite\Newtonsoft.Json.dll"
```

扩展包输出到 `src\OptiScaler.Playnite\bin\Debug\net462\OptiScaler.Playnite.pext`。

核心逻辑的冒烟测试不依赖测试框架：

```powershell
dotnet build tests\OptiScaler.Playnite.Tests\OptiScaler.Playnite.Tests.csproj
& tests\OptiScaler.Playnite.Tests\bin\Debug\net462\OptiScaler.Playnite.Tests.exe
```

## 致谢

本插件的游戏管理流程、备份与冲突处理思路、配置编辑器的结构和说明内容，均借鉴了 **OptiScaler Client** 的设计。感谢 OptiScaler Client 的作者和贡献者，也感谢 [OptiScaler](https://github.com/optiscaler/OptiScaler) 项目及其社区。

This plugin's management workflow, backup / conflict handling approach, and configuration editor are inspired by the design of **OptiScaler Client**. Many thanks to its authors and contributors, and to the OptiScaler project and community.

## 许可证

插件代码以 [GPLv3-or-later](LICENSE) 发布。

插件本身不包含 OptiScaler 或其他第三方运行时 DLL。这些组件由用户自行下载，适用各自的许可证。
