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

| 想改 | 改哪里 |
|---|---|
| 端口 | `app.py` 里的 `PORT` 或 `FATBOT_PORT` |
| token | 环境变量 `FATBOT_TOKEN` |
| OneBot 地址 | `FISH_OB_HTTP` / `FISH_OB_TOKEN` |
| 日志来源 | `last_lines()` 里的 journalctl 参数 |
| 手动发送日志路径 | `SENT_LOG` |
