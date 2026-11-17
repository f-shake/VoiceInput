# 结果优化：提速、可配置超时、可感知状态

上一轮（结果优化）已上线，实测暴露三件事：优化耗时长、容易超时、失败时看不出来。
本轮只动这些，不碰语音识别链路。

- 状态：初稿（阶段一，待闸门1 确认）
- 上一份计划：`docs/plan-ai-optimize.md`（已完成并提交，不改它）

---

## 0. 事实：为什么慢（本轮先查后改）

用你当前的配置（`https://api.deepseek.com`、模型 `deepseek-flash`、超时 8 秒）真实调用：

| 场景 | 文本长度 | 耗时 | reasoning_tokens |
|---|---|---|---|
| 短句 | 33 字 | 1187–2454 ms | 97–261 |
| 长句 | 68 字 | 2130–2718 ms | 225–401 |
| 同一长句，请求里加 `thinking: {"type":"disabled"}` | 68 字 | **656–1029 ms** | 无 |

结论：**这个端点的 `deepseek-flash` 默认就在思考**（返回里有 `reasoning_content`，
`usage` 里 reasoning_tokens 占了大头），我们从来没关过它。校对这种任务不需要思考，
白白多了 1~2 秒；句子越长思考越多，8 秒超时自然容易踩线。`/models` 本身只要 129 ms，
不是瓶颈。

所以本轮的三条提速手段：默认关掉思考（证据见上）、把超时默认调到 20 秒、超时做成可改。

---

## 0.1 已确认的决策

| # | 决策 | 结论 |
|---|---|---|
| Q1 | 默认关思考 | 默认关（请求带 `thinking: disabled`），配置项 `aiOptimizeThinking` 可打开，设置窗口不加开关 |
| Q2 | 成功时是否标记 | 标记：绿圆显示“已粘贴 N 字（已优化）”；不优化时仍显示“已粘贴 N 字” |
| Q3 | 模型为空切到优化方式 | 自动展开模型下拉并拉取一次 |
| Q4 | 橙色提示 | “已粘贴 N 字（优化失败）”，停留 1.5 秒 |
| Q5 | 图标跳动 | **跟随麦克风实际音量**：波形条高度由每块音频的 RMS 决定，静音时压到最低、说话大时变高，不自行摆动 |

---

## 1. 功能需求

| 编号 | 需求 | 验收方式 |
|---|---|---|
| B1 | 结果优化块新增“超时”输入框：整数秒，范围 1~60，默认 20；保存写回配置，下一次听写生效 | 单测：默认值/范围/写回；手工验收：改 5 秒后长句必然超时回退 |
| B2 | 请求体默认带 `thinking: {"type":"disabled"}` 关闭思考；新增配置 `aiOptimizeThinking`（默认 false，仅文件可改）可在需要时打开 | 单测：请求体含该字段；端到端：长句耗时从 ~2.4 s 降到 ~0.8 s |
| B3 | 取消模型预置：下拉不再内置 `deepseek-chat` / `deepseek-reasoner`，候选只来自 `/models`；配置默认模型为空 | 单测：默认为空、允许空、超 64 字仍报错；手工验收：下拉只显示接口返回的模型 |
| B4 | 录音时圆内波形条高度跟随**麦克风实际音量**：每块音频算 RMS、映射成 0~1，静音时压到最低、说话大声时变高；用来确认麦克风真的在收音 | 单测：RMS→归一化的映射；手工验收：对着麦克风说话条子变高，静音时低平 |
| B5 | 优化失败时状态圆变**橙色**（与失败的红色区分），气泡写“已粘贴 N 字（优化失败）”，1.5 秒消失；优化成功时绿圆写“已粘贴 N 字（已优化）”，不优化时仍写“已粘贴 N 字” | 单测：状态与文案映射；手工验收：断网听写一次看橙色 |
| B6 | 超时行为不变：到点按失败处理、回退原文继续粘贴，日志 E26 带“读取超时 N 秒” | 单测：超时路径；手工验收：日志 |

---

## 2. 技术方案

### 2.1 关思考

`DeepSeekTextRefiner` 的请求体在 `Thinking == false` 时加：

```json
"thinking": { "type": "disabled" }
```

实测该端点接受并生效（reasoning_tokens 消失、耗时降 60%）。风险与对策：个别严格端点
可能拒绝未知字段 → 400 时按 E26 回退原文，日志留 HTTP 码；把 `aiOptimizeThinking`
设成 true 即可退回旧行为。**否决**“换模型解决”：模型列表由端点决定，用户端点上
两个模型都走同一个网关，换哪个都可能带思考。

### 2.2 超时可配

- `AppConfig.AiOptimizeTimeoutSeconds` 默认 8 → **20**。
- `ConfigLoader` 范围保持 1~60（配置手改与 UI 同一口径）。
- 设置窗口用 `NumericUpDown`（1~60，整数），跟着“方式/Key/模型/接口地址”一起进
  “重建优化器”的判断；超时变了就重建，下一次听写生效。

### 2.3 取消预置模型

- `SettingsForm.BuiltInModels` 删除，构造函数不再塞候选；失败回退改成**保持当前列表
  不变**（不再假装“内置两项”）。
- `AppConfig.DefaultAiModel` 删除，`AiOptimizeModel` 默认空串；`ConfigLoader` 允许空串
  （仍限制 ≤64）。
- 方式不是“不优化”但模型为空：`BuildRefiner` 返回一个“一调用就抛 E26”的占位优化器
  （`UnavailableRefiner`，原因写“没有选择模型”），状态条按优化失败显示橙色，日志写明原因。
  设置窗口保存时只提示、不拦截（见 §6.2）。

### 2.4 波形跟随音量

- `NaudioMicRecorder` 在 `DataAvailable` 里对每块 PCM（16 kHz / 16 位 / 单声道）算 RMS，
  用 `VoiceInput.Core/Asr/AudioLevel` 映射成 0~1（-60 dBFS → 0，-6 dBFS → 1，超出截断），
  存进 `volatile float Level`；录音开始与结束都清零。
- `StatusOverlay` 构造时收一个 `Func<float>? level`（由 `Program` 传
  `() => recorder.Level`），33 ms 的定时器每帧取一次：**上升即时、下降按 0.85 衰减**，
  避免条子抖成噪点。
- 条高 = `基准高度 × (0.15 + 0.85 × 平滑后的音量)`；静音时四条压成很矮的横条，
  说话时随音量变高。**不再用固定正弦摆动**：那样看不出麦克风是否真的收到声音。

### 2.5 橙色的“优化失败”

- `OverlayState` 新增 `Warning`（Core 枚举，排在 Success/Failure 之间）。
- `StatusOverlay.StateColor`：`Warning → #E08A00`（橙）；图标画叹号（与红色叉区分）。
- `VoiceInputRunner`：优化失败且**已粘贴**时 `Show(Warning, "已粘贴 N 字（优化失败）")`；
  优化成功时 `Show(Success, "已粘贴 N 字（已优化）")`；选“不优化”时 `Show(Success, "已粘贴 N 字")`。
- 自动隐藏沿用 1.5 秒；`Apply` 里把 Warning 加进自动隐藏分支。

---

## 3. 系统结构

```
SettingsForm ──保存──► AppConfig{aiOptimizeTimeoutSeconds, aiOptimizeThinking, aiOptimizeModel=""}
Program.BuildRefiner ─► RefineOptions{Mode, Model, Timeout, Thinking}
DeepSeekTextRefiner ──POST /chat/completions（thinking.disabled）──► 结果或 E26
NaudioMicRecorder ──每块 PCM 算 RMS──► AudioLevel（-60~-6 dBFS → 0~1）──► volatile Level
StatusOverlay ──33ms 定时器读 Func<float>──► 波形条高度随音量起伏
VoiceInputRunner ──优化失败&&已粘贴──► IStatusView.Show(OverlayState.Warning, "已粘贴 N 字（优化失败）")
StatusOverlay ──橙色圆 + 叹号 + 气泡──► 1.5s 后隐藏
```

---

## 4. 数据结构

| 字段 | 变化 | 默认 | 约束 |
|---|---|---|---|
| `AiOptimizeTimeoutSeconds` | 默认 8 → 20 | 20 | 1~60 |
| `AiOptimizeThinking` | 新增 bool | false | — |
| `AiOptimizeModel` | 默认 `deepseek-chat` → `""` | 空 | ≤64，可为空 |

`RefineOptions` 增加 `bool Thinking`。总字段数 20 → 21。

---

## 5. 模块划分

| 模块 | 负责 | 不负责 |
|---|---|---|
| `DeepSeekTextRefiner`（改） | 请求体带 thinking 开关 | 决定该不该开（配置说了算） |
| `RefineOptions`（改） | 增加 Thinking | 校验 |
| `VoiceInputRunner`（改） | 优化失败时发 Warning 状态与文案 | 画颜色 |
| `StatusOverlay`（改） | 橙色、叹号、按音量画波形 | 判断优化成败、采音频 |
| `AudioLevel`（新） | PCM 的 RMS → 0~1 归一化 | 读设备 |
| `NaudioMicRecorder`（改） | 每块算出音量存 `Level` | 决定怎么画 |
| `SettingsForm`（改） | 超时输入框、空模型提示、取消预置候选 | 发请求 |
| `AppConfig` / `ConfigLoader`（改） | 新字段默认值与校验 | 界面 |

---

## 6. 交互逻辑

1. 设置 → 结果优化：方式选非“不优化”时，如果模型为空就自动展开下拉并拉一次 `/models`；
   拉不到就保留现有候选（初始为空）并弹错误框；超时框填 1~60 的整数。
2. 保存：方式非“不优化”且模型为空 → 弹提示说明“保存后听写会按原文粘贴并显示橙色提示”，
   但**照常保存**（空模型是合法配置，不能因为它丢掉这次改的热键/超时）。
   其余校验同现状。保存后优化器立即重建。
3. 听写：录音 → 圆内波形轻微跳动；松开 → 转写 → 优化（默认不带思考，≤20 秒）。
4. 优化成功 → 绿圆“已粘贴 N 字（已优化）”；优化失败 → **橙圆**“已粘贴 N 字（优化失败）”，
   文字照样粘贴，1.5 秒后消失；选“不优化”时仍是绿圆“已粘贴 N 字”。
5. 超时到点 → 按失败处理，回退原文，日志写“读取超时 N 秒”。

---

## 7. 异常情况

| 情况 | 表现 | 日志 |
|---|---|---|
| 端点不认 `thinking` 字段并返回 400 | 橙圆“已粘贴 N 字（优化失败）”，粘贴原文 | E26 + HTTP 400 |
| 超时 | 同上，气泡同样是橙色失败提示 | E26 + 读取超时 N 秒 |
| 模型为空就选了优化方式 | 保存只提示、不拦截；听写时橙色“已粘贴 N 字（优化失败）”，原因写日志 | E26 + 没有选择模型 |
| `/models` 拉不到且没有候选 | 错误框（现状），模型可手输 | 无 |
| 超时填非法值（0/61） | 数字框本身限制 1~60；手改配置文件仍按 E16 报错 | E16 |

---

## 8. 边界条件

- 超时 1 秒：几乎必超时，仍要正常回退、不能卡住听写。
- 超时 60 秒：状态条“优化中”最长停留 60 秒，期间再按热键按“转写进行中”忽略。
- 模型名 64 字上限不变；空串允许。
- 端点返回仍然带 reasoning（配置里打开了思考）：照常只取 content。
- 麦克风完全静音：波形条压到最低但仍在（证明在录，不证明有声音）。
- 设备打不开：走原有 E02 失败路径，与音量显示无关。
- 只看录音态：转写/成功/失败/警告态波形不绘制。

---

## 9. 修改范围（基线）

修改：
- `src/VoiceInput.Core/Config/AppConfig.cs`（默认值、新字段）
- `src/VoiceInput.Core/Config/ConfigLoader.cs`（读 thinking、允许空模型）
- `src/VoiceInput.Core/Asr/AudioLevel.cs`（新增：RMS→0~1 的映射，可单测）
- `src/VoiceInput.Core/Optimize/RefineOptions.cs`（Thinking）
- `src/VoiceInput.Core/Optimize/DeepSeekTextRefiner.cs`（请求体）
- `src/VoiceInput.Core/Pipeline/VoiceInputRunner.cs`（Warning 状态与文案）
- `src/VoiceInput.Core/Diagnostics/ErrorCatalog.cs`（新增“已粘贴 N 字（优化失败）”文案）
- `src/VoiceInput.App/Platform/NaudioMicRecorder.cs`（每块算音量）
- `src/VoiceInput.App/Ui/SettingsForm.cs`（超时框、取消预置、空模型提示）
- `src/VoiceInput.App/Ui/StatusOverlay.cs`（橙色、叹号、按音量画波形）
- `src/VoiceInput.App/Program.cs`（传 Thinking 与音量回调；超时变更重建）
- `tests/.../ConfigLoaderTests.cs`、`ConfigSaveTests.cs`、`DeepSeekTextRefinerTests.cs`、
  `VoiceInputRunnerTests.cs`、`ErrorCatalogTests.cs`、`AudioLevelTests.cs`（新增）
- `config.example.json`、`README.md`、`docs/manual-test.md`

明确不改：转写、粘贴、插件加载、托盘、单实例。录音只加“算音量”，采集参数与 WAV 封装不动。

---

## 10. 实现步骤

1. 配置层：默认 20、新增 thinking、允许空模型 → 配置测试。
2. RefineOptions + 请求体 thinking → 优化器测试。
3. 超时框 + 空模型校验 + 取消预置 → 设置窗口。
4. OverlayState.Warning + 文案 + 颜色/叹号 → 状态条；管线接 Warning。
5. 音量：`AudioLevel` + 录音器 `Level` + 状态条按音量画波形 → 映射单测。
6. Program 传参、重建判断。
7. 文档与手工验收清单。
8. 自测：构建 + 相关测试类。

---

## 11. 测试方案

| 维度 | 用例 |
|---|---|
| 配置 | 超时默认 20；thinking 默认 false；模型默认空；空模型可读；超 64 仍报错；写回往返 |
| 优化器 | 默认请求体含 `"thinking":{"type":"disabled"}`；Thinking=true 时不含；其余既有用例保持 |
| 管线 | 优化失败→Warning 且文案含“优化失败”；优化成功→Success；不优化→Success 无标记；超时→回退原文 |
| 音量 | 全 0（静音）→0；满幅正弦→接近 1；低于 -60 dBFS→0；空缓冲→0；超过 -6 dBFS 截到 1 |
| 文案 | 新文案进 AllMessages，中文、无 emoji、无「」 |
| 手工 | 图标跳动、橙/红区分、超时框范围、模型下拉只显示接口结果 |

---

## 附：未决问题

无。5 项决策已在 §0.1 确认，追问闭环。执行中若出现“做什么”级别的分歧，回闸门1 重新确认。
