# Map Paint（地图绘制）

**Slay the Spire 2** 模组：将图片导入，并用游戏内置绘图系统在地图上重新绘制线稿。

| | |
|---|---|
| **模组 ID** | `map_paint` |
| **依赖** | [BaseLib](https://github.com/Alchyr/BaseLib)（需先于本模组安装） |
| **当前版本** | 见 [Releases](https://github.com/logos000/Sts2-map-paint/releases) 与 `map_paint.json` |

---

## 下载与安装

1. 打开 [**Releases**](https://github.com/logos000/Sts2-map-paint/releases)，下载对应版本的 **`map_paint.zip`**。
2. 解压到游戏目录下的 `mods` 文件夹，使存在路径：  
   `…/Slay the Spire 2/mods/map_paint/map_paint.dll` 与 `map_paint.json` 等。
3. 确保已安装 **BaseLib**，再启动游戏。

---

## 从源码构建

1. 安装 **Godot 4.x**（与项目 `project.godot` 一致）及 **.NET 9 SDK**。
2. 克隆本仓库后，编辑 **`map_paint.csproj`**：
   - 将 `<Sts2Dir>` 改为你本机 **Slay the Spire 2** 安装路径（需能访问 `data_sts2_windows_x86_64\sts2.dll` 与 `mods/BaseLib/BaseLib.dll`）。
3. 用 Godot 打开本项目并构建，或使用 `dotnet build`；构建成功后脚本会将输出复制到 `$(Sts2Dir)\mods\map_paint\`。

---

## 功能概要

- 在地图界面导入图库中的图片，按可调参数提取笔画并绘制到地图上。
- **联机可见**：点击「开始绘制」后，模组直接调用游戏 `NMapDrawings` 的 Local API（`BeginLineLocal` / `UpdateCurrentLinePositionLocal` / `StopLineLocal`）按帧逐笔推进，内部自动发送联机消息，队友实时可见。无需模拟鼠标/触摸事件。
- **F5 快捷键**：随时按 F5 切换开始/停止绘制。停止时自动保存断点（`config/map_paint.playback.json`），下次在相同图片与参数下可续画。
- **本地预览**：缩放行旁的「预览」按钮使用 `LoadDrawings` 快速加载线稿（仅本机可见，不走联机同步），方便调参时即时查看效果。
- **移动端（Android / iOS）**：上传图片通过本机 HTTP 服务在浏览器中完成（详见游戏内说明）。

---

## 许可证

本项目以 [**MIT License**](LICENSE) 发布。

---

## Map Paint (English summary)

Mod for **Slay the Spire 2** that imports an image and redraws it on the map using the game's drawing system.  
**Multiplayer-visible:** strokes are drawn via the game's native `NMapDrawings` Local API, so teammates see them in real time.  
**Requires BaseLib.** Install from **Releases** by extracting the zip into the game's `mods` folder.  
Licensed under the [MIT License](LICENSE).
