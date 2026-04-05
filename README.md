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
- **移动端**：可通过本机 HTTP 服务在浏览器中上传图片至图库（详见游戏内说明）。

---

## 联机可见（绘制按钮）

点击 **「绘制」** 时，模组会通过 **`Viewport.PushInput`** 按顺序注入**左键按下 → 移动 → 抬起**，走与真实鼠标画笔相同的输入路径，便于**联机同步**（思路与 [auto-painter-win](https://github.com/PIPIKAI/auto-painter-win)、[SlayTheSpire2AutoDrawing](https://github.com/FugerQingliu/SlayTheSpire2AutoDrawing) 等**外部位图工具**一致：让游戏收到「像在画画」的事件）。

- **请先切换到地图上的「画笔」模式**，再点「绘制」；绘制过程中面板会暂时隐藏，**请勿操作鼠标**，直至状态提示完成。
- 调整参数滑块时的**即时预览**仍使用 **`LoadDrawings`**（仅本机快速预览）；联机队友看到的是你**最后一次点「绘制」**模拟画上去的内容。

若模拟绘制在某一游戏版本上坐标或输入不生效，可反馈版本号；也可继续使用上述外部工具作为备选。

---

## 许可证

本项目以 [**MIT License**](LICENSE) 发布。

---

## Map Paint (English summary)

Mod for **Slay the Spire 2** that imports an image and redraws it on the map using the game’s drawing system.  
**Requires BaseLib.** Install from **Releases** by extracting the zip into the game’s `mods` folder.  

**Multiplayer:** the **Draw** button injects mouse events via `Viewport.PushInput` so strokes replicate like real pen input. Slider preview still uses `LoadDrawings` (local only). Have the in-game **brush** tool selected before drawing.

Licensed under the [MIT License](LICENSE).
