# 类 Chromium 的块级 DOM 式渲染

## 目标

- **各 block 只负责自己的布局与绘制**，通过统一接口调度，便于扩展、稳定和效率。
- **布局**：每个块类型实现 `IBlockLayouter`，在统一 `ILayoutEnvironment` 下完成测量与换行。
- **绘制**：每个块类型通过 `IBlockPainter` 先绘制块级装饰（背景/边框），再统一绘制行与 Run。

## 与 Chromium 的对应关系

| Chromium/Blink        | 本引擎                         |
|-----------------------|--------------------------------|
| RenderObject::layout()| `IBlockLayouter.Layout(node, ctx)` |
| RenderObject::paint() | `IBlockPainter.Paint(block, ctx)` |
| LayoutBox / 字体/测量 | `ILayoutEnvironment`           |
| PaintContext          | `BlockPaintContext`（画布 + DrawRun/DrawBlockBackground 回调） |

## 统一接口

### 布局侧

- **ILayoutEnvironment**：提供字号、字体、测量画笔、换行、公式/图片测量等，由 `SkiaLayoutEngine` 实现，供各 Layouter 使用。
- **BlockLayoutContext**：单次布局入参，包含 `Width`、`BlockIndex`、`StartLine`/`EndLine` 和 `Environment`。
- **IBlockLayouter**：`Matches(node)` 判断是否处理该节点；`Layout(node, ctx)` 返回一个 `LayoutBlock`（仅尺寸，全局位置由 RenderEngine 设置）。

### 绘制侧

- **BlockPaintContext**：提供 `Canvas`、`Scale`、`Block`、`Selection`，以及由 SkiaRenderer 注入的 `DrawRun`、`DrawSelectionForLine`、`DrawBlockBackground`。
- **IBlockPainter**：`Matches(BlockKind)` 判断是否处理该块种类；`Paint(block, ctx)` 内先调用 `ctx.DrawBlockBackground()`，再调用 `ctx.DrawBlockContent()`（内部按行/按 Run 调用 `DrawRun` 与 `DrawSelectionForLine`）。

## 扩展方式

1. **新增块类型布局**：实现 `IBlockLayouter`，在 `BlockLayouterRegistry.CreateDefault` 中注册；若仍走原有逻辑，可保留在 `SkiaLayoutEngine.LayoutWithSwitch` 中。
2. **新增块样式/装饰**：实现 `IBlockPainter`（或扩展 `DefaultBlockPainter`），在 `BlockPainterRegistry.CreateDefault` 中注册；块级装饰在 SkiaRenderer 的 `DrawBlockBackground` 中按 `Block.Kind` 分支即可。

## 效率与稳定性

- **布局**：仅依赖 `ILayoutEnvironment` 的测量与换行，无全局可变状态，块间互不干扰。
- **绘制**：块内按行/按 Run 绘制，Run 级绘制逻辑集中在 SkiaRenderer，避免重复；可按需做按块/按视口的裁剪与跳过。
- **缓存**：现有块级缓存（BlockCacheEntry、CachedLayout）不变，每个块的布局结果仍由对应 Layouter 产出，便于增量与局部失效。

## 渲染区的增量/差异更新（已有）

- **增量布局**：`EnsureLayout(doc, scrollY, viewportHeight)` 时只对**视口内块**（含前后约 800px 边距）做完整布局（产出 Lines/Runs），其余块仅保留高度用于滚动与总高；全量布局仅在 HitTest、导出等需要完整布局时触发。文档变更后按块内容哈希复用未变块的高度，仅对可见窗口内的块重新做完整布局。
- **按块渲染**：`Render` 只绘制 `GetVisibleBlockRange` 得到的可见块区间，不绘制屏幕外块。
- **尚未做的**：无像素级或区域级差异重绘（仍整块重画可见区域），也未做“仅重绘脏矩形”的优化。
