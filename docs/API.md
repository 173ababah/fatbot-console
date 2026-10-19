# 后端接口（server/app.py）

基址：`http://<host>:8090`
鉴权：除 `/api/health` 外，所有请求都要带 `?token=<FATBOT_TOKEN>`

| 接口 | 方法 | 说明 |
|---|---|---|
| `/api/health` | GET | 存活探测，不需要 token |
| `/api/status` | GET | 服务状态、容器状态、内存、启动时间 |
| `/api/diag` | GET | 一句话诊断 + 技术细节 |
| `/api/selfcheck` | GET | 8 项自检（服务/容器/登录/收发/看门狗…） |
| `/api/logs` | GET | 运行日志尾部 |
| `/api/runlog` | GET | systemd 日志尾部 |
| `/api/qr` | GET | 登录二维码 PNG；`?fresh=1` 先点刷新再截 |
| `/api/desktop` | GET | 协议端容器桌面截图 |
| `/api/action` | POST | `{"action":"start"\|"stop"\|"restart"\|"restartnapcat"}` |
| `/api/conversations` | GET | 会话列表（按最后消息倒序） |
| `/api/messages` | GET | 消息列表，`?chat=群 123&limit=150` |
| `/api/send` | POST | 主动发送：`{"chat","text","voice"}` |
| `/api/ingest` | POST | **外部机器人上报事件**（任何框架都能接） |
| `/api/info` | GET | 当前接入信息（服务/容器/OneBot/日志来源/消息条数，不含密钥） |
| `/api/groups` | GET | 群列表（带每群当前规则），给「机器人控件」页用 |
| `/api/botctl` | GET | 读某个群的规则：`?chat=群 123` |
| `/api/botctl` | POST | 写某个群的规则：`{"chat","only_at","max_len","speed","chat_rate"}` |

## 会话与消息的数据结构

```json
{
  "items": [
    {"chat": "群 200000001", "count": 42, "last": "好哒", "ts": "15:27:07", "dir": "in"}
  ]
}
```

```json
{
  "messages": [
    {"dir": "in",  "chat": "群 200000001", "who": "小明", "text": "好哒",
     "kind": "text", "ts": "15:27:07"},
    {"dir": "out", "chat": "私聊 100000001", "who": "我", "text": "【语音】晚安呀",
     "kind": "text", "ts": "15:28:10"}
  ]
}
```

- `dir`：`in` 收到的 / `out` 她发的
- `chat`：`群 <群号>` 或 `私聊 <QQ号>`
- `kind`：`text` / `media`（图片、表情）

## 主动发送

```bash
T=<你的 token>

# 文字
curl -X POST -H 'Content-Type: application/json' \
  -d '{"chat":"群 200000001","text":"我回来啦"}' \
  "http://127.0.0.1:8090/api/send?token=$T"

# 语音（服务端合成 → 转 SILK → 发送）
curl -X POST -H 'Content-Type: application/json' \
  -d '{"chat":"私聊 100000001","text":"晚安呀","voice":true}' \
  "http://127.0.0.1:8090/api/send?token=$T"
```

返回：

```json
{"ok": true, "msg": "已发送", "message_id": 123456}
{"ok": true, "msg": "语音已发送", "path": "/app/data/voice/xxx.silk"}
{"ok": false, "msg": "会话格式不对：xxx"}
```

## 上报事件（/api/ingest）

任何机器人只要发一条 POST 就能进聊天记录，**不需要日志格式**。

```bash
curl -X POST "http://127.0.0.1:8090/api/ingest?token=<TOKEN>" \
  -H 'Content-Type: application/json' \
  -d '{"chat":{"type":"group","id":200000001},"who":"小明","text":"在吗","dir":"in"}'
# → {"ok": true, "written": 1}
```

批量：

```json
{"events":[
  {"group_id":200000001,"who":"小明","text":"在吗","dir":"in"},
  {"chat":"私聊 100000001","text":"在的","dir":"out","ts":"21:03:11"}
]}
```

字段容错：

| 想要 | 可以写 |
|---|---|
| 会话 | `chat: "群 123"` / `{"type":"group","id":123}` / `group_id: 123` / `user_id: 456` |
| 方向 | `dir: "in" | "out" | "send" | "sent" | "self"` |
| 内容 | `text` 或 `message` |
| 说话人 | `who` / `nickname` / `sender` |
| 时间 | `ts`（可省略，默认服务器当前时间） |

事件落盘在 `ingest.file`（默认 `data/ingest.jsonl`，一行一条 JSON），所以重启不丢、也能直接 `tail -f` 看。

## 接入信息（/api/info）

```bash
curl "http://127.0.0.1:8090/api/info?token=<TOKEN>"
```

```json
{
  "service": "fishbot", "container": "snowluma",
  "onebot": "http://127.0.0.1:3000",
  "config_file": "/opt/fatbot-console/server/config.json",
  "log": {"source": "journal", "journal_unit": "fishbot", "since_hours": 6,
          "max_lines": 800, "patterns": ["in", "out"]},
  "ingest": {"enabled": true, "file": "/opt/qqbot/data/ingest.jsonl"},
  "qr": {"mode": "screen"}, "voice": {"engine": "siliconflow"},
  "counts": {"messages": 128, "conversations": 5}
}
```

`counts` 是你排查「为什么没消息」的第一站：为 0 就说明日志没匹配上或 ingest 没进来。

## 机器人控件（/api/botctl）

每个群可以单独调三件事，写进 `group_rules.json`：

| 字段 | 类型 | 说明 |
|---|---|---|
| `only_at` | bool | 只在她被 @ 时回复 |
| `max_len` | int | 回复最大字数（超出截断），常用 14 / 26 / 40 |
| `speed` | float | 回复速度倍率：1.0 默认，<1 更快，>1 更慢（0.3~3.0） |
| `chat_rate` | float | 聊天频率：没人叫她时主动接话的概率 0~1；`-1` = 跟随全局 |

```bash
# 读
curl "http://127.0.0.1:8090/api/botctl?token=<TOKEN>&chat=%E7%BE%A4%20123456789"

# 写（只改传了的字段）
curl -X POST "http://127.0.0.1:8090/api/botctl?token=<TOKEN>" \
  -H 'Content-Type: application/json' \
  -d '{"chat":"群 123456789","only_at":true,"max_len":26,"speed":1.2}'
```

```json
{"ok": true, "chat": "群 123456789", "gid": "123456789",
 "rules": {"only_at": true, "max_len": 26, "speed": 1.2, "chat_rate": 0.3}}
```

只有**群**有这些规则（私聊读不到、写不了）。

## 消息是怎么被解析出来的

后端不存数据库，直接从两处读：

1. systemd 日志里业务打印的行：

```
[接收] 群 200000001 小明 -> 好哒
[接收] 私聊 100000001 小明 -> 在吗
[发送] group:200000001 -> 我回来啦
[发送] private:100000001 -> 【语音】晚安呀
```

2. 手动发送日志（控制台/手机发的消息会追加，保证两端都能看到）：

```
2026-10-19 15:21:30 [发送] private:100000001 -> 测试消息
```

解析后按时间排序、按 `chat` 聚合，就是 `/api/conversations` 和 `/api/messages` 的数据。

## 自定义

全部在 `config.json`（或用同名环境变量覆盖）：

| 想改 | 字段 |
|---|---|
| 端口 / token | `port` / `token` |
| 服务名 / 容器名 | `service` / `container` |
| OneBot | `onebot.http` / `onebot.token` |
| 日志来源 | `log.source` = `journal` / `file` / `ingest`（可组合，如 `journal+ingest`） |
| 日志格式 | `log.patterns.in` / `log.patterns.out`（正则，命名捕获组） |
| 读取时间范围 / 行数 | `log.since_hours` / `log.max_lines` |
| 上报文件 | `ingest.file` |
| 二维码取法 | `qr.mode` = `screen` / `off`，以及 `crop` / `scale` / `click` |
| 语音 | `voice.*`（`engine=off` 关闭） |
