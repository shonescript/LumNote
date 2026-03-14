# 超长文档性能分析

> 现象：打开 4 万+ 行文档时，Debug 消息未触发、渲染区滚动卡顿、编辑区点击光标停顿。

---

## 一、瓶颈定位

### 1.1 布局任务耗时过长（主因）

| 位置 | 行为 | 影响 |
|------|------|------|
| `LayoutTaskScheduler.EnqueueLayoutFromBlocks` | 始终使用 `ComputeFull`（scrollY=null） | 对全部块做 Skia 布局 |
| `LayoutComputeService.ComputeFull` | 对每个块调用 `layoutEngine.Layout()` | 4 万块 × 每块 Skia 测量 = 数分钟级 |
| `ApplyLayoutSnapshot` | 布局完成后才执行 | 任务未完成则永不触发，Debug 消息不出现 |

**结论**：全量布局 4 万+ 块时，后台任务长时间运行，UI 一直无布局快照，表现为空白或旧内容，且 Debug 消息不出现。

### 1.2 可见块查找 O(n)

| 位置 | 行为 | 影响 |
|------|------|------|
| `RenderEngine.FindFirstVisibleBlockIndex` | 对 `_cachedLayouts` 线性遍历 | 每帧 Render 调用一次，4 万块 = 4 万次比较 |
| `GetVisibleBlockRange` 的 end 查找 | 从 start 再线性扫到 limit | 额外 O(n) |

**结论**：已有 `_cumulativeY` 数组，应改为二分查找，将 O(n) 降为 O(log n)。

### 1.3 滚动与重绘

| 位置 | 行为 | 影响 |
|------|------|------|
| `MarkdownEngineView.Scroll.ScrollChanged` | 设置 `ScrollOffset`，32ms 节流后 `InvalidateVisual` | 已有节流，非主因 |
| `ScrollOffsetProperty.Changed` | `InvalidateVisual()` | 每次滚动触发重绘，但渲染已只画可见块 |

**结论**：滚动节流已存在，主要成本在可见块查找和布局。

### 1.4 编辑区高亮

| 位置 | 行为 | 影响 |
|------|------|------|
| `EditorController.UpdateEditorHighlight` | `ScrollOffsetChanged` / `Caret.PositionChanged` 触发 | 90ms 防抖后对“10 页”窗口做语法分析 |
| `MarkdownHighlightingService.Analyze` | 对可见窗口行做解析 | 4 万行文档中间位置可能分析 2000+ 行 |

**结论**：长文档时高亮分析可能较重，但非布局卡顿主因。

---

## 二、解决方案

### 2.1 已实现：可见块二分查找

- 使用 `_cumulativeY` 做二分查找，替代 `FindFirstVisibleBlockIndex` 的线性遍历。
- 将 `GetVisibleBlockRange` 的 start/end 查找均改为二分，整体从 O(n) 降为 O(log n)。

### 2.2 已实现：长文档两阶段布局

当块数超过阈值（如 500）时：

1. **第一阶段**：用轻量高度估计（行数 × 行高）快速得到 `cumulativeY`，确定可见窗口。
2. **第二阶段**：仅对可见窗口内的块做完整 `Layout()`，其余块不调用 Skia。

效果：首次显示从“全量布局数分钟”变为“估计 + 约百块布局”，秒级内完成。

### 2.3 已实现：滚动时重新布局

- 块数 > 500 时，`ScrollOffsetProperty.Changed` 触发 100ms 防抖，停止滚动后调用 `TriggerParseAndLayout`。
- 使用当前 scrollY 重新执行 `ComputeSlim`，布局新可见区域，用户滚动到文档中部/底部时可正常显示。
- 预渲染范围：上下各约 1.5 视口（`margin = viewportHeight * 1.5`），便于低速/正常滚动时流畅预览。

### 2.4 视口高度必须用 ScrollViewer.Viewport，不能用 Bounds.Height

- `EngineRenderControl` 作为 ScrollViewer 的内容，其 `Bounds.Height` = 文档总高度（如 1170403），不是视口高度。
- 若误用 `Bounds.Height` 作为 viewportHeight，`yEnd = scrollY + 1170403 + 400` 会包含全部块，导致 `visible=17108`（全量布局）。
- 修复：新增 `EngineRenderControl.ViewportHeight`，由 `MarkdownEngineView` 在 `OnSizeChanged` / `OnAttachedToVisualTree` / `UpdateDocument` 中设置 `Scroll.Viewport.Height`。

### 2.5 已实现：刷新/窗口尺寸改变时保持滚动位置

- `EngineRenderControl.LayoutApplied` 事件：布局快照应用后触发。
- `MarkdownEngineView` 在 `OnSizeChanged`、`UpdateDocument` 前保存滚动比例，订阅 `LayoutApplied` 后按比例恢复，避免返回顶部。
- 新开文档（`_lastMarkdown` 为空或 `lineStart==0`）时设置 `_pendingScrollRatio=0`，布局完成后滚动到顶部。

### 2.5b 已实现：ComputeSlim extent 稳定化

- 总高按 500px 向上取整：`Ceiling(max(estimated, raw) / 500) * 500`，避免 900750/900800 等微差导致 extent 振荡。
- extent 振荡会触发 ScrollViewer clamping → ScrollChanged → 级联布局（version 57→58→59…）。

### 2.5c 已实现：布局后冷却期

- 布局快照应用后 250ms 内，忽略滚动触发的布局防抖，打断「布局完成 → InvalidateMeasure → ScrollChanged → 再次布局」的级联。

### 2.6 后续可选

- **编辑区高亮**：对超长文档缩小分析窗口或提高防抖间隔。

---

## 三、相关文件

| 文件 | 修改 |
|------|------|
| `RenderEngine.cs` | `FindFirstVisibleBlockIndex` 改为基于 `_cumulativeY` 的二分查找；`GetVisibleBlockRange` 使用二分确定 end |
| `LayoutComputeService.cs` | 新增 `ComputeSlim`：先估计高度，再仅对可见块做完整布局 |
| `LayoutTaskScheduler` / `EngineRenderControl` | 块数 > 500 时调用 `ComputeSlim` 替代 `ComputeFull` |
