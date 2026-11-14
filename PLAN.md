# VoiceInput（C# / WinForms 版）开发计划

按住右 Ctrl 说话，松开后把 DeepSeek 云端 ASR 的转写结果粘贴到当前窗口；听写全程在屏幕右下角有一条状态条。

- 状态：初稿（阶段一，待闸门1 确认）
- 本计划**取代** Python 版计划。Python 版整体复制为 `..\VoiceInput-python\` 作行为参照，不删除。
- 决策来源：WinForms（用户指定）；其余沿用 Python 版已验证的行为语义，差异处在本计划逐条写明。

---

## 1. 功能需求

每条都挂在一条自动化测试或一条手工验收项上。

| 编号 | 需求 | 验收方式 |
|---|---|---|
| F1 | 按住右 Ctrl 期间持续采集；松开后 500 毫秒内发起上传 | 状态机单测（假录音器）+ 日志时间戳；手工验收 |
| F2 | 松开时有效录音短于 0.3 秒：不发请求，状态条提示后回落 | 单测：假录音器返回 0.2 秒 |
| F3 | 单次录音上限 118 秒，到上限自动停止、按已录内容转写并提示已截断 | 单测：假录音器返回 truncated |
| F4 | 转写文本非空：备份剪贴板（文本**与图片**）→ 写入文本 → 发送 Ctrl+V → 还原 | 单测：假后端断言调用序列 |
| F5 | 转写文本为空或纯空白：不触碰剪贴板，状态条提示 | 单测：空串、空格、换行三种 |
| F6 | 401/400/413/408 不重试；429/5xx/连接错误按配置重试并退避 | 单测：假 HttpMessageHandler 断言次数与等待 |
| F7 | 每次转写写日志：request_id、服务端 duration_ms、字数、端到端毫秒 | 单测：假日志捕获器 |
| F8 | 转写进行中再次按下热键：忽略并写日志，不打断在途请求 | 单测：状态停在 Transcribing 时再按 |
| F9 | 三个来源都没有 API Key：启动即弹窗提示并退出，不绑定热键 | 单测：空配置 + 空环境 + 空回退读取器 |
| F10 | 采集固定 16k、单声道、16 位，编码为 WAV；设备拒绝 16k 时按 E02 提示换设备（不做重采样） | 单测：WAV 头字段；手工验收：真机 16k 采集 |
| F11 | 配置缺字段用默认值；未知字段写日志告警后继续启动 | 单测：只写一个字段、含拼错字段 |
| F12 | 全部面向用户的文案为中文、无 emoji、无「」 | 单测：遍历文案表逐条断言 |
| F13 | ~~命令行入口~~ **本轮未实现并已裁掉**：程序以托盘常驻启动，没有控制台入口。设备与日志改由设置窗口和日志文件表达 | 不参与验收 |
| F14 | 录音期间按下除热键外的任何键：松开时整段丢弃，不产生请求 | 单测：录音态注入其它按键事件 |
| F15 | 粘贴目标包含终端：统一发 Ctrl+V，失败按 E11 兜底 | 单测：粘贴失败路径；手工验收：终端里听写一次 |
| F16 | 状态圆：待命不可见；录音时显示蓝底白色波形并跑进度环；转写中三点循环；成功变绿打勾 1.5 秒回落；失败变红打叉，圆正上方挂一条说明气泡 1.5 秒回落。圆直径 31 逻辑像素，图标固定 24 逻辑像素不随圆缩放；水平居中，底边距工作区底部 72 逻辑像素 | 单测：状态到文案与颜色的映射、回落时长；手工验收：观察 |
| F17 | 托盘图标常驻：右键菜单含「设置」与「退出」；双击弹气泡显示热键。托盘不可用时自动启用 Ctrl+Alt+Q 兜底退出，保证程序总能被关掉 | 手工验收 |
| F18 | 单实例：已有实例在跑时，第二个实例提示后用非零码退出 | 单测：模拟互斥量已占用 |
| F19 | 日志写 `%LOCALAPPDATA%\VoiceInput\voiceinput.log`；超过 2 MB 时轮转为 `.1`，只留一份历史 | 单测：轮转逻辑用临时目录验证 |
| F20 | 悬浮窗创建失败时降级为仅提示音加日志，不阻断听写 | 单测：假 UI 抛出，断言流程继续 |
| F21 | 设置窗口：托盘菜单打开；可改热键、提示音开关、API Key 三项；保存前校验，非法值不落盘；保存写入 `%LOCALAPPDATA%\VoiceInput\config.json`；热键与提示音立即生效，API Key 下次请求生效。窗口内只读显示当前 Key 来源，避免“填了却不生效”的误解 | 单测：配置写回与合并、非法值拒绝；手工验收：改热键后立即按键验证 |
| F22 | 识别引擎插件化：每个后端编成一个 DLL，放在 `providers` 目录由宿主发现；配置用 `provider` 选引擎、`providerOptions` 传厂商参数；DeepSeek 自己也是这样的 DLL | 单测：配置读写与标识校验；手工验收：日志出现“共发现 N 个识别引擎” |
| F23 | 开机自启开关（当前用户）：写 `HKCU\...\Run`，只影响本用户，不需要管理员；程序被移动后视为未启用 | 单测：注册表值的格式化与匹配 |
| F24 | 本地离线引擎 SenseVoice（sherpa-onnx + ONNX 模型）：不依赖 Python、不依赖 Audio2Text、不联网；模型只在首次使用时加载 | 单测：WAV 解析与参数解析；手工验收：切到该引擎听写一次 |
| F25 | 状态圆显式置顶：不依赖 `Form.TopMost` / `Form.Opacity`，每次显示时直接设置 `TOPMOST`、`LAYERED`、`NOACTIVATE`、`TRANSPARENT` 扩展样式 | 手工验收：它必须压在前台窗口之上 |

明确**不做**：单文件发布、本地模型（引擎位留着，实现以后加）、标点润色、历史记录、录音波形、鼠标点击穿透切换、多显示器位置配置、录音中途取消、自定义图标文件（用嵌入 ico）、设置窗口里放全部配置字段。

---

## 2. 技术方案（含选型理由与否决方案）

### 2.1 运行时与目标框架

`net10.0-windows`，SDK 10.0.401，WindowsDesktop 运行时 10.0.12 已装。仓库里 `TinySyncNote` 也用 net10.0，与既有约定一致。

### 2.2 UI 框架：WinForms

- **选它**：无边框、置顶、不抢焦点、鼠标穿透这四件事在 WinForms 里是常规操作（重写 `CreateParams` 加扩展样式）；`NotifyIcon`、`Clipboard`、`Timer` 都自带；工具链本机齐全。
- **否决继续用 tkinter**：Tk 的 mainloop 会占住主线程，热键回调必须改成队列轮询；且穿透与不抢焦点仍要 ctypes 绕过，等于用更难的方式做同一件事。
- **否决 Avalonia**：它的价值在跨平台；本项目只在 Windows 上跑，换来的只是额外依赖、更大的发布体积和一套 XAML。
- **否决 WPF**：为一条 200 像素宽的状态条引入 XAML 与另一套渲染栈不划算。

### 2.3 录音：NAudio 2.2.1 的 `WaveInEvent`

- 以 16k、单声道、16 位打开，WinMM 侧会替我们向驱动协商，绝大多数设备都接受。
- 采到的数据用 `WaveFileWriter` 直接写进 `MemoryStream`，拿到的就是可上传的 WAV 字节。
- **否决 `WasapiCapture`**：共享模式下 WASAPI 要求设备原生采样率，会逼出一整套重采样代码。
- **否决本轮自己写重采样**：上一版在 Python 里写过，但真机 16k 采集是通的，没有代码路径会走到它；留着就是没有测试覆盖的死代码。设备不接受 16k 时按 E02 明确报错，比悄悄重采样更容易排查。
- **否决纯 P/Invoke 实现 WASAPI**：约六百行 COM 互操作，换不来任何功能。

### 2.4 热键：`WH_KEYBOARD_LL` 低级键盘钩子

- 按住说话必须有**松开**事件，`RegisterHotKey` 只给按下通知，做不出来。
- 钩子回调运行在安装它的线程的消息泵里，不能阻塞；回调里只做判定并投递事件，耗时工作交给后台任务。
- 委托实例必须存字段持有，否则被 GC 回收后钩子静默失效。

### 2.5 文字落地：`Clipboard` + `SendInput`

- 备份范围扩到文本与图片：`Clipboard.GetDataObject()` 克隆后再写入，粘贴完成后原样放回。这是相对 Python 版的实质改进（pyperclip 只能读文本，图片覆盖后救不回来）。
- **否决逐字符 `SendInput` 注入 Unicode**：中文会被拆成按码点的输入节奏，部分应用还会拦截。

### 2.6 网络：`HttpClient` 单例 + 手写重试

- 一个 `HttpClient` 复用连接；multipart 用 `MultipartFormDataContent`。
- 重试就是三次状态判断，**否决引入 Polly**：为一个循环加一个包不划算。
- 超时用 `CancellationTokenSource`，连接与读取分别按配置取值。

### 2.7 悬浮条：`Form` + 扩展样式

- `FormBorderStyle=None`、`TopMost`、`ShowInTaskbar=false`、`StartPosition=Manual`。
- 重写 `CreateParams`：`WS_EX_NOACTIVATE`（不抢焦点）、`WS_EX_TRANSPARENT`（鼠标穿透）、`WS_EX_TOOLWINDOW`（不进 Alt+Tab）。
- 重写 `ShowWithoutActivation` 返回 true，避免首次显示时把光标从目标窗口抢走。
- 圆角用 DWM 的窗口圆角属性（Win11）；取不到就维持直角，不阻断。

### 2.7.1 托盘：`NotifyIcon` + `ContextMenuStrip`

- 托盘图标是唯一的常驻可见物，也是唯一可靠的退出入口；悬浮条鼠标穿透，点不到它。
- 图标来自用户提供的 SVG（`语音识别.svg`）：构建前用 Edge 无头模式栅格化，做成 16/20/24/32/40/48/64/128/256 九个尺寸的 `Resources\voiceinput.ico`，作为嵌入资源编译进程序。托盘按 `SystemInformation.SmallIconSize` 取原生尺寸，不再放大，也不依赖系统字体字形。读不到资源时退回 `SystemIcons.Application`，不阻断启动。
- **资源管理器重启后托盘图标会消失**，这是 Windows 的固定行为：注册 `RegisterWindowMessage("TaskbarCreated")` 并在收到时重新 `Visible = true`。
- 菜单只有一项：退出。菜单在 UI 线程弹，退出走与窗口关闭同一条路径。

### 2.8 分层：`Core` 与 `App` 两个工程

- `VoiceInput.Core`：配置、ASR 客户端、文本归一、错误文案、日志、状态机；不引用 WinForms。
- `VoiceInput.App`：WinForms 悬浮条、键盘钩子、NAudio 录音、剪贴板注入、提示音、组装。
- **理由**：状态机与协议逻辑必须在没有 UI、没有麦克风的环境下可测；单工程会把它们和 `Form` 绑死。
- **否决单工程**：省一个 csproj，换来状态机只能在真机上手测。

### 2.9 测试：xUnit

- `dotnet test` 按测试类过滤运行；不跑整个解决方案。

### 2.10 依赖清单

| 包 | 版本 | 用途 |
|---|---|---|
| NAudio | 2.2.1 | 采集、WAV 写出、提示音生成 |
| xunit | 2.9.2 | 测试框架 |
| xunit.runner.visualstudio | 2.8.2 | 测试运行器 |
| Microsoft.NET.Test.Sdk | 17.11.1 | 测试宿主 |

版本在实现第 1 步按 restore 结果锁定；若 2.2.1 与 net10.0 不兼容，升到可用的最新稳定版并在本表更新。

---

## 3. 系统结构

```
        ┌───────────────────────── VoiceInput.App（WinExe）─────────────────────────┐
        │  Program.cs  单实例 Mutex、配置加载、Key 解析、组装、Application.Run       │
        │                                                                            │
        │  KeyboardHook ──按键事件──▶ RunnerContext ──更新──▶ StatusOverlay          │
        │  (WH_KEYBOARD_LL)            (UI 线程 marshal)      (Form + 扩展样式)      │
        │                                  │                                         │
        │  NaudioMicRecorder ◀──调用───────┤                                         │
        │  ClipboardPaster   ◀──调用───────┤                                         │
        │  ToneFeedback      ◀──调用───────┘                                         │
        └──────────────────────────────────┼─────────────────────────────────────────┘
                                           │ 依赖注入（接口在 Core）
        ┌──────────────────────── VoiceInput.Core（net10.0，无 UI）───────────────────┐
        │  AppConfig / ConfigLoader / ApiKeyResolver                                 │
        │  AsrClient（HttpClient + 重试 + 状态映射）                                  │
        │  TextNormalizer / ErrorCatalog / RotatingFileLog                           │
        │  VoiceInputRunner（状态机）── 依赖 IRecorder / ITranscriber / IPaster /     │
        │                              IStatusView / IFeedback                        │
        └────────────────────────────────────────────────────────────────────────────┘
```

依赖方向单向：App → Core。Core 里没有任何 WinForms 类型，接口定义在 Core，实现全部在 App。

---

## 4. 数据结构

### 4.1 配置（`config.json`，12 个字段，全部可选）

| 字段 | 类型 | 默认 | 约束 |
|---|---|---|---|
| `apiKey` | string | 空 | 允许被环境变量覆盖；启动时必须非空 |
| `hotkey` | string | `"RightCtrl"` | 目前只接受右 Ctrl 与 F 系键的别名表 |
| `deviceNumber` | int? | null | null 表示系统默认输入设备；负数非法 |
| `maxSeconds` | int | 118 | 1..118 |
| `minSeconds` | double | 0.3 | 0..10，且小于 `maxSeconds` |
| `connectTimeoutSeconds` | double | 5.0 | 0.5..30 |
| `readTimeoutSeconds` | double | 60.0 | 5..120 |
| `retries` | int | 2 | 0..5 |
| `retryBackoffSeconds` | double[] | `[0.5, 1.5]` | 每项 0..10，长度不小于 `retries` |
| `beep` | bool | true | — |
| `pasteDelayMilliseconds` | int | 80 | 0..1000 |
| `restoreClipboard` | bool | true | — |

字段名用 camelCase，与 C# 命名习惯对齐后再落 JSON；`baseUrl` 固定常量（`https://api.deepseek.com`），不进配置——只服务一个后端时留个开关只会多一条出错的路径。

配置文件查找顺序：`VOICEINPUT_CONFIG` 环境变量指向的路径 → exe 同目录 `config.json` → `%LOCALAPPDATA%\VoiceInput\config.json`。实际使用哪个路径写进日志首行。

API Key 查找顺序：`VOICEINPUT_API_KEY` → `DEEPSEEK_API_KEY`（进程环境变量）→ 同名用户级环境变量（注册表 `HKCU\Environment`）→ 配置文件。日志只记来源，不记值。

### 4.2 运行状态

```csharp
enum RunnerState { Idle, Recording, Transcribing }
enum OverlayState { Hidden, Recording, Transcribing, Success, Failure }
```

### 4.3 转写结果

```csharp
readonly record struct AsrResult(string Text, int DurationMs, string RequestId, double ElapsedSeconds);
```

### 4.4 异常

```csharp
sealed class VoiceInputException : Exception
{
    string Code { get; }        // E01..E25
    string UserMessage { get; } // 面向用户的完整文案
    bool Retryable { get; }
}
```

### 4.5 录音结果

```csharp
readonly record struct Recording(byte[] WavBytes, double DurationSeconds, bool Truncated);
```

---

## 5. 模块划分

| 模块 | 负责 | 不负责 |
|---|---|---|
| `Core/Config/ConfigLoader` | 读 JSON、补默认、校验范围、未知字段告警、按顺序找配置文件 | 不创建目录、不写配置、不碰注册表 |
| `Core/Config/ApiKeyResolver` | 按优先级找 Key，返回 Key 与来源描述 | 不记日志、不缓存、不写回配置 |
| `Core/Asr/AsrClient` | 组 multipart、发请求、超时、重试退避、状态码到错误码的映射、解析响应 | 不录音、不判断文本值不值得粘贴 |
| `Core/Text/TextNormalizer` | 去控制字符、折空白、换行压空格、判空 | 不加标点、不调模型 |
| `Core/Diagnostics/ErrorCatalog` | 全部用户文案常量与拼装函数 | 不打日志、不决定重试 |
| `Core/Diagnostics/RotatingFileLog` | 按大小轮转写文件、带时间戳 | 不做业务判断 |
| `Core/Pipeline/VoiceInputRunner` | 状态迁移、组合键守卫、时长判定、调用顺序、把状态推给 `IStatusView` | 不实现任何具体 IO |
| `App/Platform/KeyboardHook` | 装钩子、识别右 Ctrl 与兜底的 Ctrl+Alt+Q、组合键记录 | 不判断该不该录 |
| `App/Platform/NaudioMicRecorder` | 打开 16k 采集、按块累积、上限截断、产出 WAV | 不判断录音太短 |
| `App/Platform/ClipboardPaster` | 备份文本与图片、写文本、发 Ctrl+V、还原 | 不加工文本 |
| `App/Platform/ToneFeedback` | 三种提示音（正弦短音） | 不做状态判断 |
| `App/Ui/StatusOverlay` | 无边框置顶穿透窗口、状态到文字的映射、1.5 秒回落计时 | 不做业务判断 |
| `App/Ui/TrayIcon` | 托盘图标与菜单、运行时绘制图标、资源管理器重启后重挂 | 不做业务判断，不直接退出进程 |
| `App/Ui/SettingsForm` | 三项设置的窗口、校验提示、保存后通知装配层应用 | 不直接写文件（交给 `ConfigLoader`）、不改运行中状态 |
| `App/Program` | 单实例、装配、UI 线程 marshal、退出 | 不含业务规则 |

---

## 6. 交互逻辑

1. 启动：取单实例锁（占用则提示 E21 并退出）→ 找配置文件 → 校验 → 找 Key → 发现插件 → 仅当选中的引擎需要 Key 且没有 Key 时弹窗 E01 退出→ 初始化日志 → 建悬浮条（失败则降级，记 E22）→ 装键盘钩子 → 写“就绪”。
2. 按下右 Ctrl：状态 Idle→Recording；悬浮条显示 `录音中 0.0s` 并开始 100 毫秒计时；播放开始音。
3. 录音中：每块追加；到 `maxSeconds` 自动停止并置截断标记（F3）；期间按下任何其它键置守卫标记（F14）。
4. 松开右 Ctrl：Recording→Transcribing；停止采集；播放结束音；吐 WAV 与时长。
5. 守卫命中：丢弃整段，状态条显示 `检测到组合键，已取消本次听写`，回 Idle（不发请求）。
6. 时长判定：小于 `minSeconds` 则走 E03，回 Idle。
7. 上传：调 `AsrClient`；成功得到 `AsrResult`。
8. 文本判空：空则 E09 提示，回 Idle，不碰剪贴板。
9. 粘贴：备份 → 写文本 → 等 `pasteDelayMilliseconds` → `SendInput` Ctrl+V → 等 150 毫秒 → 还原 → 成功音。
10. 状态条：成功显示 `已粘贴 N 字`，失败显示错误摘要（超过 18 字截断），1.5 秒后隐藏。
11. 失败路径一律写日志（含 request_id），状态条只给一句人话，详细原因在日志里。
12. 退出：托盘菜单退出（托盘不可用时为 Ctrl+Alt+Q）；卸钩子、关采集、落日志。

---

## 7. 异常情况（含提示文案）

| 码 | 触发 | 重试 | 状态条文案 |
|---|---|---|---|
| E01 | 三个来源都没有 Key | 否 | 未配置 API Key，请检查 config.json。 |
| E02 | 麦克风打不开或拒绝 16k | 否 | 无法打开麦克风，请在日志中查看设备列表。 |
| E03 | 录音短于 `minSeconds` | 不上传 | 录音过短，已忽略。 |
| E04 | HTTP 401 | 否 | API Key 无效或已失效。 |
| E05 | 连接失败、DNS 失败、读超时 | 是 | 网络请求失败，已重试 N 次。 |
| E06 | HTTP 429 | 是 | 请求过于频繁，请稍后再试。 |
| E07 | HTTP 5xx | 是 | 服务暂时不可用，已重试 N 次。 |
| E08 | HTTP 400 / 408 / 413 | 否 | 音频不被接受。 |
| E09 | 响应无法解析，或文本为空 | 否 | 没有识别到语音内容。 |
| E10 | 写剪贴板失败（重试三次后） | 否 | 无法写入剪贴板，结果已写入日志。 |
| E11 | 发送粘贴键失败 | 否 | 已复制到剪贴板，请手动按 Ctrl+V。 |
| E12 | 转写中再次按下热键 | 不适用 | 正在转写，请稍候。 |
| E13 | 达到 `maxSeconds` 上限 | 不适用 | 录音超过 118 秒，已按已录内容转写。 |
| E14 | 录音期间按下其它键 | 不适用 | 检测到组合键，已取消本次听写。 |
| E15 | 配置文件 JSON 语法错误 | 否 | 配置文件无法解析，请检查 config.json。 |
| E16 | 配置项越界或类型不符 | 否 | 配置项 <名称> 不合法。 |
| E17 | 剪贴板原有图片还原失败 | 否 | 原剪贴板内容未能还原。 |
| E18 | 采集中途出错或取不到数据 | 否 | 录音中断，本次听写已丢弃。 |
| E20 | 未预期的异常 | 否 | 发生未预期的错误，详见日志。 |
| E21 | 已有实例在运行 | 否 | 已有一个 VoiceInput 在运行。 |
| E22 | 悬浮条创建失败 | 不适用 | 仅提示音与日志可用。 |
| E23 | 托盘创建失败 | 不适用 | 已改用 Ctrl+Alt+Q 退出。 |
| E24 | 配置文件存在但读不出来（被占用、权限变化） | 否 | 无法读取配置文件，请检查文件是否被占用。 |
| E25 | 本地引擎的模型或 tokens 缺失 | 否 | 本地识别模型缺失或不完整，请检查模型目录。 |

约定：状态条文本为纯中文、无 emoji、无「」；状态条超过 18 个字符截断，完整文案写日志。

---

## 8. 边界条件

| 场景 | 行为 |
|---|---|
| 空录音／立即松开 | E03，不发请求 |
| 恰好 118 秒 | 正常提交，属成功路径 |
| 音频 8 MiB 上限 | 16k 单声道 16 位下 118 秒约 3.8 MB，永不触达 |
| 转写文本超长（5000 字） | 不截断，整段落入剪贴板；日志只记字数 |
| 文本含换行 | 归一为单行空格 |
| 首次运行无 config.json | 全默认，随后因缺 Key 走 E01 |
| config.json 非法 JSON | E15，退出 |
| 配置含未知字段 | 日志告警一次，继续启动 |
| 剪贴板原内容是图片 | 备份 `DataObject`，粘贴后放回；放回失败才走 E17 |
| 剪贴板被其它进程占用 | 写文本重试三次、间隔 50 毫秒，仍失败走 E10 |
| 目标窗口以管理员运行 | 普通权限下钩子收不到按键，README 写明限制 |
| 设备拒绝 16k | E02，日志列出可用设备 |
| 多显示器 | 固定主屏工作区右下角，距边 24 像素 |
| DPI 缩放 125%/150% | 按 PerMonitorV2 感知，位置按工作区换算 |
| 悬浮窗创建失败 | 降级为提示音加日志，听写照常可用（E22） |
| 状态圆被前台窗口盖住 | 每次显示时显式设置 `HWND_TOPMOST` 与 `WS_EX_LAYERED`；`Form.TopMost` / `Form.Opacity` 在绕过 WinForms 显示路径时不生效，这是踩过的坑 |
| 插件缺依赖程序集 | 默认加载上下文只查 deps.json；宿主注册解析器，按文件名在 `providers\` 与程序目录里找 |
| 托盘创建失败 | 启用 Ctrl+Alt+Q 兜底退出，写 E23，其余功能不受影响 |
| 资源管理器重启 | 收到 `TaskbarCreated` 消息后重新显示托盘图标 |
| 托盘图标被折叠进隐藏区 | 不做处理，Windows 的固定行为 |
| 机器休眠后恢复 | 钩子仍在，采集按需重开；不做额外处理 |
| 第二个实例 | E21 提示后退出 |
| 日志目录不可写 | 静默降级为不写日志，不阻断听写 |
| 右 Ctrl 被其它软件占用 | 钩子仍能收到（低级钩子优于热键），但目标软件也会响应；README 建议改配 |

---

## 9. 修改范围（文件清单）

### 9.1 新增（C# 工程，55 个文件，6 个工程）

```
VoiceInput/
├─ PLAN.md                          本计划（由 PLAN-CSHARP.md 改名）
├─ VoiceInput.sln
├─ Directory.Build.props            Nullable、ImplicitUsings、LangVersion、TreatWarningsAsErrors
├─ .gitignore                       bin/ obj/ config.json *.user
├─ README.md                        安装、配置、热键、故障排查
├─ config.example.json
├─ src/VoiceInput.Core/VoiceInput.Core.csproj
│  ├─ Config/AppConfig.cs
│  ├─ Config/ConfigLoader.cs
│  ├─ Config/ApiKeyResolver.cs
│  ├─ Asr/AsrClient.cs
│  ├─ Asr/AsrResult.cs
│  ├─ Text/TextNormalizer.cs
│  ├─ Diagnostics/ErrorCatalog.cs
│  ├─ Diagnostics/RotatingFileLog.cs
│  └─ Pipeline/VoiceInputRunner.cs      含状态、接口、守卫
├─ src/VoiceInput.App/VoiceInput.App.csproj
│  ├─ Program.cs
│  ├─ Platform/KeyboardHook.cs
│  ├─ Platform/NaudioMicRecorder.cs
│  ├─ Platform/ClipboardPaster.cs
│  ├─ Platform/ToneFeedback.cs
│  ├─ Ui/StatusOverlay.cs
│  ├─ Ui/TrayIcon.cs
│  ├─ Ui/SettingsForm.cs               设置窗口，流式布局
│  ├─ Ui/AppIcon.cs                    读嵌入的 ico 资源
│  ├─ Resources/voiceinput.ico         9 个尺寸，来自用户的 SVG
│  └─ app.manifest                     PerMonitorV2
├─ tests/VoiceInput.Core.Tests/VoiceInput.Core.Tests.csproj
│  ├─ ConfigLoaderTests.cs
│  ├─ ApiKeyResolverTests.cs
│  ├─ AsrClientTests.cs
│  ├─ TextNormalizerTests.cs
│  ├─ ErrorCatalogTests.cs
│  ├─ RotatingFileLogTests.cs
│  ├─ ConfigSaveTests.cs               写回与合并
│  ├─ VoiceInputRunnerTests.cs
│  └─ Fakes/FakeDependencies.cs
└─ docs/manual-test.md               手工验收清单
```

### 9.2 对既有内容的处理

1. **先复制**：仓库目录 → 同级目录 `VoiceInput-python\`，**排除** `.venv`、`__pycache__`、`.pytest_cache`。复制后核对：Python 文件数与 `PLAN.md`、`tests/`、`tools/` 都在，且在新副本里能跑通一个测试文件。
2. **再清理原目录**：副本核对通过后，删除 `VoiceInput\` 下的全部 Python 产物——`main.py`、`voiceinput\`、`tests\`、`tools\`、`docs\`、`PLAN.md`、`pytest.ini`、`requirements*.txt`、`config.example.json`、`run.cmd`、`README.md`、`.gitignore`、`.venv`、`__pycache__`、`.pytest_cache`。
3. **完成后 `VoiceInput\` 只剩本计划 §9.1 的 C# 文件**。
4. 运行中的 Python 实例（两个进程，PID 34492 与 31532）在第 1 步之前结束，否则 `.venv` 删不掉、复制也可能撞文件锁。
5. 不触碰 `Audio2Text` 及其它任何兄弟仓库。

---

## 10. 实现步骤

每步都有可独立验证的出口。

1. **停实例、留副本、清原目录**：结束 PID 34492 与 31532 → 复制到 `VoiceInput-python`（排除三项）→ 核对文件数与一个测试文件可跑 → 删除原目录 Python 产物。验证：`VoiceInput-python` 里 `pytest tests/test_config.py` 通过；`VoiceInput\` 下 `Get-ChildItem -Recurse -Filter *.py` 无结果。
2. **骨架与还原验证**：建 sln、两个工程、测试工程、`Directory.Build.props`、`.gitignore`。写一个临时探针测试类跑通 `dotnet test --filter`。验证：restore 成功，NAudio 与 xUnit 版本落进 `obj/project.assets.json`。
3. **配置**：`AppConfig`、`ConfigLoader`、`ApiKeyResolver`。验证：`ConfigLoaderTests`、`ApiKeyResolverTests` 全绿。
4. **文本与文案**：`TextNormalizer`、`ErrorCatalog`。验证：对应测试类全绿（含 F12 合规断言）。
5. **ASR 客户端**：`AsrClient` + 假 `HttpMessageHandler`。验证：`AsrClientTests` 全绿（含重试次数与退避）。
6. **日志**：`RotatingFileLog`。验证：`RotatingFileLogTests` 全绿（含 2 MB 轮转）。
7. **状态机**：`VoiceInputRunner` + 五个接口 + 假实现。验证：`RunnerTests` 全绿（含 F1/F2/F3/F5/F8/F14/F16 的状态与时长）。
8. **App 平台层**：`KeyboardHook`、`NaudioMicRecorder`、`ClipboardPaster`、`ToneFeedback`、`StatusOverlay`、`TrayIcon`、`Program`。验证：`dotnet build` 零警告；`dotnet publish -c Release` 成功。
9. **端到端手工验收**：真机跑起来，按 `docs/manual-test.md` 逐条过——按住说话、悬浮条计时、粘贴进记事本、粘贴进终端、组合键取消、托盘右键退出、双击托盘气泡、第二个实例被拒。
10. **收尾**：README、`config.example.json`、`docs/manual-test.md` 写完；把 `PLAN-CSHARP.md` 改名为 `PLAN.md`。

---

## 11. 测试方案

- 位置：`tests/VoiceInput.Core.Tests/`，一个被测模块一个文件，`Fakes/` 放替身。
- 运行：`dotnet test tests/VoiceInput.Core.Tests/VoiceInput.Core.Tests.csproj --filter "FullyQualifiedName~ConfigLoaderTests"`；按类过滤，不跑整个解决方案。
- 硬约束：单测不起 WinForms、不开麦克风、不发真实按键、不联网。四处外部依赖各有替身：`HttpMessageHandler`、`IRecorder`、`IPaster`、`IStatusView`。
- 真实 API 不进自动化套件：端到端听写由手工验收完成（见 `docs/manual-test.md`）。

覆盖维度对照：

| 维度 | 落点 |
|---|---|
| 正常流程 | 2 秒录音 → 转写 → 粘贴的完整调用序列 |
| 异常流程 | E04/E05/E06/E07/E08/E09/E10/E11 各自的码、文案与重试行为 |
| 边界条件 | 0 秒、0.29 秒、118 秒、空文本、5000 字、空配置、非法 JSON、未知字段 |
| 极端输入 | 含换行/制表符/emoji 的文本；`<script>` 与 SQL 串原样进剪贴板 |
| 核心业务逻辑 | 状态迁移表、组合键守卫、重试退避、截断标记、悬浮条回落时长 |
| 模块间交互 | ASR 抛错时不做粘贴；粘贴失败时仍写日志；悬浮窗抛错时流程继续 |
| 回归风险点 | 新仓库首版，无既有行为；以“文案合规”“配置默认值”“WAV 头字段”三组守卫测试代替 |

预计：8 个测试文件、60~75 条用例。

---

## 附：决策记录

阶段一追问的全部结论，均已并入上文。

| 问题 | 结论 |
|---|---|
| 用什么 UI 框架 | WinForms（用户指定；否决 Avalonia、否决继续 tkinter） |
| 旧 Python 代码怎么处理 | 复制到兄弟目录 `VoiceInput-python` 作行为参照，核对后清理原目录（Q18A） |
| 悬浮条常驻还是只在听写时出现 | 只在听写期间出现（Q19A） |
| 怎么退出程序 | 托盘图标常驻，右键菜单退出；双击弹气泡；托盘不可用时启用 Ctrl+Alt+Q 兜底（Q20 自定义） |
| 提示音 | 加长：开始 200 毫秒、结束 120 毫秒、失败 300 毫秒，用 NAudio 生成正弦音（Q21B） |
| 单实例锁 | 要，第二个实例提示后退出（Q22A） |
| 设备拒绝 16k 怎么办 | 直接报 E02 并提示换设备，不做重采样 |
| 状态条的形态 | 圆形小指示器，录音蓝、成功绿、失败红，跟着屏幕水平居中、贴底部（Q23~Q26） |
| 设置窗口放哪些项 | 热键、提示音开关、API Key 三项（Q27 用户指定） |
| 设置保存到哪 | 固定 `%LOCALAPPDATA%\VoiceInput\config.json`（Q28A） |
| 设置如何生效 | 能立即生效的立即生效，其余下次录音读取（Q29A） |
| API Key 优先级 | 设置里填的最优先，清空则回退环境变量（用户选定 B） |
| 开机自启 | 做，当前用户，写 `HKCU\...\Run`（用户指定） |
| 识别引擎 | 插件化，一个后端一个 DLL；DeepSeek 也走插件的路（用户指定） |
| 本地引擎选型 | SenseVoice（与 Audio2Text 同款模型），走 sherpa-onnx 的 ONNX 版本，不依赖 Python（用户指定） |
| 已装引擎 | deepseek（云端）、sensevoice-local（离线）；Windows 内置那个试过之后按用户要求去掉了 |
| 图标从哪来 | 用户提供的 SVG，栅格化成 9 尺寸 .ico 作嵌入资源；改掉原先“运行时绘制、不引入二进制资源”的方案 |
| 设置窗口布局 | 流式布局（TableLayoutPanel + AutoSize），不用绝对坐标；绝对坐标在 200% 缩放下会重叠 |
