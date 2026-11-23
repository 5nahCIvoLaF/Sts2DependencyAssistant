# 前置检查助手 (Sts2DependencyAssistant)

《杀戮尖塔 2》的依赖管理 mod：检查已启用 mod 的前置是否齐全，在游戏内窗口列出缺失/禁用/版本过低/待重启等问题，并可一键通过 Steam 创意工坊下载缺失前置。

**无需任何前置 mod，可独立运行。**

## 功能

- **递归依赖检查**：扫描所有已启用的 mod，递归解析 `manifest.dependencies`（含循环依赖保护，最深 8 层）
- **六类状态区分**：满足 / 未安装 / 已禁用 / 版本过低 / 加载失败 / 重复安装 / 需重启
- **工坊"必需物品"检测**：补足游戏 manifest 未声明、但工坊页面标注为必需的前置（如 SpeedX 依赖 ModConfig）
- **一键下载**：调用 Steam 原生 `SubscribeItem` + `DownloadItem`，带实时下载进度条，不跳浏览器
- **游戏内窗口**：主界面右下角「前置检查」按钮随时打开；启动时若检出问题会自动提示，无问题则静默
- **便捷关闭**：点击窗口外区域或按 ESC 关闭

## 截图

（见 `workshop/image.png`）

## 安装

**通过 Steam 创意工坊（推荐）**：在工坊订阅后启动游戏，在 Mods 菜单启用本 mod，重启游戏。

**手动安装**：将 `Sts2DependencyAssistant.dll` 与 `Sts2DependencyAssistant.json` 放入游戏目录下的 `mods/Sts2DependencyAssistant/`：

| 平台 | 路径 |
|---|---|
| Windows | `...\steamapps\common\Slay the Spire 2\mods\Sts2DependencyAssistant\` |
| macOS | `.../SlayTheSpire2.app/Contents/MacOS/mods/Sts2DependencyAssistant/` |
| Linux | `.../common/Slay the Spire 2/mods/Sts2DependencyAssistant/` |

## 兼容性

- 游戏版本 ≥ **v0.113.1**
- 无硬性 mod 前置。已安装 AutoModSubscriber 时会复用它优化订阅队列；未安装则使用 Steam 原生接口
- 游戏小版本更新后如出现异常，请附 `godot.log` 反馈

## 从源码构建

```bash
cd src
dotnet build -p:SteamLibraryPath="<你的 steamapps 路径>"
```

构建产物会自动复制到 `dist/Sts2DependencyAssistant/`。

需要 .NET 9 SDK 与 Godot.NET.Sdk 4.5.1。引用仅指向游戏自带程序集（`sts2.dll`、`GodotSharp.dll`、`Steamworks.NET.dll`、`0Harmony.dll`），不随包分发。

## 发布创意工坊

使用 Mega Crit 官方的 [sts2-mod-uploader](https://github.com/megacrit/sts2-mod-uploader)：

1. 解压 `workshop/` 内容到上传器同级的自定义工作区目录
2. 把 `dist/Sts2DependencyAssistant/` 里的 dll 与 json 放进工作区的 `content/`
3. 回填 `workshop.json`（首次上传后从 `mod_id.txt` 取得工坊 ID）
4. 运行 `ModUploader.exe upload -w <工作区目录>`

## 源码结构

```
src/
  MainFile.cs           初始化入口、主菜单浮动按钮、启动检测
  DependencyScanner.cs  依赖递归扫描与状态分类、隐藏前置规则
  WorkshopBridge.cs     Steamworks 订阅/下载/进度（反射）
  DependencyDialog.cs   游戏内窗口
  UiFont.cs             字体与配色
```

## 许可

MIT
