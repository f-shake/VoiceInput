# 计划：组合键即时取消 + 热键必须“干净按下” + 录音波形的视觉幅度放大

> 阶段一产物。基线提交 `47d3981`。动机见 §1，追问见 §附（Q1~Q8 全部裁决，无未决项）。

## 1. 功能需求

用户原始反馈（2026-11-18，三句）：

1. “遇到组合键的时候是松开才提示报错的……应该当组合键第二个键按下的时候，就应该提示”。
2. “如果先按了其他的键，再按我们的快捷键的时候，依旧会触发这个语音，这也是不合理的”。
3. “现在不是麦克风说话的时候，中间的东西会闪动嘛？你把这个闪动的幅度再增加 1~2 倍……得说话很大声，才会大幅度跳动”。

| 编号 | 需求 | 验收方式 |
|---|---|---|
| R1 | 录音期间按下任意非热键：**该键按下的同一瞬间**状态条转红并显示 `检测到组合键，已取消本次听写。`，不再等热键松开 | 单测：`HotkeyDown` → `NoteOtherKey` 后立刻断言状态条已是 Failure，且此时 `HotkeyUpAsync` 尚未调用 |
| R2 | 判定为组合键时**立即停止录音**（不再录到松手），状态回到 Idle，本次不产生任何转写请求、不碰剪贴板 | 单测：假录音器观察到取消；转写器调用次数 0；粘贴次数 0 |
| R3 | 热键按下那一刻还按着修饰键（Ctrl / Shift / Alt / Win，例如先按住 Shift / 左 Ctrl 再按右 Ctrl）时**不启动录音**，连“录音中”都不出现；其它键按着不挡（Q9） | 单测：`HotkeyDown(new[]{"LeftCtrl"})` 后录音器调用次数 0、状态条无任何事件、提示音无事件 |
| R4 | 不改变“录音期间按下任何非热键即取消”的**判定口径**（任何键都算，只改时机），也不改文案 | 单测：现有组合键用例改写后仍走同一条文案常量 |
| R5 | 同一次按住热键的期间只提示/只记录一次：热键自动重复不刷屏；组合键取消后即使其它键先松开、热键还按着，也不再启动新录音；最后松开热键不产生第二次提示 | 单测：取消后连续 `HotkeyDown()` 不再启动；状态条失败事件只有 1 条；日志只有 1 条 |
| R6 | 干净按下的完整流程与现在**完全一致**（开始音 → 录音 → 松开 → 转写 → 粘贴 → 成功音） | 现有单测全绿（尤其“一轮完整流程以粘贴成功收尾”） |
| R7 | 热键仍是配置里的单键（右 Ctrl 或 F1~F12），不引入“自定义组合键”能力 | 不改 `HotkeyNames` / `ConfigLoader` |
| R8 | 录音状态下状态条中心的四根竖条**随声音起伏的幅度明显变大**（用户要 ×2 量级），正常音量说话就能看到大幅跳动，且任何音量下都不越出圆 | 单测：`AudioLevel.ToDisplayGain` 在 0 / 0.3 / 0.5 / 0.75 / 1 各点的增益与单调性；手工：对着状态条用平常音量说话 |
| R9 | 状态条显示时，鼠标可以按住它（圆或气泡）拖到屏幕任意位置，松手即生效 | 手工：录音中把它拖到左上角，圆跟着走；录音不中断 |
| R10 | 位置用**屏幕工作区百分比**记住（不是像素），下次启动/下次显示都回到原位；分辨率变化后位置按比例仍然合理 | 单测：`AppConfig` 默认值、`ConfigLoader` 读写往返、越界钳制；手工：拖到右上角 → 退出 → 重启，位置不变 |

## 2. 技术方案

### 2.1 即时取消放在 Core 状态机里

`NoteOtherKey` 从“打标记”改成“当场取消”：

- `Recording` → `Idle`，取出 `_stopRecording` 与 `_recording`，`Cancel()` 取消源；
- 同步投递失败状态与短错误音（Q3 已定：400 Hz / 300 ms），钩子线程上只做投递；
- 录音任务的收尾（等待它真的停下、释放 CTS、吞掉取消异常）交给 `DiscardRecordingAsync`，**不 await**，避免阻塞钩子线程。

随之删除 `_guardTripped` / `_guardKey` 与 `RunPipelineAsync` 的 `guard` 分支——“先标记、松手再判”这条路整条去掉，只留一条路径，避免两份真相。

### 2.2 “干净按下”：钩子记账 + 局部对账（2026-11-18 实测后调整）

非空即判定为组合键、不启动（Q1 已定：**只算还按着的键**，不加时间窗口；Q9 已定：**只认修饰键**，见下文的键名白名单）。

**原方案与它为什么没成立**：最初是“热键按下瞬间扫一遍全部虚拟键”。真机第一次测试就失败——本机 `GetAsyncKeyState(0x61)`（小键盘 1）**恒定返回 0x8000**（三次采样一致，NumLock 本身正常），全表扫描每次都读到一个假按下的键，于是每次按右 Ctrl 都被判成组合键、静默不启动。日志现场：`E14 按下热键时 VK97 还按着，不启动`。

**现方案**：

- `KeyboardHook` 维护 `HashSet<int> _heldDown`：钩子收到真实 keydown 时加入、keyup 时移除。只在钩子线程读写（回调与 `Program.OnKey` 同线程），不加锁。
- `ModifierKeysDownBesides(vk)` 只返回表里除热键自身以外的**修饰键**（Ctrl / Shift / Alt / Win 的通用与左右形式）：真正的组合键都以修饰键打头，字母、数字、小键盘、锁定键按着不该把热键挡掉（Q9 已定）。录音期间的取消规则不受此限——那时任何真实按键都算数。
- 查表前先 `PruneReleasedKeys()`：对表里每个键查一次 `GetAsyncKeyState`，已经松开的清掉。**只核对确实进过表的键**，所以那些假按下的虚拟键永远进不了判定。
- 代价：热键按下时只做“表大小”次 `GetAsyncKeyState` 调用（通常 0~3 次），比全表 247 次更便宜。

被否决的备选：

| 备选 | 否决理由 |
|---|---|
| 扫全表 `GetAsyncKeyState` | 本机实测存在假按下的虚拟键，每次都误判成组合键（见上） |
| 只维护按下集合、不做对账 | 一次漏掉的 keyup（安全桌面 Ctrl+Alt+Del、钩子被系统重装、进程卡顿）就会让热键**永久失效** |
| `RegisterHotKey` 注册系统热键 | 拿不到“松开”事件，按住说话做不了（`KeyboardHook` 类注释已写明） |
| 只检查修饰键（Ctrl/Shift/Alt/Win） | 挡不住“按着 A 再按右 Ctrl”，而用户原话是“其他的键” |

### 2.3 取消后的录音收尾

```csharp
private static async Task DiscardRecordingAsync(Task<Recording>? recording, CancellationTokenSource? stop)
```

- `await recording` 只为了等它真的结束（吞掉取消异常，避免 UnobservedTaskException 噪音），`finally` 里 `stop?.Dispose()`。
- 失败（录音器抛异常）时写一条 `Warn` 日志，不影响已经显示给用户的红叉。

### 2.4 一次按住的去抖标志

`private bool _chordIgnored`（新增，语义是“本次按住热键期间已被判定为组合键”）：

- `NoteOtherKey` 判定取消时置位，`HotkeyDown` 因“其它键按着”忽略时置位；
- 只有 `HotkeyUpAsync` 开头（在状态判断之前）清位，`HotkeyDown` 真正启动录音时也清位；
- 作用：一是挡住长按热键（系统每 ~30 ms 补发 keydown）造成的日志刷屏；二是防止“其它键松开、热键还按着”时被自动重复当成新的干净按下而重新启动录音。

### 2.5 波形幅度放大（R8）

现状（`StatusOverlay.DrawWaveform`）：

```
条高 = iconDiameter × 0.62 × factor × gain
gain = 0.15 + 0.85 × level          factor ∈ {0.34, 0.66, 0.46, 0.78}
```

24 像素图标直径下：单条最高约 `11.6 × gain` 像素。估算正常说话 RMS ≈ −30 dBFS → `level ≈ 0.56` → `gain ≈ 0.63` → 最高条约 **7 像素**；静音底（`gain = 0.15`）约 1.8 像素。圆内可用高度约 21 像素，所以纯等比放大到 ×2 时，满格会到 23 像素越出圆，必须封顶。

已定方案（Q7 = B：约 ×2；Q8 = B：低音区抬升曲线 + 整体乘数）：

- 把“电平 → 显示增益”抽成 Core 里的**纯函数** `AudioLevel.ToDisplayGain(float level)`，与现有 dBFS 换算放在一起、可单测；`DrawWaveform` 只负责几何。
- 增益曲线：`gain = 0.15 + 0.85 × level^0.6`（新增常量 `DisplayGamma = 0.6`）。gamma < 1 把“平常说话”这一段从增益底部抬到中段。
- 几何乘数：`0.62 → 1.0`（新增常量 `BarHeightFactor = 1.0`）。
- 条高封顶 `0.88 × iconDiameter`（24 像素下约 21 像素）：按下面的实测算，最高只到 18.7 像素，封顶是保险而不是日常路径。
- 静音底仍是 `gain = 0.15`，静音条高从 1.7 像素升到 2.8 像素，仍看得出“在录”。

工作量对照（24 像素图标直径、四根条里最高的那根）：

| 场景 | 电平 | 现增益 | 现条高 | 新增益 | 新条高 | 倍数 |
|---|---|---|---|---|---|---|
| 静音 | 0 | 0.150 | 1.7 px | 0.150 | 2.8 px | 1.6× |
| 小声说话 | 0.25 | 0.363 | 4.2 px | 0.520 | 9.7 px | 2.3× |
| 正常说话 | 0.56 | 0.626 | 7.3 px | 0.750 | 14.1 px | 1.9× |
| 大声说话 | 0.90 | 0.915 | 10.6 px | 0.948 | 17.7 px | 1.7× |
| 满格 | 1.0 | 1.000 | 11.6 px | 1.000 | 18.7 px | 1.6× |

（电平 `level` 由 −60~−6 dBFS 线性映射而来；正常说话按 RMS ≈ −30 dBFS 估算。）

被否决的备选：

| 备选 | 否决理由 |
|---|---|
| 只把上限从 −6 dBFS 放宽到 −12 dBFS | 等效只抬了 6 dB，正常说话大约只多 10%，达不到用户要的量级 |
| 单纯整体 ×2、不封顶 | 满格时四根竖条会顶穿圆，视觉上像破图 |
| 把 RMS 换成峰值 | 峰值抖动大，会变成噪点式闪烁；用户抱怨的是“幅度小”而不是“不灵敏” |

### 2.6 状态圆可拖动并记住位置（R9 / R10，2026-11-18 追加）

**现状**：窗口带 `WS_EX_TRANSPARENT`（整窗鼠标穿透），位置由 `LayoutWindow()` 写死成“主屏工作区水平居中、距底部 72 逻辑像素”，命中区域是自定义 `Region`（圆 ∪ 气泡）。

**方案**：

- 去掉 `WS_EX_TRANSPARENT`，让既有的自定义 `Region` 决定命中范围：只有可见的圆与气泡接鼠标，其余透明区域照旧不挡点击。
- 拖动：`OnMouseDown` 记下按下点并捕获鼠标，`OnMouseMove` 移动窗口，`OnMouseUp` 释放并抛出 `Moved` 事件（圆心换算成工作区百分比 0~1）。拖动不改变状态、不取消录音或转写。
- 存储：`config.json` 新增 `"overlay": { "x": 0.5, "y": 0.92 }`；读取时钳到 0.05~0.95，缺省值等价于今天的位置（主屏工作区底部居中）。松手即保存，写盘由 `Program` 负责，`StatusOverlay` 不碰文件。
- 布局：由百分比算圆心 → 圆矩形 → 气泡优先画在圆上方；圆心贴近工作区顶边时气泡**翻到圆下方**；整体钳在工作区内（圆心到边距至少半个直径）。
- 位置基准是**屏幕工作区**（已扣掉任务栏），所以换分辨率、换任务栏高度都按比例走。

被否决的备选：

| 备选 | 否决理由 |
|---|---|
| 保留穿透，用鼠标钩子模拟拖动 | 钩子看不到“点在我们的圆上”，得自己再实现一套命中测试，等于把 WinForms 已经做好的事重写一遍 |
| 只有圆环边缘可拖（中间仍穿透） | 圆只有 31 逻辑像素，边缘环更窄，用户第一反应是拖圆心，拖不动会以为坏了 |
| 托盘菜单里的“移动位置”模式 | 多一步操作，与“看着就能拖”不是一个体验 |

## 3. 系统结构

```
键盘 → KeyboardHook.OnKeyboardMessage（钩子线程，低级键盘钩子）
          │  KeyEvent(Name, VirtualKey, IsDown, Ctrl, Alt)
          ▼
        Program.OnKey（钩子线程，只做分派）
          ├─ Ctrl+Alt+Q → 退出（不变）
          ├─ 设置窗口打开 → 全部忽略（不变）
          ├─ 热键按下 → KeyboardHook.ModifierKeysDownBesides(vk) → VoiceInputRunner.HotkeyDown(modifiers)
          ├─ 热键松开 → VoiceInputRunner.HotkeyUpAsync()
          └─ 其它键按下 → VoiceInputRunner.NoteOtherKey(name)
                                │
                                ▼
                   VoiceInputRunner（Core，状态机 + 提示）
                        ├─ IRecorder.Record（取消）
                        ├─ IStatusView.Show
                        ├─ IFeedback.Play
                        └─ IDiagnosticsLog

麦克风每块音频 → NaudioMicRecorder.Level ──→ StatusOverlay.CurrentLevel（平滑）
                                                   → AudioLevel.ToDisplayGain（新增纯函数）
                                                   → DrawWaveform（几何 + 封顶）
```

无新增项目、无新增依赖、无新增 NuGet 包。

## 4. 数据结构

| 类型/成员 | 变化 | 说明 |
|---|---|---|
| `KeyboardHook` | 新增 `public IReadOnlyList<string> HeldKeysBesides(int virtualKey)` | 返回此刻除该键外还按着的键名（复用 `NameOf`）；内部 `try/catch` 失败时返回空表并走 `_reportFailure` |
| `VoiceInputRunner.HotkeyDown` | 新增重载 `public void HotkeyDown(IReadOnlyCollection<string> otherKeysHeld)`；无参版本保留并转调空表 | 无参重载保证现有单测调用不用改 |
| `VoiceInputRunner` 私有字段 | 删 `_guardTripped`、`_guardKey`；增 `_chordIgnored` | 见 §2.4 |
| `VoiceInputRunner.RunPipelineAsync` | 签名去掉 `bool guard` 参数与对应分支 | |
| `VoiceInputRunner` 私有方法 | 新增 `DiscardRecordingAsync` | 见 §2.3 |
| `AudioLevel` | 新增常量 `DisplayGamma = 0.6` 与 `public static float ToDisplayGain(float level)` | 纯函数：`0.15 + 0.85 × level^DisplayGamma`，输入 0~1 电平，输出 0.15~1 的条高增益 |
| `StatusOverlay` | 新增常量 `BarHeightFactor = 1.0`（原字面量 0.62）；`DrawWaveform` 改用 `ToDisplayGain`；条高封顶 `0.88 × iconDiameter` | 几何与封顶留在 UI 层，倍数决策留在 Core 的纯函数里 |
| `ErrorCatalog.ComboCancelled` | 不动 | 文案仍是 `检测到组合键，已取消本次听写。` |
| `KeyboardHook.KeyEvent` | 不动 | 保持 record struct 原样，避免污染事件契约 |

## 5. 模块划分

- **`VoiceInputRunner`（Core）** 负责：什么时候算组合键、取消时机、提示文案与状态迁移、录音收尾、日志。
  **不负责**：判断物理按键状态（钩子的事）、播放声音的实现、界面绘制。
- **`KeyboardHook`（App.Platform）** 负责：把物理按键变成事件、热键按下时如实报告“还有谁按着”。
  **不负责**：这些键对业务意味着什么（语义留在 Core，便于单测）。
- **`Program.OnKey`（App）** 负责：分派与优先级（退出键、设置窗口、热键、其它键）。
  **不负责**：任何状态判断。
- **`AudioLevel`（Core）** 负责：PCM → 电平、电平 → 显示增益这两段纯计算。
  **不负责**：怎么画（几何、圆角、封顶留给 `StatusOverlay`）。
- **`StatusOverlay`（App.Ui）** 负责：平滑、几何、颜色、文案。**不负责**：决定“放大多少倍”（那是 Core 的纯函数）。
- **`ToneFeedback` / `ErrorCatalog`**：本次不动（`ToneKind.Error` 的 400 Hz 300 ms 已存在）。

## 6. 交互逻辑

| 用户动作 | 状态迁移 | 用户看到/听到 |
|---|---|---|
| 干净按下热键 | Idle → Recording | 开始音 + “录音中” |
| 按住热键时按任意其它键 | Recording → Idle（立即） | 短错误音（400 Hz）+ 失败红叉 + `检测到组合键，已取消本次听写。`；录音当场停 |
| 松开热键（已在 Idle） | 不变 | 无声音、无提示 |
| 松开热键（Recording，正常路径） | Recording → Transcribing → Idle | 停止音 → 转写 → 粘贴 → 成功音（不变） |
| 其它键按着时按下热键 | 不变（不启动） | 无界面变化，只有一行日志（Q2 已定：静默） |
| 转写中再按热键 | 不变 | 仅日志 `E06`（不变） |
| 组合键取消后又按住热键不放 | 不变 | 无 |
| 录音中正常说话 | 不变 | 波形条随音量起伏，幅度约为改造前的 2 倍（R8） |

## 7. 异常情况

| 情况 | 表现 | 文案/日志 |
|---|---|---|
| `GetAsyncKeyState` 扫描抛异常 | 按“无其它键”处理，照常启动录音 | 日志 `WARN`：`E14 按键扫描失败，按干净按下处理：<ex>`；宁可漏判也不让热键失效 |
| 录音任务收尾抛异常 | 不影响已显示的红叉 | 日志 `WARN`：`E14 取消的录音收尾异常：<ex>` |
| 状态条不可用（`SilentStatusView`） | 只有日志与提示音 | 现有降级路径，不改 |
| 麦克风没有回调（无音量） | 波形停在静音底 0.15 | 不变 |
| 设置窗口开着时按这些键 | 全部忽略 | 不变 |

## 8. 边界条件

- **热键自动重复**：长按右 Ctrl 时系统持续补发 keydown；`_state == Recording` 时静默返回（不变），`_chordIgnored` 为真时也静默返回。
- **滚键**：右 Ctrl 与 S 几乎同时按下时，先按下的那次扫描是干净的 → 会出现一次极短的“录音中”闪动，随后被 S 取消（Q4 已定：接受）。
- **取消后继续按住**：其它键先松开、热键仍按着 → 不启动新录音（`_chordIgnored`）。
- **左 Ctrl 当“其它键”**：右 Ctrl 按住时按左 Ctrl 仍判组合键（与现状一致）。
- **非修饰键按着**：字母、数字、小键盘、锁定键按着都不挡热键（Q9：干净按下只认修饰键）；录音期间按下它们仍然照常取消。
- **Ctrl+Alt+Q 兜底退出**：右 Ctrl 是热键时，按 Alt 的那一刻会先触发组合键取消、红叉，然后 Q 退出（Q6 已定：接受）。
- **按住其它键再按热键 → 松开热键 → 干净再按**：第二次正常启动（必须）。
- **`otherKeysHeld` 传 null**：重载里 `ArgumentNullException.ThrowIfNull`；`Program` 永远传 `HeldKeysBesides` 的返回值（非 null）。
- **波形幅度上限**：任何 `level ∈ [0,1]` 下条高都 ≤ 0.88 直径；`level = 0` 时仍有 0.15 的底。

## 9. 修改范围

| 文件 | 改动 |
|---|---|
| `src/VoiceInput.Core/Pipeline/VoiceInputRunner.cs` | 主改动：即时取消、干净按下判定、去抖标志、删除 guard |
| `src/VoiceInput.App/Platform/KeyboardHook.cs` | 新增 `HeldKeysBesides` 与排除规则常量 |
| `src/VoiceInput.App/Program.cs` | `OnKey` 里热键按下改为传入 held keys |
| `src/VoiceInput.Core/Asr/AudioLevel.cs` | 新增 `ToDisplayGain` 纯函数与常量 |
| `src/VoiceInput.App/Ui/StatusOverlay.cs` | `DrawWaveform` 用新增益、几何乘数上调、条高封顶；追加：去掉 `WS_EX_TRANSPARENT`、拖动与 `Moved` 事件、按百分比布局、气泡翻转 |
| `src/VoiceInput.Core/Config/AppConfig.cs` | 追加：`OverlayX` / `OverlayY`（double，0~1，默认 0.5 / 0.92） |
| `src/VoiceInput.Core/Config/ConfigLoader.cs` | 追加：读写 `overlay` 对象，越界钳到 0.05~0.95，非数字回退缺省 |
| `tests/VoiceInput.Core.Tests/ConfigLoaderTests.cs` | 追加：overlay 读写往返、缺省、越界钳制用例 |
| `tests/VoiceInput.Core.Tests/ConfigSaveTests.cs` | 追加：键集合断言加入 `overlay`（现有测试会对新增键失败，必须同步改） |
| `docs/manual-test.md` | 追加：第 47 行“点它没反应”改为“可见形状接鼠标、其余仍穿透”；新增拖动与重启记住位置两条 |
| `tests/VoiceInput.Core.Tests/VoiceInputRunnerTests.cs` | 改写组合键用例、新增干净按下与去抖用例 |
| `tests/VoiceInput.Core.Tests/Fakes/FakeDependencies.cs` | 假录音器增加“停在录音里直到被取消”的能力与取消观察点 |
| `tests/VoiceInput.Core.Tests/AudioLevelTests.cs` | 新增 `ToDisplayGain` 的边界与单调性用例 |
| `docs/manual-test.md` | 第 6 条改写为“按下 C 的瞬间变红”；新增“按住 Shift 再按右 Ctrl 不启动”与“正常音量说话波形明显跳动”两条 |
| `README.md` | 第 85~86 行的组合键说明补上“按下即取消”与“另一键按着时不启动” |
| `PLAN.md` | F14 条目口径更新（松开时丢弃 → 按下即取消），补一条 F26（干净按下），波形增益写进既有状态条条目的验收 |
| `docs/plan-hotkey-chord.md` | 本计划（阶段一产物，不计入实现改动） |

不动：`ConfigLoader` / `AppConfig` / `HotkeyNames`（Q5 已定：默认热键仍是右 Ctrl）、`ToneFeedback`、`ErrorCatalog`、所有 provider 项目。

## 10. 实现步骤

1. 先写失败的单测：`组合键按下瞬间就提示并停录`、`按下热键时另有键按着不启动`、`取消后热键重复按下不再启动`。
2. `VoiceInputRunner`：`NoteOtherKey` 改为即时取消 + `DiscardRecordingAsync`；`HotkeyUpAsync` 去掉 guard、开头清 `_chordIgnored`；`RunPipelineAsync` 去掉 guard 分支。
3. `VoiceInputRunner.HotkeyDown(IReadOnlyCollection<string>)` 重载 + 去抖日志；无参重载转调。
4. `KeyboardHook.HeldKeysBesides` + 排除常量 + 失败兜底。
5. `Program.OnKey` 接线（热键按下传 held keys）。
6. `AudioLevel.ToDisplayGain` + 单测；`StatusOverlay.DrawWaveform` 改用新增益与封顶。
7. `dotnet build src/VoiceInput.App`；`dotnet test tests/VoiceInput.Core.Tests --filter "FullyQualifiedName~VoiceInputRunnerTests|FullyQualifiedName~AudioLevelTests"`（单文件，非全量）。
8. 文档同步：`manual-test.md`、`README.md`、`PLAN.md`。

## 11. 测试方案

单测（xunit）：

| 用例 | 断言 |
|---|---|
| 组合键按下瞬间就提示并停录 | `NoteOtherKey` 返回后即 `LastState == Failure`、`LastText == ComboCancelled`；录音器观察到取消；随后 `HotkeyUpAsync` 不改状态、转写 0 次 |
| 组合键只提示一次 | 状态条里 `Failure` 恰好 1 条，日志恰好 1 条 |
| 按下热键时另有键按着不启动 | `Recorder.Calls == 0`、`Status.Entries` 为空、`Tones.Played` 为空、日志含 `E14` |
| 被忽略后再干净按下能启动 | 第二次 `HotkeyDown(Array.Empty<string>())` 后 `Recorder.Calls == 1`，`HotkeyUpAsync` 正常粘贴 |
| 取消后热键重复按下不再启动 | 取消后连续 `HotkeyDown(Array.Empty<string>())`，`Recorder.Calls` 不变，日志只有 1 条 |
| 干净按下完全不变 | 现有“一轮完整流程”用例保持绿 |
| 显示增益的边界与单调性 | `ToDisplayGain(0) == 0.15`、`ToDisplayGain(1) == 1`；0→1 上单调不减；`ToDisplayGain(0.56) ≈ 0.750`（容差 0.01，旧曲线同点是 0.626）；`ToDisplayGain(0.25) > 0.5`（旧曲线同点 0.363），确认低音区确实被抬起 |

手工验收（`docs/manual-test.md`）：第 6 条（按下 C 的瞬间变红）、新增“按住 Shift 再按右 Ctrl 不启动”、新增“平常音量说话波形明显跳动且不越出圆”、第 18 条（F9 热键仍正常）。

回归：现有 `VoiceInputRunnerTests` 与 `AudioLevelTests` 全部用例必须绿；`--filter` 单文件运行即可（全量单测需单独授权）。

## 附：未决问题

已裁决（2026-11-18，Q1~Q6、Q9~Q12 选 A，Q7、Q8 选 B）：

| 编号 | 问题 | 裁决 |
|---|---|---|
| Q1 | “别的键”是否只算还按着 | 只算还按着 |
| Q2 | 因其它键按着而不启动时是否提示 | 静默，只写日志 |
| Q3 | 组合键取消时播什么音 | 短错误音 400 Hz |
| Q4 | 滚键时“录音中”闪一下 | 接受 |
| Q5 | 默认热键是否改 F9 | 不改，仍右 Ctrl |
| Q6 | Ctrl+Alt+Q 退出前先闪一次提示 | 接受 |
| Q7 | 视觉幅度放大到几倍 | 约 ×2：正常说话的条高从 7.3 像素变成 14.1 像素 |
| Q8 | 用哪种方式放大 | 低音区抬升曲线（gamma 0.6）+ 几何乘数 1.0 + 封顶 0.88 直径 |
| Q9 | “干净按下”的判定用白名单还是黑名单 | 白名单：只认修饰键（Ctrl/Shift/Alt/Win）；录音期间仍任何真实按键都取消 |
| Q10 | 拖动与鼠标穿透怎么取舍 | 圆与气泡可拖，圆外透明区域仍穿透（整窗不再无条件穿透） |
| Q11 | 位置用什么基准、要不要记屏幕 | 主屏工作区百分比（扣掉任务栏），不记屏幕序号，不支持拖到副屏 |
| Q12 | 拖到屏幕顶边时气泡摆哪 | 气泡自动翻到圆下方 |

无未决项。
