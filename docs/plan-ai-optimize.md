# AI 优化（听写结果校对与润色）开发计划

按住热键说完话，转写结果先在本地或云端拿到，再按设置决定是否送一趟“AI 优化”，
最后才粘贴。声音识别本身不动；这一层只处理已经拿到的文字。

- 状态：修订稿（阶段一，追问已闭环，待闸门1 确认）
- 上一份计划：仓库根目录 `PLAN.md`（本轮不改它）
- 决策来源：用户 2026-11-16 的口述需求 + 同日一轮追问，7 项决策见 §0

---

## 0. 已确认的决策

| # | 决策 | 结论 |
|---|---|---|
| Q1 | 优化覆盖范围 | 所有引擎都走优化，云端与本地一视同仁 |
| Q2 | “保留句末标点”开关 | 独立生效：选“不优化”时也删原文末尾标点，不花请求 |
| Q3 | 优化失败兜底 | 粘贴原文，不重试、不弹窗，只写日志 |
| Q4 | “修复和润色”边界 | 可删无意义语气词与重复词、可微调语序；不得增删实义信息 |
| Q5 | 两个 Key 的来源 | **都只认设置里手填的值**，彻底不读环境变量（2026-11-16 两次收紧：先取消结果优化的，再取消语音转写的） |
| Q6 | 默认值 | 默认方式“不优化”；句末标点默认保留 |
| Q7 | 模型名与接口地址 | 可在设置窗口改：模型是下拉（可手输），地址是自由文本；默认 `deepseek-chat` / `https://api.deepseek.com` |
| Q8 | 界面分块与“不优化”（2a 评审时新增） | 设置窗口分三块：常规（提示音、开机自启、热键）、语音识别（识别引擎、API Key）、结果优化。取消总开关，改为方式里的一项“不优化”；选中它时 Key、模型、接口地址变灰，保留句末标点仍可操作 |

---

## 1. 功能需求

每条都能落到一条自动化测试或一条手工验收项上。

| 编号 | 需求 | 验收方式 |
|---|---|---|
| A1 | 设置窗口分三块：常规（提示音、开机自启、热键）、语音识别（识别引擎、API Key）、结果优化（优化方式含“不优化”、独立 Key、模型、接口地址、保留句末标点），并保留“打开日志/取消/保存” | 手工验收：三块布局在 100% 与 200% 缩放下不重叠、不裁切 |
| A2 | 优化方式为“不优化”时，转写结果**逐字**按现状粘贴，不发起任何额外请求；日志注明“结果优化：不优化” | 单测：假优化器调用次数为 0 |
| A3 | 优化方式非“不优化”时，转写成功（非空）后先优化再粘贴；优化成功时粘贴的是优化后的文本 | 单测：假优化器返回不同文本，断言粘贴的是新文本 |
| A4 | 优化方式为“仅修复标点”时，输出与原文去掉标点后的字符序列必须完全一致；不一致视为优化失败，回退原文 | 单测：假响应改动了一个汉字，断言回退 |
| A5 | 优化失败（网络、超时、401、429、5xx、响应不可解析、结果为空、结果长度异常、标点模式改字）时**粘贴原始转写文本**，并在日志里留一条带错误码的告警，不弹窗、不中断听写 | 单测：假优化器抛各类异常，断言粘贴原文且状态仍为成功 |
| A6 | “保留句末标点”关闭时，最终文本末尾的句末标点被去掉（如“今天天气不错。”→“今天天气不错”） | 单测：各种标点、空白、无标点、整句为空 |
| A7 | 两个 Key 都只取设置里手填的值（`apiKey` / `aiOptimizeApiKey`），**不读任何环境变量**；设置窗口里不再显示“来源”提示行 | 单测：环境里有同名变量也不生效；手工验收：窗口里没有来源行 |
| A8 | 选了非“不优化”的方式、但 Key 为空时：不阻断启动、不阻断听写；启动日志与保存设置后各有一条明确提示，听写时退回原文粘贴 | 单测：方式为 fix 但 Key 为空，断言走原文路径 |
| A9 | 优化期间状态条文案为“优化中”；优化期间再按热键按现有“转写进行中”处理（忽略并写日志） | 单测：卡住的假优化器 + 状态条文案 |
| A10 | 优化结果统一过一遍现有 `TextNormalizer.Normalize`：控制字符清掉、换行压成空格、首尾空白去掉 | 单测：响应里带换行与制表符 |
| A11 | 优化失败的判定可解释：日志写明模式、耗时、失败原因（HTTP 码或哪条守卫拦下） | 单测：假日志断言关键字 |
| A12 | 配置文件 6 个新字段可手写；其中 5 个可被设置窗口写回（超时除外）；旧配置文件（没有这 6 个字段）照常启动 | 单测：旧文件读默认值；写回后再读一致 |
| A13 | 三种模式的提示词各不相同，且“仅修复标点”模式明确禁止改动任何文字 | 单测：断言三份 system prompt 的关键约束句 |
| A14 | 设置窗口里模型名是下拉框（候选 `deepseek-chat`、`deepseek-reasoner`，也允许手输），位置在接口地址下方；保存后立即生效；超时仍只在 config.json 里手改 | 单测：配置读写；手工验收：改模型名后保存，日志里出现新模型 |
| A15 | 模型名与接口地址沿用配置文件同一套校验（模型非空且 ≤64 字符、地址以 `http://` 或 `https://` 开头），不合法阻止保存并提示 | 单测：配置层三种非法值；手工验收：填非法地址点保存 |
| A16 | “保留句末标点”放在结果优化块的最下面，不加额外说明文字 | 手工验收：看窗口，且选“不优化”时它仍可操作 |
| A17 | 展开模型下拉时，按**当时的**接口地址与 Key 实时 `GET {baseUrl}/models`，用返回的 id 填充候选；接口地址不合法或 Key 为空时不发请求，保持内置候选，模型名仍可手输 | 单测：假 handler 断言请求行与鉴权头、解析结果；手工验收：点下拉看候选变化 |
| A18 | 拉取模型列表失败（网络、401、404、格式不符、空列表、超过 5 秒）时弹**现代样式的错误框**（WinForms `TaskDialog`，不用老式 `MessageBox`），写明原因；候选保持内置两项，模型名仍可手输 | 单测：异常到文案的映射；手工验收：把地址改成不通的端口，点下拉看弹框 |
| A19 | 首次启动或配置里没有 Key、而选中的引擎需要 Key 时：直接打开设置窗口让人填；取消或保存后仍为空才退出 | 手工验收：删掉 config 里的 apiKey 再启动 |

明确**不做**（本轮）：

- 不做语音识别的二次识别或重录。
- 不做多轮对话、不做上下文记忆（每次听写独立一次请求）。
- 不做本地模型（不引入第二个 LLM 依赖），优化只走 OpenAI 兼容的对话接口。
- 不做设置窗口里的超时编辑（它留在配置文件里手改，与其余“调错就难查”的参数
  同一处理方式）。
- 不做模型列表的联网拉取：候选项固定为 `deepseek-chat`、`deepseek-reasoner`，另允许手输。
- 不做“测试连接”按钮。
- 不改状态圆的尺寸、位置、动画；只新增一条“优化中”文案。

---

## 2. 技术方案（含选型理由与否决方案）

### 2.1 优化放在宿主管线里，不做成识别引擎插件

- **选它**：AI 优化与“用哪家语音识别”正交。本地 SenseVoice 也要能优化；插件契约
  （`IAsrProvider`）描述的是“把音频变成文字”，塞进文字加工会让每个插件重复实现一遍。
- **否决“在 DeepSeek 插件里实现”**：`VoiceInput.App` 对插件项目是
  `ReferenceOutputAssembly="false"` 的引用（见 `VoiceInput.App.csproj`），宿主编译期
  根本引用不到插件里的类型。
- **否决“新建一个 `VoiceInput.Providers.DeepSeek` 里的 refiner 项目”**：为了一个类
  加一个工程、加一条构建顺序，收益为零。

### 2.2 放在 `VoiceInput.Core/Optimize/`

- Core 现在没有网络代码，但 HTTP 是 BCL 自带（`HttpClient`），不引入任何 NuGet 包。
- 好处：宿主装配、单元测试都直接引用 Core，不需要新的工程引用；`VoiceInput.Core.Tests`
  已经引用了 Core，测试即插即用。
- 端点是 OpenAI 兼容的 `POST {baseUrl}/chat/completions`，默认
  `https://api.deepseek.com`，默认模型 `deepseek-chat`。之所以叫“Chat 文本优化器”而不是
  “DeepSeek 优化器”，是因为 baseUrl 与 model 可配后它对任何兼容端点都能用。

### 2.3 优化器做成可替换的接口，与转写器同一套装配方式

```csharp
public interface ITextRefiner
{
    Task<string> RefineAsync(string text, CancellationToken cancellationToken);
}
```

- 模式、句末标点开关、模型、超时都烤进实例（构造时给），与 `ITranscriber` 的
  `ReplaceTranscriber` 完全对称；设置变更时由装配层重建实例再替换。
- **否决“每次调用传 options”**：那会让状态机持有配置快照，多一处状态不一致的机会。
- **否决“在 `VoiceInputRunner` 里直接 new HttpClient”**：状态机将不可测。

### 2.4 失败一律回退原文，不重试

- 用户此刻正等着文字落进窗口，优化是锦上添花。重试会把“一句 2 秒”拖成“一句 6 秒”，
  而且原始文本已经在手里，没有任何理由为了润色卡住粘贴。
- 超时独立于 ASR 的 `readTimeoutSeconds`：默认 8 秒，配置项可改（1~60）。
- 复用 `retries`/`retryBackoffSeconds` 的**否决理由**：那两个参数是为“丢掉整段录音不可接受”
  设计的，优化失败没有这个代价。

### 2.5 确定性守卫，不只靠提示词

模型不听话是常态，所以每条护栏都在本地再判一次：

| 守卫 | 规则 | 触发后 |
|---|---|---|
| 空结果 | 去空白后为空 | 失败回退 |
| 长度上限 | 长度 > 原文长度 × 2 + 10 | 失败回退（模型开始解释、扩写） |
| 标点模式改字 | 去掉标点与空白后字符串不相等 | 失败回退 |
| 句末标点 | 本地确定性切除，不交给模型 | — |

### 2.6 模型列表从接口地址实时拉取

- 走 OpenAI 兼容的 `GET {baseUrl}/models`（Bearer 鉴权），只取每项的 `id`。
- **不缓存、不重试**：每次展开下拉都拉一次，保证候选跟当前地址一致；失败就退回内置
  两项（`deepseek-chat`、`deepseek-reasoner`），模型名仍然手输可用。
- 超时 5 秒，独立于转写与优化的超时——用户正盯着下拉框，等久了不如让他手输。
- 否决“后台定时刷新”：列表一天也变不了几次，定时请求只会浪费配额。
- 否决“把模型列表做进下拉之外的选择页”：为一个字段开一页，收益不抵复杂度。

---

## 3. 系统结构

```
Program.Main
  ├─ ConfigLoader.Load ───────────────► AppConfig（新增 6 字段）
  ├─ config.ApiKey ──────────────────► 转写 Key（只认配置文件）
  ├─ config.AiOptimizeApiKey ────────► 优化 Key（只认配置文件）
  ├─ 缺 Key → SettingsForm.ShowDialog() → 重新读配置
  └─ TrayApplicationContext
        ├─ BuildTranscriber ───────────► ITranscriber（插件）
        ├─ BuildRefiner ───────────────► ITextRefiner?（Core.Optimize）
        └─ VoiceInputRunner(config, recorder, transcriber, refiner?, paster, status, feedback, log)
              HotkeyUpAsync
                └─ RunPipelineAsync
                      transcribe ──► [refiner.RefineAsync] ──► TrailingPunctuation ──► paste
```

调用关系要点：

- `VoiceInputRunner` 只认 `ITextRefiner`，不认 HTTP、不认 DeepSeek。
- `DeepSeekTextRefiner` 只认 `HttpClient` + 提示词 + 守卫，不认配置、不认日志接口
  （出错抛 `VoiceInputException`，由 runner 捕获并记日志）。
- 设置窗口不认识优化器，只把 5 个字段写回 `AppConfig`（超时不进窗口）。

---

## 4. 数据结构

### 4.1 `AppConfig` 新增字段

| 字段 | 类型 | 默认 | 约束 | JSON 键 |
|---|---|---|---|---|
| `AiOptimizeApiKey` | string | `""` | 非空时长度 ≥ 16（与 ASR Key 同规则） | `aiOptimizeApiKey` |
| `AiOptimizeMode` | string | `"none"` | 只能是 `none` / `polish` / `fix` / `punctuation` | `aiOptimizeMode` |
| `KeepTrailingPunctuation` | bool | `true` | — | `keepTrailingPunctuation` |
| `AiOptimizeModel` | string | `"deepseek-chat"` | 非空，长度 ≤ 64 | `aiOptimizeModel` |
| `AiOptimizeBaseUrl` | string | `"https://api.deepseek.com"` | 非空，`http`/`https` 开头 | `aiOptimizeBaseUrl` |
| `AiOptimizeTimeoutSeconds` | double | `8.0` | 1~60 | `aiOptimizeTimeoutSeconds` |

字段总数从 14 变 20；`ConfigLoader.Save` 逐个写回，未知键照旧保留。

### 4.2 优化模式

“不优化”是方式里的一个取值，不另设总开关：一项设置只表达一件事，界面上就不会
出现“开关关着、方式却选着”的矛盾状态。

```csharp
public enum RefineMode { None, Polish, Fix, Punctuation }

public static class RefineModes
{
    public const string None = "none";               // 不优化
    public const string Polish = "polish";           // 修复和润色
    public const string Fix = "fix";                 // 仅修复错别字和标点
    public const string Punctuation = "punctuation"; // 仅修复标点

    public static bool TryParse(string? raw, out RefineMode mode);   // 忽略大小写与首尾空白
    public static string ToConfigValue(RefineMode mode);
    public static string DisplayName(RefineMode mode);               // 中文，给下拉框
}
```

### 4.3 优化器构造参数

```csharp
public sealed record RefineOptions(
    RefineMode Mode,
    bool KeepTrailingPunctuation,
    string Model,
    double TimeoutSeconds);
```

`DeepSeekTextRefiner(string apiKey, string baseUrl, RefineOptions options, HttpMessageHandler? handler = null)`
—— `handler` 只为测试注入，生产传 null。

### 4.4 请求与响应

请求（`application/json`）：

```json
{
  "model": "deepseek-chat",
  "messages": [
    { "role": "system", "content": "<按模式生成的提示词>" },
    { "role": "user", "content": "<转写文本>" }
  ],
  "stream": false,
  "temperature": 0
}
```

响应只取 `choices[0].message.content`（字符串）；其余字段忽略。

### 4.5 模型列表响应

```json
{ "object": "list", "data": [ { "id": "deepseek-chat", "object": "model" } ] }
```

只取 `id`；也兼容根节点直接是字符串数组的简化端点。空 id 丢弃、重复项去重、
最多保留 200 项（再长对下拉框没有意义）。

---

## 5. 模块划分

| 模块 | 负责 | **不负责** |
|---|---|---|
| `RefineMode.cs` | 四个取值的解析、序列化、中文名 | 提示词、网络 |
| `RefinePrompt.cs` | 按模式拼 system prompt；公共约束（只输出结果、不要解释） | 发送请求、结果守卫 |
| `TrailingPunctuation.cs` | 确定性去掉句末标点 | 判断该不该去（那是配置的事） |
| `DeepSeekTextRefiner.cs` | 组请求、发请求、状态码映射、解析、守卫、`TextNormalizer.Normalize`、句末标点切除 | 重试策略（本轮不重试）、日志、UI |
| `ITextRefiner` | 管线契约（一个方法） | 任何实现细节 |
| `VoiceInputRunner`（改） | 在转写与粘贴之间调用一次优化器；失败回退；改状态条文案；改日志 | 渲染提示词、发 HTTP |
| `AppConfig.ApiKey` | 转写 Key 的唯一来源；不再有解析层 | 环境变量、注册表 |
| `SettingsForm`（改） | 三块布局、控件取值、写回 5 个字段、不优化时变灰 | 解析 Key、发请求 |
| `Program`（改） | 解析 AI Key、造优化器、设置变更后重建并替换 | 提示词内容 |

---

## 6. 交互逻辑

### 6.1 一次听写

1. 按住热键 → 录音中（不变）。
2. 松开 → 状态条“转写中”（若超上限先显示截断提示，不变）。
3. 转写返回：空白 → “没有识别到语音内容”，结束（**不调优化器**）。
4. 非空、方式不是“不优化”、且优化器已就绪 → 状态条“优化中” → 发一次优化请求。
5. 优化成功 → 用优化后文本；失败 → 用原文，日志告警。
6. `KeepTrailingPunctuation == false` → 对最终文本去句末标点。**本步与优化是否执行无关**：
   方式为“不优化”时直接对原文去，优化时对优化结果去。
7. 粘贴 → 状态条“已粘贴 N 字”（N 为最终文本长度），1.5 秒后消失。
8. 日志一行：`已粘贴 N 字，服务端 X 毫秒，耗时 Y 秒，request_id=…，结果优化 fix，优化耗时 Z 毫秒`；
   不优化时写 `结果优化 不优化`；失败时写 `结果优化失败（E26 …）已使用原始文本`。

### 6.2 设置窗口

```
┌ VoiceInput 设置 ─────────────────────────┐
│ ┌ 常规 ────────────────────────────────┐ │
│ │ ☐ 开启提示音                         │ │
│ │ ☐ 开机自启（当前用户）                │ │
│ │ 热键      [RightCtrl             ▾]  │ │
│ └──────────────────────────────────────┘ │
│ ┌ 语音识别 ────────────────────────────┐ │
│ │ 识别引擎  [DeepSeek 云端         ▾]  │ │
│ │ API Key   [••••••••••••           ]  │ │
│ └──────────────────────────────────────┘ │
│ ┌ 结果优化 ────────────────────────────┐ │
│ │ 优化方式  [仅修复错别字和标点     ▾]  │ │
│ │ API Key   [••••••••••••           ]  │ │
│ │ 接口地址  [https://api.deepseek.com]  │ │
│ │ 模型      [deepseek-chat         ▾]  │ │
│ │ ☐ 保留句末标点                       │ │
│ └──────────────────────────────────────┘ │
│                        [保存][取消][打开日志] │
└──────────────────────────────────────────┘
```

- 布局继续用 `TableLayoutPanel` + `AutoSize`，不写绝对坐标；三块各用一个
  `GroupBox`，列宽给一个统一下限，三块的输入框左边缘才对得齐。
- 优化方式下拉固定四项，顺序：不优化、修复和润色、仅修复错别字和标点、仅修复标点。
- 选“不优化”时，Key、接口地址、模型所在的整块 `Enabled = false`（视觉变灰、
  不可编辑）；“保留句末标点”不在其中，仍可操作。
- 接口地址是自由文本输入框；模型是下拉框（候选项 `deepseek-chat`、`deepseek-reasoner`），
  也允许手输，换兼容端点时不用改代码。
- **展开模型下拉时**用当前接口地址与 Key 去拉一次 `/models`：拉到了就换掉候选
  （列表确实变了、且下拉还开着时重新展开一次，免得要点第二次）；拉不到就弹一个
  `TaskDialog` 错误框写明原因，候选保持内置两项，模型名仍可手输。
- 保存时若方式不是“不优化”但 Key 为空 → 弹一次提示“结果优化已选择该方式，
  但没有填写 API Key，本次保存后听写会按原文粘贴。”，点“确定”后**照常保存**。
- 保存时若模型名为空、或地址不是 `http://` / `https://` 开头 → 弹
  “结果优化的模型名不能为空。” / “结果优化的接口地址必须以 http:// 或 https:// 开头。”
  并**不保存**（与现有 API Key 长度校验同一处理方式）。
- 保存后立即生效：热键、提示音、Key 已有的规则不变；优化器实例按需重建
  （方式、Key、模型、地址变化都重建；超时只能在文件里改，改了要重启）。

### 6.3 状态条

只多一条文案“优化中”（3 字，无需截断）。状态仍是 `OverlayState.Transcribing`，
圆与动画不变。

---

## 7. 异常情况（含提示文案）

| 情况 | 表现 | 日志 |
|---|---|---|
| 方式不是“不优化”但 Key 为空 | 正常听写，按原文粘贴 | `WARN E26 结果优化已选择该方式但没有填写 API Key，已跳过优化。` |
| 401 | 粘贴原文 | `ERROR E26 AI 优化失败：API Key 无效（HTTP 401），已使用原始文本。` |
| 429 / 5xx | 粘贴原文（不重试） | 同上，带 HTTP 码 |
| 连接失败 / 超时 | 粘贴原文 | `E26` + `读取超时 8 秒` 或异常消息 |
| 响应不是 JSON / 缺 `choices[0].message.content` | 粘贴原文 | `E26` + 响应前 200 字（`TextNormalizer.OneLine`） |
| 优化结果为空 | 粘贴原文 | `E26` + `优化结果为空` |
| 优化结果超长（> 2×原文 + 10） | 粘贴原文 | `E26` + `优化结果长度异常：N 字` |
| 标点模式改动了文字 | 粘贴原文 | `E26` + `仅修复标点模式改动了文字，已回退` |
| 转写结果为空 | 与现在一致：不粘贴、提示“没有识别到语音内容” | 不写优化相关日志 |
| 配置文件里 `aiOptimizeMode` 非法 | 启动失败，弹“配置项 aiOptimizeMode 不合法。” | `E16` + 实际值 |
| 配置文件里 `aiOptimizeTimeoutSeconds` 越界 | 同上，报 `aiOptimizeTimeoutSeconds` | `E16` + 实际值 |
| 配置文件里 `aiOptimizeModel` 为空或超过 64 字 | 同上，报 `aiOptimizeModel` | `E16` + 实际值 |
| 配置文件里 `aiOptimizeBaseUrl` 不以 `http`/`https` 开头 | 同上，报 `aiOptimizeBaseUrl` | `E16` + 实际值 |

新增错误码 `E26 AiOptimizeFailed`，用户可见文案：
**“AI 优化失败，已使用原始文本。”**（本轮只在日志用；状态条不放它，避免把成功粘贴
显示成失败。）

---

## 8. 边界条件

| 情况 | 期望 |
|---|---|
| 方式为“不优化” + 句末标点开关关闭 | 仍去掉原文末尾标点（已确认：该开关独立生效） |
| 原文只有标点（如“。”） | 优化器照常调用；结果非空即粘贴；句末标点关闭后变空 → 视为空文本，不粘贴，提示“没有识别到语音内容” |
| 原文末尾是“。”、“？”、“！”、“；”、“…”或 ASCII 的 `.?!;` | 关闭保留时全部去掉 |
| 原文末尾是“他说‘走吧。’”（引号包住） | 只处理引号外的标点；引号内的不动（去掉会破坏引号配对），行为写进 README |
| 原文末尾有空白/换行 | 先 trim 再去标点 |
| 优化结果带换行/制表符/控制字符 | `TextNormalizer.Normalize` 压成单行 |
| 优化结果首尾带空白 | trim |
| 优化结果用代码块包住 | 只可能被长度守卫或字符守卫拦下；不加额外剥离逻辑（提示词已禁止） |
| 优化期间按热键 | 与现有“转写进行中”一致：忽略并写 `E12` |
| 优化期间退出程序 | 现有退出路径不变，请求随进程结束 |
| `aiOptimizeBaseUrl` 带结尾斜杠 | 拼接前 `TrimEnd('/')` |
| `aiOptimizeApiKey` 填了 15 个字符 | 设置窗口阻止保存（与 ASR Key 同规则），手改配置文件则按原样使用 |
| 只改“保留句末标点”不改别的 | 立即生效，不重建优化器 |
| 只改优化方式 / AI Key / 模型名 / 接口地址 | 立即生效：重建优化器实例后替换 |
| 只改 `aiOptimizeTimeoutSeconds`（只能手改文件） | 重启程序后生效，日志提示当前超时值 |
| 模型名填了不存在的模型 | 请求返回 4xx，按优化失败回退原文，日志带 HTTP 码与模型名 |
| 展开模型下拉时拉不到（401 / 404 / 网络 / 坏 JSON / 空列表 / 超 5 秒） | 弹 TaskDialog 错误框：标题“无法获取模型列表”，展开区写明原因；候选保持内置两项 | 无（设置窗口不写日志） |

---

## 9. 修改范围（文件清单，基线）

**新增（7 个源文件 + 5 个测试文件）**

| 文件 | 内容 |
|---|---|
| `src/VoiceInput.Core/Optimize/RefineMode.cs` | `RefineMode` 枚举 + `RefineModes` |
| `src/VoiceInput.Core/Optimize/ITextRefiner.cs` | 接口 |
| `src/VoiceInput.Core/Optimize/RefinePrompt.cs` | 三份 system prompt + 公共约束 |
| `src/VoiceInput.Core/Optimize/TrailingPunctuation.cs` | 去句末标点 |
| `src/VoiceInput.Core/Optimize/DeepSeekTextRefiner.cs` | 请求、解析、守卫 |
| `src/VoiceInput.Core/Optimize/ModelCatalog.cs` | `GET /models` 拉取可用模型 |
| `tests/VoiceInput.Core.Tests/RefineModesTests.cs` | 模式解析 |
| `tests/VoiceInput.Core.Tests/RefinePromptTests.cs` | 提示词差异 |
| `tests/VoiceInput.Core.Tests/TrailingPunctuationTests.cs` | 标点切除 |
| `tests/VoiceInput.Core.Tests/DeepSeekTextRefinerTests.cs` | 请求/解析/守卫/超时 |
| `tests/VoiceInput.Core.Tests/ModelCatalogTests.cs` | `/models` 请求与解析 |

**修改**

| 文件 | 改动 |
|---|---|
| `src/VoiceInput.Core/Config/AppConfig.cs` | 6 个新属性；注释里的“14 个字段”改为 20 |
| `src/VoiceInput.Core/Config/ConfigLoader.cs` | 读取 6 个字段（含校验）；`Save` 写回 6 个字段 |
| `src/VoiceInput.Core/Config/ApiKeyResolver.cs` | **删除**：Key 不再来自环境变量，这个类与它的测试一并移除 |
| `src/VoiceInput.Core/Diagnostics/ErrorCatalog.cs` | `ErrorCodes.AiOptimizeFailed = "E26"`；`Optimizing`、`AiOptimizeFailed` 文案；`AllMessages()` 补两条 |
| `src/VoiceInput.Core/Pipeline/VoiceInputRunner.cs` | 可选 `ITextRefiner` + `ReplaceRefiner`；转写后优化；失败回退；日志与“优化中”文案 |
| `src/VoiceInput.App/Program.cs` | 解析结果优化 Key、日志来源、造优化器并传入 runner、设置变更后重建替换、缺 Key 提示 |
| `src/VoiceInput.App/Ui/SettingsForm.cs` | 改为“常规 / 语音识别 / 结果优化”三块；结果优化块含方式（四项）、Key、接口地址、模型（下拉，可手输）、句末标点；选“不优化”时前三项变灰；保存写回 5 个字段（超时不进窗口）；模型/地址校验 |
| `tests/VoiceInput.Core.Tests/Fakes/FakeDependencies.cs` | 新增 `FakeRefiner`（可返回值/抛异常/计数/可卡住） |
| `tests/VoiceInput.Core.Tests/ConfigLoaderTests.cs` | 新字段读取、默认值、三种非法值 |
| `tests/VoiceInput.Core.Tests/ConfigSaveTests.cs` | “14 个字段”改为 20；写回往返含新字段 |
| `tests/VoiceInput.Core.Tests/ApiKeyResolverTests.cs` | **删除**（随 ApiKeyResolver 一起移除） |
| `tests/VoiceInput.Core.Tests/VoiceInputRunnerTests.cs` | 优化成功/失败/不优化不调/空白不调/卡住状态文案 |
| `config.example.json` | 补 6 个字段 |
| `README.md` | 配置表 14→20、设置说明、结果优化一节、故障排查两行、已知限制一条 |
| `docs/manual-test.md` | 新增 6 条手工验收（三种优化方式、不优化、句末标点、断网回退、模型/地址校验与 200% 缩放） |

**明确不改**：两个 provider 项目、`StatusOverlay` 的绘制代码、`KeyboardHook`、
`ClipboardPaster`、`PLAN.md`、`VoiceInput.sln`（无新工程）。

---

## 10. 实现步骤

每步结束都能编译或能跑一条测试。

1. **配置层**：`AppConfig` 加 6 字段 → `ConfigLoader` 读写与校验 → 补配置测试 → `dotnet test --filter Config`。
2. **模式与提示词**：`RefineMode`/`RefineModes` + `RefinePrompt` → 测试。
3. **句末标点**：`TrailingPunctuation` → 测试。
4. **错误码与文案**：`ErrorCatalog` 两条 → 契约测试通过。
5. **优化器**：`ITextRefiner` + `DeepSeekTextRefiner`（请求、状态码、解析、守卫）→ 用
   `FakeHttpHandler`/`HangingHandler` 跑测试。
6. **Key 来源收紧**：`apiKey` 与 `aiOptimizeApiKey` 都只从配置文件取值 → 删除 `ApiKeyResolver` 及其测试 → 启动缺 Key 时打开设置窗口。
7. **模型列表**：`ModelCatalog` 拉 `/models` → 假 handler 测请求与解析 → 设置窗口接上下拉展开事件。
7. **管线接入**：`VoiceInputRunner` 可选优化器 + 回退 + 文案 → 测试。
8. **设置窗口**：三块布局 + 5 个字段读写 + 模型/地址校验 + 变灰逻辑与提示 → 编译并手工看一眼缩放。
9. **装配**：`Program` 造优化器、替换（模式/Key/模型/地址变化时）、日志、缺 Key 提示 → 编译并手工跑一次。
10. **文档**：`config.example.json`、`README.md`、`docs/manual-test.md`。
11. 自测：`dotnet build` + 与改动相关的测试类逐个跑（全量测试按禁令需另行授权）。

---

## 11. 测试方案

测试全部落在 `tests/VoiceInput.Core.Tests`（已引用 Core，无需新工程）。

| 维度 | 用例 | 文件 |
|---|---|---|
| 配置读取 | 6 字段各读对；缺省值；`aiOptimizeMode` 非法值报 E16；超时越界报 E16；模型为空报 E16；地址不是 http/https 开头报 E16；旧文件无新字段 | `ConfigLoaderTests` |
| 配置写回 | 20 字段全部写出；往返一致；未知字段保留；新字段类型正确 | `ConfigSaveTests` |
| 模式 | 四个值的解析/序列化/中文名；大小写与空白；未知值 | `RefineModesTests` |
| 提示词 | 三种优化方式 prompt 互不相同；“仅修复标点”含“一个字都不要改”；公共约束含“只输出” | `RefinePromptTests` |
| 标点 | 全角/半角句末标点；连续标点；末尾空白；引号内不处理；无标点不变；空串 | `TrailingPunctuationTests` |
| 优化器 | 请求头/模型/temperature；成功解析；换行压平；401/429/5xx/坏 JSON/缺字段/空结果/超长/标点模式改字/超时 → 均抛 E26 | `DeepSeekTextRefinerTests` |
| 模型列表 | 请求行是 `{baseUrl}/models`、带 Bearer；解析 `data[].id`；兼容字符串数组；空 id 与重复项过滤；非 200 抛错；超时抛错 | `ModelCatalogTests` |
| 管线 | 方式“不优化”不调用；成功粘贴新文本；失败粘贴原文且状态成功；不优化但句末标点关闭时粘贴去标文本；转写空白不调用；录音过短不调用；卡住时状态条“优化中”；日志关键字 | `VoiceInputRunnerTests` |
| 文案契约 | 新文案进 `AllMessages()`，逐条断言中文、无 emoji、无「」 | `ErrorCatalogTests`（现有逻辑自动覆盖） |
| 设置窗口 | 无法单测（WinForms），列入手工验收：三块布局、四项方式、不优化时变灰、模型下拉、模型/地址校验、缺 Key 提示、200% 缩放 | `docs/manual-test.md` |

运行方式（单类过滤，符合禁令要求）：

```powershell
dotnet test tests/VoiceInput.Core.Tests --filter "FullyQualifiedName~ConfigLoaderTests"
dotnet test tests/VoiceInput.Core.Tests --filter "FullyQualifiedName~DeepSeekTextRefinerTests"
dotnet test tests/VoiceInput.Core.Tests --filter "FullyQualifiedName~VoiceInputRunnerTests"
```

---

## 附：未决问题

无。8 项决策已在 §0 确认（Q8 为 2a 评审时新增），追问闭环。执行中若出现“做什么”
级别的分歧（不是实现细节），按流程回到闸门1 重新确认，不在阶段二私自改计划。
