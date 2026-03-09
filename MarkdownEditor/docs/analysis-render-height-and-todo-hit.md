# 渲染区高度与 Todo 点击偏差 — 原因分析（已由块级增量布局替代）

> **说明**：本文描述的问题已通过「块级增量布局」方案从根本上解决，总高由各块实际高度聚合，不再存在估计/实际切换。详见 [design-incremental-block-layout.md](design-incremental-block-layout.md)。

## 现象

- 处理 todolist 并有一定编辑后，**右侧渲染区高度**与 **todolist 鼠标点击响应**出现偏差。
- 与「单字符变更 vs 多行变更」无直接关系。

## 数据流简述

1. **控件高度（ScrollViewer Extent）**  
   `EngineRenderControl.MeasureOverride` 使用 `engine.MeasureTotalHeight(doc)` 作为内容高度，ScrollViewer 据此得到 Extent 和滚动范围。

2. **MeasureTotalHeight 的返回值**（RenderEngine.cs）  
   - 若 `_layoutWindow.start < 0` 且已有 `_cachedLayouts`：返回 **`_cachedTotalHeight`**（全量布局得到的**实际**总高）。  
   - 否则：返回 **`_cachedEstimatedTotalHeight`**（按块行数估算，如 `lineCount * 24` 的**估计**总高）。

3. **谁在改 _cachedTotalHeight**  
   - **EnsureLayoutFull**（全量布局）：末尾有  
     `_cachedTotalHeight = y + _config.ExtraBottomPadding`  
     即写入**实际**总高。  
   - **EnsureLayoutIncremental**（增量布局）：末尾有  
     `_cachedTotalHeight = _cachedEstimatedTotalHeight`  
     即每次增量布局都把总高**改回估计值**。

4. **谁触发哪种布局**  
   - **Render**：`EnsureLayout(doc, scrollY, viewportHeight)` → 有 scroll/viewport → 走**增量**布局。  
   - **HitTest**（如点击 todo）：`EnsureLayout(doc)` 无参数 → 走**全量**布局。

## 根本原因

- **总高度在「估计」与「实际」之间被反复改写**，且与「只绘制 / 做命中测试」强相关：
  - 平时**只绘制**时用增量布局，并把 `_cachedTotalHeight` 设为**估计**。
  - 一旦发生**点击**（HitTest），会做一次全量布局，把 `_cachedTotalHeight` 设为**实际**。
  - 下次再**绘制**时若又做增量布局，会再次执行 `_cachedTotalHeight = _cachedEstimatedTotalHeight`，把总高**改回估计**。
- 估计高度与真实排版高度不一致（列表、代码块、空行等与 `lineCount * 24` 有差异），因此：
  - ScrollViewer 的 Extent 会在「估计」与「实际」之间变化 → **渲染区高度看起来在变、滚动条位置被重新 clamp** → 表现为**高度偏差和滚动跳动**。
  - 若再叠加滚动位置、命中测试使用的布局与当前绘制不一致等情况，会出现**点击位置和实际响应对不上的偏差**（例如点第一个却反应在最后一个）。

结论：**不是「单字符 vs 行变更」的逻辑问题，而是「总高度在估计/实际之间来回切换」导致的布局与滚动不稳定，进而带来高度偏差和 todo 点击响应偏差。**

## 修复方向（建议）

1. **统一总高语义，避免被增量布局覆盖**  
   - 仅在 **EnsureLayoutFull** 中写入 `_cachedTotalHeight`（实际总高）。  
   - 在 **EnsureLayoutIncremental** 中**删除**  
     `_cachedTotalHeight = _cachedEstimatedTotalHeight`，  
     这样增量布局不再用估计值覆盖已有的实际总高。

2. **MeasureTotalHeight 的返回逻辑**  
   - 当尚未做过全量布局时（例如 `_cachedTotalHeight == 0` 或从未执行过 EnsureLayoutFull），应返回 `_cachedEstimatedTotalHeight`，以便初始测量有合理高度。  
   - 一旦做过全量布局（`_cachedTotalHeight > 0`），则始终返回 `_cachedTotalHeight`，保证 ScrollViewer 的 Extent 与真实排版一致且稳定。

3. **效果**  
   - 全量布局后（例如第一次 HitTest 或导出等），总高固定为实际值，不再被增量布局改回估计值。  
   - 渲染区高度和滚动条不再在「估计/实际」之间来回跳，todo 点击与视觉位置的一致性会恢复。

（是否在首次 Measure 时主动做一次全量布局以「一开始就用实际高度」，属于性能与体验的权衡，可在上述稳定语义基础上再考虑。）
