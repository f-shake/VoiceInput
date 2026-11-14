# VoiceInput

按住右 Ctrl 说话，松开后把 DeepSeek 云端 ASR 的转写结果直接粘贴到当前窗口。

WinForms 实现，替代此前的 Python 版；Python 版保留在兄弟目录 `VoiceInput-python`
里作为行为参照，不再维护。

## 环境要求

- Windows 10 或 11
- .NET 10 桌面运行时（开发机装 SDK 即可）

## 构建与运行

```powershell
dotnet build                         # 编译整个解决方案
dotnet build src/VoiceInput.App      # 只编译程序
dotnet run --project src/VoiceInput.App
```

发布到单目录（仍需目标机装 .NET 10 桌面运行时）：

```powershell
dotnet publish src/VoiceInput.App -c Release -o publish
```

单文件发布本次不做：自包含会带来六十多兆体积，与当前需求不匹配。

## 配置

配置文件按以下顺序查找，用第一个存在的：

1. `VOICEINPUT_CONFIG` 环境变量指向的路径
2. 可执行文件同目录的 `config.json`
3. `%LOCALAPPDATA%\VoiceInput\config.json`

实际用了哪一个，会写进日志首行。没有配置文件也能启动——全部字段都有默认值，
只是还会因为缺 API Key 停下来。

14 个字段全部可选：

| 字段 | 默认 | 说明 |
|---|---|---|
| `apiKey` | 空 | 设置窗口里填的就是它；清空即回退到环境变量 |
| `provider` | `deepseek` | 用哪个识别引擎，对应 `providers` 目录里某个插件的 Id |
| `providerOptions` | `{ "baseUrl": … }` | 交给插件的键值，例如 `modelDir`、`numThreads`、`baseUrl` |
| `hotkey` | `RightCtrl` | 接受 `RightCtrl` / `RCTRL` / `F1`~`F12` |
| `deviceNumber` | null | 输入设备编号，null 表示系统默认 |
| `maxSeconds` | 118 | 单次录音上限，1~118 |
| `minSeconds` | 0.3 | 短于这个时长直接丢弃，0~10 |
| `connectTimeoutSeconds` | 5.0 | 连接超时，0.5~30 |
| `readTimeoutSeconds` | 60.0 | 读取超时，5~120 |
| `retries` | 2 | 可重试错误的重试次数，0~5 |
| `retryBackoffSeconds` | `[0.5, 1.5]` | 每项 0~10，长度不小于 `retries` |
| `beep` | true | 提示音开关 |
| `pasteDelayMilliseconds` | 80 | 写剪贴板到发 Ctrl+V 之间的等待，0~1000 |
| `restoreClipboard` | true | 粘贴后是否还原原剪贴板 |

JSON 里允许注释与尾逗号。未知字段会在日志里告警一次然后忽略。

### API Key 从哪来

按优先级：**设置里填的（即配置文件里的 `apiKey`）优先**，其次才是
`VOICEINPUT_API_KEY` → `DEEPSEEK_API_KEY`（进程环境变量）→ 同名用户级环境变量
（注册表 `HKCU\Environment`）。想改回用环境变量，把设置窗口里的 Key 清空即可。
日志只记来源，不记值。

新设的用户级环境变量不会补进已经运行的进程，所以程序会额外读一次注册表——否则
现象会是“明明设了却报没配置”。

## 使用

1. 启动后托盘出现图标，日志写一行“就绪”。
2. 光标放进目标窗口，**按住右 Ctrl** 说话。屏幕右下角出现状态条，红点加计时。
3. 松开。状态条变“转写中”，随后显示“已粘贴 N 字”，1.5 秒后消失。
4. 退出：右键托盘图标 → 退出。托盘不可用时用 `Ctrl+Alt+Q`。

按住热键期间**按了任何其它键**，这一整段会被丢弃并提示“检测到组合键，已取消本次
听写”——右 Ctrl 是组合键的修饰键，没有这层保护，按右 Ctrl+C 就会误触发听写。

## 日志

`%LOCALAPPDATA%\VoiceInput\logs\voiceinput.log`，超过 2 MB 轮转，保留 3 份。
写不进去就静默停止写日志，不影响听写。

## 设置

右键托盘图标 → 设置。可以改：识别引擎（deepseek / sensevoice-local）、热键（右 Ctrl / F1~F12）、提示音开关、开机自启、API Key。

- 保存写入 `%LOCALAPPDATA%\VoiceInput\config.json`；其余 10 个字段仍手改文件
- 热键与提示音**立即生效**；API Key 从下一次转写请求开始使用
- 窗口里有一行只读说明告诉你当前 Key 来自哪里。**这里填的值优先于环境变量**，
  清空后才回退到环境变量
- API Key 以明文保存在 config.json 里，与手工填写一样
- 写回时只覆盖那 14 个已知字段，你手工加的其它字段不会被抹掉

## 图标

托盘与窗口图标来自 `src/VoiceInput.App/Resources/voiceinput.ico`，由用户提供的 SVG
栅格化成 16~256 共 9 个尺寸，作为嵌入资源编译进程序。托盘按系统要求的小图标尺寸
取原生位图，200% 缩放下也是 32 像素原生绘制，不是放大出来的。

## 已知限制

- **目标窗口以管理员权限运行时收不到按键**：普通权限进程的低级键盘钩子拦不到提权
  窗口。需要在这类窗口里用时，得让本程序也以管理员身份启动。
- **设备必须支持 16k 采样**：不支持就报“无法打开麦克风”，日志里列出可用设备。
  本次不做重采样——那条路径没有测试覆盖，出问题比直接报错难查。
- **右 Ctrl 可能被别的软件占用**：钩子仍能收到，但对方软件也会响应；冲突时把
  `hotkey` 改成 `F9` 之类。
- 悬浮条只在听写期间出现，不做常驻，也不做鼠标交互。
- 不做单文件发布。
- 开机自启只做当前用户（`HKCU\...\Run`），不写系统级、不需要管理员。

## 故障排查

| 现象 | 原因 | 做法 |
|---|---|---|
| 弹窗“未配置 API Key” | 三个来源都没有 Key | 设环境变量或填 `apiKey` |
| 弹窗“已有一个 VoiceInput 在运行” | 单实例锁被占用 | 从托盘退出旧实例 |
| 状态条一闪“录音过短” | 按下松开太快 | 说完再松手 |
| 状态条“无法打开麦克风” | 设备被独占或不支持 16k | 换设备，或改 `deviceNumber` |
| 状态条“没有识别到语音内容” | 没说话、太小声、环境太吵 | 靠近麦克风重说 |
| 状态条“网络请求失败” | 断网、代理、DNS | 检查网络；日志里有重试次数 |
| 状态条“API Key 无效或已失效” | Key 过期或被撤销 | 换 Key |
| 粘贴没反应但提示“已复制，请按 Ctrl+V 粘贴” | 目标窗口拦截了模拟按键 | 手动粘贴；文本已在剪贴板里 |
| 弹窗“无法读取配置文件” | 文件被别的程序独占或权限变了 | 关掉编辑器再启动 |
| 状态条“本地识别模型缺失或不完整” | SenseVoice 的 model.int8.onnx 或 tokens.txt 不在模型目录 | 重新下载模型到 %LOCALAPPDATA%\VoiceInput\models\sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17 |
| 托盘图标不见了 | 资源管理器重启过 | 会自动重挂；仍不见就重启程序 |
| 完全没有反应 | 目标窗口是管理员窗口 | 用管理员身份启动本程序 |

## 测试

```powershell
dotnet test tests/VoiceInput.Core.Tests --filter "FullyQualifiedName~ConfigLoaderTests"
```

按类过滤运行。单元测试不碰麦克风、剪贴板、网络与窗口。
