# 创意工坊发布工作区

本目录是发布到 Steam 创意工坊的素材，配合 Mega Crit 官方上传器使用。

## 上传器

下载：https://github.com/megacrit/sts2-mod-uploader/releases

获取 `ModUploader.exe` 后**双击运行一次**，会自动生成 `NewModWorkspace` 工作区目录
（其中包含官方当前版本要求的配置文件，字段名以该文件为准）。

## 上传步骤

1. 把官方生成的 `NewModWorkspace` 重命名为 `Sts2DependencyAssistant`
2. 将本目录中的 `content/`（dll + json）复制进工作区的 `content/`
3. 将本目录的 `image.png` 复制进工作区根目录，覆盖同名文件（必须 < 1MB）
4. 把本目录 `workshop.json` 的字段**对照填写**到工作区自带的配置文件中
   - 若官方文件名为 `workspace.json`，则填到 `workspace.json`
   - `tags` 必须在首次上传时填对（工坊发布后无法修改 tags）
   - `dependencies` 留空数组——本 mod 无前置
5. 在工作区目录打开终端，运行：

```
ModUploader.exe upload -w
```

6. 上传成功后工作区会出现 `mod_id.txt`，记录工坊项目 ID，后续更新沿用同一文件

## 更新流程

1. 用新版 `dist/Sts2DependencyAssistant/` 的 dll 与 json 覆盖工作区 `content/`
2. 在配置文件的 `changeNote` 写本次更新说明
3. 再次运行 `ModUploader.exe upload -w`

## 注意

- `image.png` 必须小于 1MB，当前为 340KB
- 上传前确认 Steam 客户端已登录且拥有游戏
- 首次上传后建议在工坊页面手动补充详细描述与截图
- `mod_id.txt` 已被 `.gitignore` 排除，不会提交到仓库

## 当前预览图

`image.png` 为当前版本游戏内窗口截图（1080×760，355 KB）。

若官方工作区生成的文件名是 `image.jpg`，把本文件改名为 `image.jpg` 即可；
Steam 只校验内容与 1MB 上限，不强制扩展名必须与格式一致。
