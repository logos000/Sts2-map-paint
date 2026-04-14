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
- **多图叠加**：画新图时不会清除旧图，多张图可以同时存在于画布上。
- **选择性移除**：已绘制列表中可单独移除某一张图，或一键清除全部。
- **断点续画**：停止时自动保存断点（`config/map_paint.playback.json`），下次在相同图片与参数下可续画。
- **移动端（Android / iOS）**：上传图片通过本机 HTTP 服务在浏览器中完成（详见游戏内说明）。

---

## 许可证

本项目以 [**MIT License**](LICENSE) 发布。

---

## Map Paint (English summary)

Map Paint lets you import any image and redraw it on the map in Slay the Spire 2, stroke by stroke, using the game's native drawing API. Pick a picture, adjust a few settings, and watch it come to life on the map — teammates will see it too.

**Core Features:**
- **Multiplayer-visible:** Strokes go through the game's own drawing API (no mouse simulation). Teammates see your art in real time.
- **Multi-image stacking:** Draw multiple images without clearing previous ones. They all coexist on the map.
- **Selective removal:** Remove a specific image from the map while keeping others, or clear the entire canvas at once.
- **Two extraction algorithms:** Canny for a hand-drawn feel, Skeleton (XDoG) for clean contours.
- **Tunable parameters:** Adjust scale, detail, stroke count, edge sensitivity, line joining, contrast, and drawing speed.
- **Auto-save & resume:** Progress saves when you stop, picking up exactly where you left off.
- **Collapsible UI:** Panel shrinks to a draggable ball so it never blocks your view.
- **Mobile support:** Upload images via a local browser page on Android/iOS.

**Requires BaseLib.** Install from **Releases** by extracting the zip into the game's `mods` folder. Licensed under the [MIT License](LICENSE).
