# fatbot-console

肥鱼机器人（一只住在 QQ 里的 AI 虚拟少女）的**控制台三件套**开源版。

包含：电脑端 WinForms 控制台、原生 Android 控制台、控制台后端 HTTP API，以及配套的语音工具链。

> 这是「控制台/工具」部分，**不含**机器人的聊天人格逻辑、任何服务器地址、账号、密钥或聊天数据。
> 直接用需要你有自己的 QQ 协议端（本项目按 OneBot v11 约定对接）。

---

## 目录

```
fatbot-console/
├── desktop/            电脑端控制台（C# / WinForms / .NET 4.0）
│   ├── FishBotConsole.cs
│   ├── fish_icon.ico
│   └── build.ps1
├── android/            Android 原生控制台（Java，无 WebView）
│   ├── AndroidManifest.xml
│   ├── src/cn/feiyu/console/MainActivity.java
│   ├── src/cn/feiyu/console/InnerScrollView.java
│   ├── res/mipmap-*/ic_launcher.png
│   └── build-apk.ps1
├── server/             控制台后端（Python 3.10，只用标准库）
│   ├── app.py          配置驱动，支持 journal / file / ingest 三种取数
│   └── static/index.html  网页版控制台（可单独托管，自带连接设置）
├── voice/              语音工具链
│   ├── sf_tts.py       SiliconFlow CosyVoice2 合成（零样本克隆）
│   ├── to_silk.py      mp3 → QQ 原生 SILK 编码
│   ├── edge_seg_tts.py Edge-TTS 分段合成（备用）
│   └── xf_tts.py       讯飞在线 TTS（备用，需自己填凭据）
└── docs/API.md         后端接口文档
```

---

## 它长什么样

**电脑端**：状态卡 / 自检修复 / 登录二维码 / **QQNT 风格聊天记录**（会话列表 + 气泡 + 快捷消息 + 主动发文字或语音）/ **机器人控件页**（按群调「仅@后回复」「回复速度」「句式长度 14/26/40」）/ 文档页（含一键执行常用命令）。

**手机端**：状态 / 自检 / 扫码 / 日志四页；会话列表点进单会话看气泡；底部快捷语 + 语音开关 + 输入框。

**后端**：一个文件的标准库 HTTP 服务，给两端提供状态、诊断、扫码图、会话与消息、主动发送（文字/语音）。

---

## 接入你自己的机器人（三种方式）

后端是**配置驱动**的，不写死任何名字。先复制一份配置：

```bash
cd server
cp ../config.example.json config.json     # 或者从 presets/ 里挑一个
```

配置里这几项决定它连谁：

| 字段 | 作用 | 例子 |
|---|---|---|
| `service` | 机器人的 systemd 服务名（留空=不检查服务） | `fishbot` / `mybot` |
| `container` | 协议端容器名（留空=没有容器） | `snowluma` / `napcat` |
| `bot_dir` | 机器人目录，读 `data/`、`.env` 用 | `/opt/qqbot` |
| `onebot.http` | OneBot v11 HTTP 地址（发消息用） | `http://127.0.0.1:3000` |
| `log.source` | 聊天记录从哪读 | `journal` / `file` / `ingest` |
| `log.patterns` | 怎么从日志里认出「一条消息」 | 见下 |
| `qr.mode` | 二维码怎么取 | `screen`（截容器桌面）/ `off` |
| `voice.engine` | 语音引擎 | `siliconflow` / `off` |

### 方式 A：日志里已经有你要的格式（最省事）

满足下面任一条件就能直接用：日志里能 grep 出 `[接收]` / `[发送]` 这种行。

```json
"log": {
  "source": "journal",
  "patterns": {
    "in":  "\\[接收\\]\\s*(?P<ck>群|私聊)\\s*(?P<cid>\\d+)?\\s*(?P<who>.*?)\\s*->\\s*(?P<text>.*)$",
    "out": "\\[发送\\]\\s*(?P<target>group|private|群|私聊)\\s*:?\\s*(?P<cid>\\d+)\\s*->\\s*(?P<text>.*)$"
  }
}
```

如果格式不一样（比如 go-cqhttp 的 `[INFO]` 日志），只改这两个正则就行，命名捕获组合需要这几个：

| 捕获组 | 含义 | 必需 |
|---|---|---|
| `cid` | 群号或 QQ 号 | ✅ |
| `ck` 或 `target` | `群`/`私聊` 或 `group`/`private` | ✅ |
| `who` | 谁说的 | 可选 |
| `text` | 消息内容 | ✅ |

### 方式 B：任何机器人（推荐，最通用）

不用碰日志，让机器人往控制台**推事件**：

```bash
# 单条
curl -X POST "http://127.0.0.1:8090/api/ingest?token=<TOKEN>" \
  -H 'Content-Type: application/json' \
  -d '{"chat":{"type":"group","id":200000001},"who":"小明","text":"在吗","dir":"in"}'

# 批量
curl -X POST "http://127.0.0.1:8090/api/ingest?token=<TOKEN>" \
  -H 'Content-Type: application/json' \
  -d '{"events":[
        {"group_id":200000001,"who":"小明","text":"在吗","dir":"in"},
        {"chat":"私聊 100000001","text":"在的","dir":"out"}
      ]}'
```

字段很宽松，认这几种写法：

| 字段 | 可用写法 |
|---|---|
| `chat` | `"群 123"` / `"私聊 456"` / `{"type":"group","id":123}`，或用 `group_id` / `user_id` |
| `dir` | `in` / `out`（也可写 `send`、`sent`、`self`） |
| `text` | 也可写 `message` |
| `who` | 也可写 `nickname` / `sender` |
| `ts` | 可省略，服务器补当前时间 |

Python 上报示例（挂在你的机器人收消息回调里）：

```python
import requests
def on_message(chat_type, chat_id, nick, text, is_self=False):
    requests.post("http://127.0.0.1:8090/api/ingest",
                  params={"token": "你的TOKEN"},
                  json={"chat": {"type": chat_type, "id": chat_id},
                        "who": nick, "text": text,
                        "dir": "out" if is_self else "in"}, timeout=3)
```

Node 上报示例：

```js
await fetch("http://127.0.0.1:8090/api/ingest?token=" + TOKEN, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ chat: { type: "group", id: 200000001 },
                         who: "小明", text: "在吗", dir: "in" }),
});
```

用这个方式时，配置里把日志关掉更干净：

```json
"log": { "source": "ingest" },
"service": "",
"container": "",
"qr": { "mode": "off" }
```

现成的放在 `presets/ingest-only.json`。

### 方式 C：只想「替机器人发消息」

那就只用到 OneBot：

```json
"onebot": { "http": "http://你的onebot:3000", "token": "OneBot 的 token" }
```

任何 OneBot v11 实现都行（NapCat / LLOneBot / Lagrange / go-cqhttp）。
发语音需要对方支持 `record` 消息段。

### 方式 D：用控制台调她的说话方式（/api/botctl）

电脑端左侧「机器人控件」页，或直接调接口，可以给每个群单独设：

| 控件 | 效果 |
|---|---|
| 仅接受 @ 后回复 | 这个群里只在她被 @ 时说话 |
| 回复速度 | 0.3x~3.0x，调的是「打字等待」的倍率 |
| 句式长度 | 14 / 26 / 40 字，超出截断 |

存进 `data/group_rules.json`，改完立刻生效（不用重启）。

### 方式 C+：网页版控制台也能指向任何后端

`server/static/index.html` 是纯静态页面，**可以单独托管**（放 GitHub Pages / Cloudflare Pages / 本地双击都行），
页面里点「连接设置」填两样就能用：

- **接口地址**：`http://你的服务器:8090`（留空 = 同源）
- **Token**：`config.json` 里的 `token`

也可以直接用网址参数打开一次，之后记在浏览器本地：

```
https://你的页面/?api=http://1.2.3.4:8090&token=xxxx
```

首页会显示 `/api/info` 的「接入信息」：服务名、协议端容器、OneBot 地址、日志来源、事件上报文件、已有多少条消息 —— 一眼能看出到底连上了什么。

> 跨域部署时后端要允许 CORS。本项目后端默认只给同源用；如果页面和后端不同域，
> 在后端前面加一层反代（Nginx `add_header Access-Control-Allow-Origin *;`）即可。

### 接好之后怎么确认

```bash
curl "http://127.0.0.1:8090/api/info?token=<TOKEN>" | python3 -m json.tool
```

返回里能直接看到：连的哪个服务/容器、OneBot 地址、日志来源、ingest 文件、**现在能读到多少条消息**。

---

## 快速开始

### 1. 后端（server）

```bash
# 只需要 Python 3.10+，没有第三方依赖
cd server
cp ../config.example.json config.json     # 然后改 token / service / onebot
python3 app.py                             # 默认监听 0.0.0.0:8090
```

也可以全部用环境变量（优先级最高）：

```bash
FATBOT_TOKEN='你自己的随机串' \
FATBOT_SERVICE='fishbot' \
FATBOT_CONTAINER='snowluma' \
FISH_OB_HTTP='http://127.0.0.1:3000' \
python3 app.py
```

自检：

```bash
curl "http://127.0.0.1:8090/api/health"
curl "http://127.0.0.1:8090/api/status?token=$FATBOT_TOKEN"
```

接口清单见 [docs/API.md](docs/API.md)。

### 2. 电脑端（desktop）

需要 .NET Framework 4.0 的 `csc.exe`（Windows 自带）：

```powershell
cd desktop
powershell -ExecutionPolicy Bypass -File build.ps1
# 产物：肥鱼机器人开关.exe
```

运行前设两个环境变量（或在程序「设置」页里填）：

| 变量 | 说明 |
|---|---|
| `FATBOT_CONSOLE_TOKEN` | 和后端一致的 token |
| 服务器地址 | 程序内「设置」里填 `user@host`，走 SSH 执行命令 |

> 电脑端是通过 **SSH** 在服务器上执行命令/curl 的，所以本机不需要能直接访问 8090。

### 3. 手机端（android）

不需要 Gradle，用 Android SDK build-tools 手工打包（脚本已封装）：

```powershell
cd android
powershell -ExecutionPolicy Bypass -File build-apk.ps1
# 产物：app-release.apk
```

改这三处常量即可接你自己的后端：

```java
// MainActivity.java
static final String BASE  = "http://your-server:8090";
static final String TOKEN = "换成你自己的 token";
```

需要：`aapt2 / javac / d8 / zipalign / apksigner`（build-tools 35 实测可用），JDK 17+。
注意：源码路径不要含中文，aapt2 会失败。

### 4. 语音（voice）

```bash
pip install pilk requests edge-tts     # ffmpeg 需要另外装

export FISH_SF_KEY='你的 SiliconFlow Key'

# 合成（零样本克隆音色）
python3 voice/sf_tts.py out.mp3 "要念的文本" \
  "speech:音色名:…:…" FunAudioLLM/CosyVoice2-0.5B 1.08 1.00

# 转 QQ 原生语音编码
python3 voice/to_silk.py out.mp3 out.silk 24000
```

为什么要转 SILK：直接把 mp3 当语音消息发出去，客户端会二次转码，听起来像劣质外放；SILK 是 QQ/微信的原生语音编码，体积更小、听感正常。

**音色克隆**（SiliconFlow）：

```bash
# 1) 素材处理：去首尾静音 + 轻降噪 + 标准化 + 16k 单声道
ffmpeg -i ref.mp3 \
  -af "silenceremove=start_periods=1:start_threshold=-45dB,\
highpass=f=90,afftdn=nr=18:nf=-30,lowpass=f=7800,loudnorm=I=-18:TP=-2:LRA=11" \
  -ar 16000 -ac 1 ref_clean.mp3

# 2) 转写（text 参数必须和素材真实发音一致）
curl -s -X POST https://api.siliconflow.cn/v1/audio/transcriptions \
  -H "Authorization: Bearer $FISH_SF_KEY" \
  -F model=FunAudioLLM/SenseVoiceSmall -F file=@ref_clean.mp3

# 3) 上传克隆
curl -s -X POST https://api.siliconflow.cn/v1/uploads/audio/voice \
  -H "Authorization: Bearer $FISH_SF_KEY" \
  -F model=FunAudioLLM/CosyVoice2-0.5B \
  -F customName=my-voice -F "text=<上一步的转写>" -F file=@ref_clean.mp3
```

经验：

- **中文音色必须用中文素材**。用日语素材克隆出来的音色念中文，同一句生成长度会 4s↔16s 乱跳，识别全是乱码。
- 语速别低于 1.05，1.02 时模型会随机把句子念断。
- 合成完在尾部补 0.45s 静音，否则客户端转码会切掉最后一个字。
- 验证方法：把发出去的语音用 ASR 转写，能念回来才算正常。

---

## 环境变量一览

| 变量 | 作用 |
|---|---|
| `FATBOT_TOKEN` | 后端接口鉴权 token（server/app.py 读取） |
| `FATBOT_CONSOLE_TOKEN` | 电脑端调用后端用的 token |
| `FISH_OB_HTTP` | OneBot HTTP 地址，默认 `http://127.0.0.1:3000` |
| `FISH_OB_TOKEN` | OneBot HTTP 鉴权 token |
| `FATBOT_CONFIG` | 配置文件路径（默认 `server/config.json`） |
| `FATBOT_SERVICE` | 覆盖 systemd 服务名 |
| `FATBOT_CONTAINER` | 覆盖协议端容器名 |
| `FISH_SF_KEY` | SiliconFlow API Key（语音合成） |
| `XF_APPID` / `XF_APIKEY` / `XF_APISECRET` | 讯飞 TTS 凭据（仅备用引擎需要） |

---

## 设计上的几个取舍

| 问题 | 做法 |
|---|---|
| 手机端为什么不用 WebView | 掉线时 WebView 打不开就等于没控制台，原生列表/气泡更靠得住 |
| 为什么不用 Gradle | 手工 aapt2→javac→d8→zipalign→apksigner，一条脚本、无网络依赖 |
| 消息从哪来 | 读 systemd 日志里的 `[接收]` / `[发送]` 行 + 一份手动发送日志，正则解析成结构化消息 |
| 会话怎么分 | 按 `群 <id>` / `私聊 <id>` 聚合，按最后一条时间倒序 |
| 为什么关心「内层滚动」 | 聊天区嵌在外层 ScrollView 里，不抢手势父容器会先把滑动吃掉 |
| 为什么不直接读协议端数据库 | 不是每家都有；日志 + ingest 两路已经够用，也不碰别人的数据 |
| 为什么要 `/api/ingest` | 日志格式千奇百怪，推事件是唯一真正通用的接法 |

---

## 许可

MIT，见 [LICENSE](LICENSE)。

第三方：`pilk`（SILK 编解码）、`ffmpeg`、SiliconFlow / 讯飞 / Edge 的 TTS 服务，请遵循各自许可与服务条款。
