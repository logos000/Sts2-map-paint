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

## 联机：为什么队友有时看不到你「一键导入」的画？

本模组在点击「绘制」时，会在内部把线稿整理成存档结构，并调用游戏的 **`LoadDrawings`**，相当于**一次性把线条写进本地地图画布**。这条路径**不会**走玩家用笔在屏幕上移动、按下、拖动时才会触发的**输入与联机同步流程**，因此**联机队友往往看不到**这次导入结果（你本机能看到，是因为本地 UI 已更新）。

社区里常见的「别人也能看见」的做法，是让游戏**以为**有人在真实操作鼠标画笔，例如：

| 项目 | 思路（与联机的关系） |
|------|----------------------|
| [**PIPIKAI/auto-painter-win**](https://github.com/PIPIKAI/auto-painter-win) | 独立桌面程序：用 PyQt 做线稿预览，**自动绘画**通过 `pyautogui` 等去**真的移动、点击鼠标**，在地图画布上逐段绘制。游戏只收到和普通玩家一样的指针事件，因此会按官方逻辑同步。 |
| [**FugerQingliu/SlayTheSpire2AutoDrawing**](https://github.com/FugerQingliu/SlayTheSpire2AutoDrawing) | 外部脚本 / 可执行文件：同样是在系统层面**模拟鼠标**在窗口内绘画，不注入游戏内存里的 `LoadDrawings`，走的是**真实输入**那条路。 |

**若你需要联机展示给别人看**：更稳妥的是用上述一类**外部位图绘画工具**（或自己手写鼠标宏），在地图界面里按笔划慢慢画；本模组更适合**单机快速把整张图铺进地图**或本机预览。

> 将来若在游戏内用模组逐点**模拟输入事件**或找到与官方画笔共用的**网络同步 API**，理论上也能对齐联机表现，但需要针对版本逆向与维护，当前实现未包含这一条。

---

## 许可证

本项目以 [**MIT License**](LICENSE) 发布。

---

## Map Paint (English summary)

Mod for **Slay the Spire 2** that imports an image and redraws it on the map using the game’s drawing system.  
**Requires BaseLib.** Install from **Releases** by extracting the zip into the game’s `mods` folder.  

**Multiplayer note:** this mod bulk-loads strokes via `LoadDrawings` (local state). It does **not** replay pen input, so co-op peers may not see it. Tools like [auto-painter-win](https://github.com/PIPIKAI/auto-painter-win) or [SlayTheSpire2AutoDrawing](https://github.com/FugerQingliu/SlayTheSpire2AutoDrawing) drive the **real mouse** so the game syncs strokes normally.

Licensed under the [MIT License](LICENSE).
