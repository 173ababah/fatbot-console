#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""肥鱼控制台 · 手机版 Web 后端（只用标准库）

监听 0.0.0.0:8090，带 token 鉴权，给手机 APK / 浏览器用。
所有操作都在本机执行（systemctl / docker / journalctl），不需要 SSH。
"""
import base64
import datetime
import urllib.request
import hmac
import io
import json
import os
import re
import shlex
import subprocess
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

HERE = os.path.dirname(os.path.abspath(__file__))
STATIC = os.path.join(HERE, "static")

# ---------------------------------------------------------------- 配置
# 优先级：环境变量 > config.json > 下面这份默认值
DEFAULTS = {
    "port": 8090,
    "token": "change-me-please",
    "cors": False,                        # 网页版单独托管时打开
    "bot_dir": "/opt/qqbot",              # 机器人程序目录（放 data/、.env 等）
    "service": "fishbot",                 # systemd 服务名
    "container": "",                      # 协议端容器名，空=不用 docker
    "health_log": "",                     # 看门狗日志，空=不读
    "sent_log": "",                       # 手动发送日志，空=不读
    "onebot": {
        "http": "http://127.0.0.1:3000",
        "token": ""
    },
    "log": {
        # 日志来源：journal / file / ingest / 组合（journal+ingest 等）
        "source": "journal",
        "journal_unit": "",               # 空 = 用 service
        "file": "",                       # source 含 file 时读这个文件
        "since_hours": 6,
        "max_lines": 800,
        "patterns": {
            "in":  r"\[接收\]\s*(?P<ck>群|私聊)\s*(?P<cid>\d+)?\s*(?P<who>.*?)\s*->\s*(?P<text>.*)$",
            "out": r"\[发送\]\s*(?P<target>group|private|群|私聊)\s*:?\s*(?P<cid>\d+)\s*->\s*(?P<text>.*)$"
        }
    },
    "ingest": {
        "enabled": True,
        "file": "data/ingest.jsonl"
    },
    "qr": {
        "mode": "screen",                 # screen / off
        "display": ":1",
        "user": "",                       # docker exec -u
        "crop": "220:220:852:434",
        "scale": "660:660",
        "click": "961 581"
    },
    "voice": {
        "engine": "siliconflow",          # siliconflow / off
        "dir": "/app/data/voice",
        "py": "/opt/voice/.venv/bin/python",
        "bin": "/opt/voice/sf_tts.py",
        "silk_bin": "/opt/voice/to_silk.py",
        "voice": "",
        "model": "FunAudioLLM/CosyVoice2-0.5B",
        "speed": "1.08",
        "pitch": "1.00"
    }
}


def _deep_merge(base, over):
    out = dict(base)
    for k, v in (over or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def _load_config():
    path = os.environ.get("FATBOT_CONFIG") or os.path.join(HERE, "config.json")
    cfg = dict(DEFAULTS)
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            cfg = _deep_merge(cfg, json.load(f))
    except (OSError, ValueError):
        pass
    # 环境变量覆盖（方便用 systemd Environment= 起）
    if os.environ.get("FATBOT_TOKEN"):
        cfg["token"] = os.environ["FATBOT_TOKEN"]
    if os.environ.get("FATBOT_PORT"):
        cfg["port"] = int(os.environ["FATBOT_PORT"])
    if os.environ.get("FATBOT_SERVICE"):
        cfg["service"] = os.environ["FATBOT_SERVICE"]
    if os.environ.get("FATBOT_CONTAINER"):
        cfg["container"] = os.environ["FATBOT_CONTAINER"]
    if os.environ.get("FISH_OB_HTTP"):
        cfg["onebot"]["http"] = os.environ["FISH_OB_HTTP"]
    if os.environ.get("FISH_OB_TOKEN"):
        cfg["onebot"]["token"] = os.environ["FISH_OB_TOKEN"]
    if os.environ.get("FISH_VOICE_SF_VOICE"):
        cfg["voice"]["voice"] = os.environ["FISH_VOICE_SF_VOICE"]
    cfg["config_file"] = path
    return cfg


CFG = _load_config()
TOKEN = CFG["token"]
PORT = int(CFG["port"])
SERVICE = CFG["service"] or ""
BOT_CONTAINER = CFG["container"] or ""
BOT_DIR = CFG["bot_dir"]
QR_LOCAL = "/tmp/fatbot_qr.png"
DESKTOP_LOCAL = "/tmp/fatbot_desktop.png"
HEALTH_LOG = CFG["health_log"]


def _under(base, rel):
    return rel if os.path.isabs(rel) else os.path.join(base, rel)


SENT_LOG = _under(BOT_DIR, CFG["sent_log"]) if CFG["sent_log"] else ""
INGEST_FILE = _under(BOT_DIR, CFG["ingest"]["file"]) if CFG["ingest"]["file"] else ""
QR_CROP = "crop=%s,scale=%s:flags=neighbor" % (CFG["qr"]["crop"], CFG["qr"]["scale"])
QR_CLICK = CFG["qr"]["click"]


def run(cmd, timeout=25):
    try:
        p = subprocess.run(cmd, shell=isinstance(cmd, str), capture_output=True,
                           timeout=timeout, text=True)
        return (p.stdout or "") + (p.stderr or "")
    except subprocess.TimeoutExpired:
        return "（超时）"
    except Exception as e:
        return "（失败：%r）" % (e,)


SUDO = "sudo -n "


def svc_state():
    if not SERVICE:
        return "n/a"      # 没配服务名（比如用 ingest 的纯外部机器人）
    return run("systemctl is-active " + SERVICE).strip()


def napcat_running():
    out = run(SUDO + "docker inspect -f '{{.State.Running}}' " + BOT_CONTAINER + " 2>/dev/null", timeout=10)
    return "true" in out.lower()


def last_lines(pat, since="30 min ago", limit=80):
    q = SUDO + "journalctl -u " + SERVICE + " --since '%s' --no-pager" % since
    out = run(q + " | grep -F -e %s | tail -%d" % (pat, limit), timeout=30)
    return [x for x in out.replace("\r", "").split("\n") if x.strip()]


def status():
    svc = svc_state()
    nap = napcat_running()
    mem = ""
    try:
        parts = run("free -m").split("\n")[1].split()
        mem = "%s/%s MB" % (parts[2], parts[1])
    except Exception:
        mem = ""
    up = run("systemctl show " + SERVICE + " -p ActiveEnterTimestamp --value").strip()
    conn = 0
    # 一次 journalctl 抓完，别分两次（每次 grep 都要重扫日志，很慢）
    both = last_lines("'[接收]' -e '[发送]'", since="30 min ago", limit=200)
    rx = [x for x in both if "[接收]" in x]
    tx = [x for x in both if "[发送]" in x]
    return {
        "svc": svc, "napcat": nap, "mem": mem, "up": up,
        "connects": conn,
        "rx": len(rx), "tx": len(tx),
        "last_rx": rx[-1] if rx else "", "last_tx": tx[-1] if tx else "",
        "ts": time.strftime("%H:%M:%S"),
    }


def _docker(args, timeout=25):
    """有容器才执行 docker 命令，否则返回空串"""
    if not BOT_CONTAINER:
        return ""
    return run(SUDO + "docker " + args, timeout=timeout)


def diag():
    st = status()
    qr_age = -1
    kick = 0
    if BOT_CONTAINER:
        try:
            out = _docker("exec %s sh -c 'stat -c %%Y /tmp/fishqr.png' 2>/dev/null" % BOT_CONTAINER, 15)
            m = re.search(r"(\d{9,})", out or "")
            if m:
                qr_age = int(time.time()) - int(m.group(1))
        except Exception:
            pass
        try:
            cnt = _docker("logs --since 10m %s 2>&1 | grep -c KickedOffLine" % BOT_CONTAINER, 25).strip()
            kick = int(cnt or 0)
        except (TypeError, ValueError):
            kick = 0
    if not SERVICE:
        verdict, hint = "已连上控制台（未配置服务名）", "只做转发与记录，不检查 systemd 服务"
    elif st["svc"] != "active":
        verdict, hint = "不在线：机器人服务停了", "点下面的「启动服务」"
    elif not st["napcat"]:
        verdict, hint = "不在线：协议端容器没起来", "点「重启协议端」"
    elif st["tx"] > 0 or st["rx"] > 0:
        verdict, hint = "在线，正在正常收发", ""
    elif kick > 0:
        verdict, hint = "不在线：账号被踢下线", "去「二维码」页扫码重新登录"
    elif 0 <= qr_age < 900:
        verdict, hint = "不在线：卡在扫码登录", "去「二维码」页扫码"
    else:
        verdict, hint = "不在线：OneBot 没连上", "检查 NapCat 是否已登录"
    return {"verdict": verdict, "hint": hint, "qr_age": qr_age, "kick": kick, "status": st}




def _exec_in_container(script, timeout=25):
    """在协议端容器里跑一段 shell（QR/桌面截图用）"""
    if not BOT_CONTAINER:
        return ""
    user = ("-u %s " % CFG["qr"]["user"]) if CFG["qr"]["user"] else ""
    disp = CFG["qr"]["display"]
    return run(SUDO + 'docker exec %s-e DISPLAY=%s %s sh -lc "%s"'
               % (user, disp, BOT_CONTAINER, script), timeout=timeout)


def click_qr_refresh():
    """点一下协议端登录窗里的「刷新」按钮（坐标来自 config.qr.click）"""
    click = CFG["qr"].get("click") or ""
    if not click:
        return
    _exec_in_container("xdotool mousemove %s click 1" % click, timeout=25)
    time.sleep(2.5)


def _grab(path_in_container, vf=None, timeout=40):
    """容器里 ffmpeg 截一张图，拷到本机，返回字节"""
    vf = ("-vf '%s' " % vf) if vf else ""
    script = ("ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
              + vf + "-frames:v 1 -y " + path_in_container)
    _exec_in_container(script, timeout=timeout)
    run(SUDO + "docker cp %s:%s %s" % (BOT_CONTAINER, path_in_container,
                                      QR_LOCAL if "qr" in path_in_container else DESKTOP_LOCAL),
        timeout=20)
    dest = QR_LOCAL if "qr" in path_in_container else DESKTOP_LOCAL
    if os.path.exists(dest) and os.path.getsize(dest) > 2000:
        with open(dest, "rb") as f:
            return f.read()
    return None


def grab_qr(fresh=False):
    """从协议端容器里的 QQ 窗口截屏，裁出二维码那块（qr.mode=screen 时用）"""
    if not BOT_CONTAINER:
        return None
    if fresh:
        click_qr_refresh()
    _exec_in_container("xdotool mousemove 1500 950", timeout=15)   # 鼠标挪开别挡码
    return _grab("/tmp/fishqr.png", QR_CROP)


def grab_desktop():
    """整个远程桌面（需要手动处理验证码/滑块时用）"""
    if not BOT_CONTAINER:
        return None
    return _grab("/tmp/fishdesk.png")


def health():
    """看门狗（fishdog.timer）状态：自愈了几次、最后一次是什么时候。"""
    lines = []
    try:
        with open(HEALTH_LOG, "r", encoding="utf-8", errors="ignore") as f:
            lines = [x.strip() for x in f.read().splitlines() if x.strip()]
    except Exception:
        pass
    acts = [x for x in lines if ("重启 napcat" in x) or ("执行 start" in x)]
    return {
        "timer": run("systemctl is-active fishdog.timer").strip(),
        "count": len(acts),
        "last": acts[-1] if acts else "",
        "tail": lines[-6:],
    }


def env_of_container():
    return run(SUDO + "docker inspect napcat --format '{{range .Config.Env}}{{println .}}{{end}}'", timeout=20)


def count_lines(pattern, since):
    out = run(SUDO + "journalctl -u " + SERVICE + " --since '%s' --no-pager 2>/dev/null | grep -cF %s" % (since, pattern), timeout=25)
    try:
        return int(out.strip().splitlines()[-1])
    except Exception:
        return 0


def conn_state():
    """最后一个连接相关事件是「已连接」还是「重连中」。"""
    out = run(SUDO + "journalctl -u " + SERVICE + " --since '25 min ago' --no-pager 2>/dev/null "
                    "| grep -E 'connected|Trying to reconnect' | tail -1", timeout=25)
    return "connected" in out


def qr_age():
    """-1 表示没在等扫码（已登录）；否则返回容器启动后多少秒。"""
    if bot_logged_in():
        return -1
    try:
        out = run(SUDO + "docker inspect %s --format '{{.State.StartedAt}}'" % BOT_CONTAINER, timeout=15)
        m = re.search(r"(\d{4}-\d{2}-\d{2}T[\d:]+)", out)
        if m:
            import datetime
            t = datetime.datetime.strptime(m.group(1), "%Y-%m-%dT%H:%M:%S")
            return max(0, int(time.time() - t.replace(tzinfo=None).timestamp() + 8 * 3600 - 8 * 3600))
    except Exception:
        pass
    return 60


def bot_logged_in():
    """日志里出现 hook 工作模式 / 收到消息，就算登录成功。"""
    out = run(SUDO + "docker logs --tail 60 %s 2>&1 | grep -cE 'awaiting login' " % BOT_CONTAINER, timeout=25)
    try:
        waiting = int(out.strip().splitlines()[-1])
    except Exception:
        waiting = 0
    return waiting == 0


def selfcheck():
    """手机版自检：像电脑上那张卡片一样，一条条打勾。"""
    st = status()
    has_pw = bot_logged_in()
    rx5 = count_lines("'[接收]'", "5 min ago")
    tx5 = count_lines("'[发送]'", "5 min ago")
    recon = count_lines("'Trying to reconnect'", "10 min ago")
    linked = conn_state()
    conn20 = count_lines("'connected'", "20 min ago")
    kick = count_lines("'KickedOffLine'", "30 min ago")
    age = qr_age()
    h = health()

    items = []

    def add(ok, name, detail):
        items.append({"ok": bool(ok), "name": name, "detail": detail})

    add(st["svc"] == "active", "机器人服务 " + SERVICE,
        "运行中" if st["svc"] == "active" else st["svc"])
    add(st["napcat"], (BOT_CONTAINER or "协议端") + " 容器", "在跑" if st["napcat"] else "没起来")
    add(has_pw, "账号已登录", "已登录（会话保存在容器卷里）" if has_pw else "还没登录，去「扫码」页")
    add(linked, "OneBot 连接",
        "已连上" if linked else ("正在重连（%d 次 / 10 分钟）" % recon if recon else "没有连接记录"))
    add((rx5 + tx5) > 0, "最近 5 分钟收发", "收 %d / 发 %d" % (rx5, tx5))
    add(age < 0 or age >= 900, "登录二维码",
        "没有待扫的码" if age < 0 else ("%d 秒前生成（大概率过期）" % age))
    add(kick == 0, "被踢记录", "30 分钟内没有被踢" if kick == 0 else "30 分钟内被踢 %d 次" % kick)
    add(h["timer"] == "active", "看门狗（每 3 分钟）",
        ("运行中，已自愈 %d 次" % h["count"]) if h["timer"] == "active" else "没在跑")

    busy = (rx5 + tx5) > 0
    if not SERVICE:
        verdict, hint = "已连上控制台（未配置服务名）", "只做转发与记录，不检查 systemd 服务"
    elif st["svc"] != "active":
        verdict, hint = "不在线：机器人服务停了", "点下面的「启动服务」"
    elif not st["napcat"]:
        verdict, hint = "不在线：协议端容器没起来", "点「重启协议端」"
    elif busy:
        verdict, hint = "在线，正在正常收发", "5 分钟内还说过话，不用管"
    elif (not linked) and recon >= 3 and has_pw:
        verdict, hint = "正在自愈：掉线后自动重登", "等 1 分钟，看门狗会自动重启 NapCat"
    elif kick > 0:
        verdict, hint = "不在线：账号被踢下线", "密码登录会尝试救回；救不回就去「扫码登录」"
    elif has_pw:
        verdict, hint = "在线（密码登录备用中）", "群里没人说话而已"
    else:
        verdict, hint = "不在线：OneBot 没连上", "去「扫码登录」扫一下"

    return {"verdict": verdict, "hint": hint, "items": items, "health": h,
            "ts": time.strftime("%H:%M:%S")}



_MSG_CACHE = {"ts": 0.0, "msgs": []}
MSG_TTL = 15


def _log_cfg():
    return CFG["log"]


def _since_arg():
    h = _log_cfg().get("since_hours", 6) or 6
    try:
        h = float(h)
    except (TypeError, ValueError):
        h = 6
    if h <= 24:
        return "%g hours ago" % h
    return "%g days ago" % (h / 24.0)


def _read_journal(limit):
    """从 journalctl 读日志行（失败返回空列表）"""
    unit = _log_cfg().get("journal_unit") or SERVICE
    if not unit:
        return []
    cmd = (SUDO + "journalctl -u " + unit + " --since '" + _since_arg() +
           "' --no-pager -n " + str(int(limit)) + " 2>/dev/null")
    out = run(cmd, timeout=40)
    if not out or out.startswith("（"):
        return []
    return [x for x in out.replace("\r", "").split("\n") if x.strip()]


def _read_logfile(limit):
    path = _log_cfg().get("file") or ""
    if not path:
        return []
    path = _under(BOT_DIR, path)
    try:
        with open(path, "r", encoding="utf-8", errors="ignore") as f:
            return [x.rstrip("\n") for x in f.readlines()[-int(limit):] if x.strip()]
    except OSError:
        return []


def _read_ingest(limit=1500):
    """机器人主动推过来的事件（JSONL），一条一行"""
    if not CFG["ingest"].get("enabled", True) or not INGEST_FILE:
        return []
    try:
        with open(INGEST_FILE, "r", encoding="utf-8", errors="ignore") as f:
            return [x.rstrip("\n") for x in f.readlines()[-int(limit):] if x.strip()]
    except OSError:
        return []


def _event_from_ingest(obj):
    """把 ingest 的一条 JSON 变成内部消息格式"""
    if not isinstance(obj, dict):
        return None
    direction = "out" if str(obj.get("dir") or obj.get("direction") or "in").lower() in (
        "out", "send", "sent", "self") else "in"
    # chat 支持三种写法：字符串 "群 123" / {"type":"group","id":123} / group_id / user_id
    chat = obj.get("chat")
    chat_name = ""
    if isinstance(chat, dict):
        t = str(chat.get("type") or "")
        cid = chat.get("id")
        chat_name = ("群 " if t in ("group", "群") else "私聊 ") + str(cid or "")
    elif isinstance(chat, str) and chat.strip():
        chat_name = re.sub(r"^(group|群):\s*", "群 ", chat.strip())
        chat_name = re.sub(r"^(private|私聊):\s*", "私聊 ", chat_name)
    elif obj.get("group_id"):
        chat_name = "群 " + str(obj["group_id"])
    elif obj.get("user_id"):
        chat_name = "私聊 " + str(obj["user_id"])

    text = str(obj.get("text") or obj.get("message") or "")
    who = str(obj.get("who") or obj.get("nickname") or obj.get("sender") or
              ("我" if direction == "out" else ""))
    kind = str(obj.get("kind") or "text")
    if kind not in ("text", "media"):
        kind = "text"
    ts = str(obj.get("ts") or obj.get("time") or "")
    m = re.search(r"(\d{2}:\d{2}:\d{2})", ts)
    if m:
        ts = m.group(1)
    elif not ts:
        ts = time.strftime("%H:%M:%S")
    return {"dir": direction, "chat": chat_name, "who": who,
            "text": text[:200], "kind": kind, "ts": ts,
            "dt": _dt_str(_dt_from_ts(ts))}


# journalctl 输出 `Oct 19 21:13:29 host unit[pid]: ...`
# 手动发送日志是 `2026-10-19 21:13:29 [发送] ...`
_MON = {"Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6,
        "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12}
_TS_FULL = re.compile(r"^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})")
_TS_JOURNAL = re.compile(r"^([A-Z][a-z]{2})\s+(\d{1,2})\s+(\d{2}):(\d{2}):(\d{2})")
_TS_HMS = re.compile(r"(\d{2}):(\d{2}):(\d{2})")


def _dt_from_line(line):
    """一行日志的真实时间（datetime）；认不出返回 None"""
    now = datetime.datetime.now()
    m = _TS_FULL.match(line)
    if m:
        try:
            return datetime.datetime(*[int(x) for x in m.groups()])
        except ValueError:
            return None
    m = _TS_JOURNAL.match(line)
    if m and _MON.get(m.group(1)):
        try:
            dt = datetime.datetime(now.year, _MON[m.group(1)], int(m.group(2)),
                                   int(m.group(3)), int(m.group(4)), int(m.group(5)))
            if dt - now > datetime.timedelta(days=2):        # 跨年
                dt = dt.replace(year=now.year - 1)
            return dt
        except ValueError:
            return None
    m = _TS_HMS.search(line)
    if m:
        try:
            return now.replace(hour=int(m.group(1)), minute=int(m.group(2)),
                               second=int(m.group(3)), microsecond=0)
        except ValueError:
            return None
    return None


def _dt_from_ts(ts):
    """只有 HH:MM:SS 时按今天补全"""
    if not ts:
        return None
    m = _TS_HMS.search(ts)
    if not m:
        return None
    try:
        return datetime.datetime.now().replace(hour=int(m.group(1)), minute=int(m.group(2)),
                                               second=int(m.group(3)), microsecond=0)
    except ValueError:
        return None


def _dt_str(dt):
    return dt.strftime("%Y-%m-%d %H:%M:%S") if dt else ""


def _compile_patterns():
    pats = []
    for direction, rx in (_log_cfg().get("patterns") or {}).items():
        try:
            pats.append((direction, re.compile(rx)))
        except re.error:
            continue
    return pats


def _event_from_line(ln, pats):
    """用配置里的正则把一行日志解析成消息；认不出来返回 None"""
    ts = ""
    m = re.search(r"(\d{2}:\d{2}:\d{2})", ln)
    if m:
        ts = m.group(1)
    for direction, rx in pats:
        mm = rx.search(ln)
        if not mm:
            continue
        g = mm.groupdict()
        ck = (g.get("ck") or g.get("target") or "").strip()
        cid = (g.get("cid") or "").strip()
        if not cid:
            continue
        if ck in ("群", "group", "g"):
            chat_name = "群 " + cid
        elif ck in ("私聊", "private", "p"):
            chat_name = "私聊 " + cid
        else:
            chat_name = (ck + " " + cid).strip()
        who = (g.get("who") or "").strip() or ("我" if direction == "out" else "")
        text = (g.get("text") or "").strip()
        kind = "text"
        if "【非文本消息】" in text or text.startswith("【image】") or "【表情包】" in text:
            kind = "media"
            text = "[表情]" if "表情" in text else "[图片]"
        return {"dir": direction, "chat": chat_name, "who": who,
                "text": text[:200], "kind": kind, "ts": ts,
                "dt": _dt_str(_dt_from_line(ln))}
    return None


def _parse_all(force=False):
    """把三处来源解析成消息列表：日志（journal/file）+ 手动发送日志 + ingest"""
    now = time.time()
    if not force and _MSG_CACHE["msgs"] and now - _MSG_CACHE["ts"] < MSG_TTL:
        return _MSG_CACHE["msgs"]

    limit = int(_log_cfg().get("max_lines", 800) or 800)
    source = str(_log_cfg().get("source", "journal")).lower()
    lines = []
    if "journal" in source:
        lines += _read_journal(limit)
    if "file" in source:
        lines += _read_logfile(limit)

    pats = _compile_patterns()
    msgs = []
    for ln in lines:
        ev = _event_from_line(ln, pats)
        if ev:
            msgs.append(ev)

    # 手动发送日志（控制台/手机发的，保证两端都能看到）
    if SENT_LOG:
        try:
            with open(SENT_LOG, "r", encoding="utf-8", errors="ignore") as f:
                for ln in f.readlines()[-400:]:
                    ev = _event_from_line(ln.rstrip("\n"), pats)
                    if ev:
                        ev["dir"] = "out"
                        msgs.append(ev)
        except OSError:
            pass

    # 机器人主动推过来的事件
    for ln in _read_ingest():
        try:
            ev = _event_from_ingest(json.loads(ln))
        except ValueError:
            continue
        if ev:
            msgs.append(ev)

    # 三处来源合并后按「真实时间」稳定排序。
    # 只按 HH:MM:SS 排会在跨零点时把昨天的消息排到今天后面（自己发的消息就跑到最下面了）
    indexed = [(0, ev.get("dt") or "", i, ev) if ev.get("dt") else (1, "", i, ev)
               for i, ev in enumerate(msgs)]
    indexed.sort(key=lambda t: (t[0], t[1], t[2]))
    msgs = [t[3] for t in indexed]
    _MSG_CACHE["msgs"] = msgs
    _MSG_CACHE["ts"] = now
    return msgs


def chat_messages(limit=120, only_chat=None):
    msgs = _parse_all()
    if only_chat:
        msgs = [m for m in msgs if m["chat"] == only_chat]
    return msgs[-limit:]


def conversations(limit=300):
    msgs = _parse_all()
    seen = {}
    order = []
    for m in msgs:
        key = m["chat"] or "未知"
        if key not in seen:
            seen[key] = {"chat": key, "title": "", "count": 0, "last": "",
                         "ts": "", "dt": "", "dir": ""}
            order.append(key)
        c = seen[key]
        c["count"] += 1
        c["last"] = m["text"]
        c["ts"] = m["ts"]
        c["dir"] = m["dir"]
        c["dt"] = m.get("dt", "")
    out = [seen[k] for k in order]
    for c in out:
        c["title"] = chat_title(c["chat"])
    out.sort(key=lambda x: x["dt"] or "", reverse=True)
    return out



OB_HTTP = CFG["onebot"]["http"].rstrip("/")
OB_TOKEN = CFG["onebot"]["token"]

# 会话显示名：默认就是 chat 本身（「群 123456」）；
# 想显示真名就写 data/console_names.json，例如 {"群 123456": "相亲相爱一家人"}
NAMES_FILE = os.path.join(BOT_DIR, "data", "console_names.json")
_NAMES_CACHE = {"ts": 0.0, "map": {}}


def chat_title(chat):
    now = time.time()
    if now - _NAMES_CACHE["ts"] > 60:
        _NAMES_CACHE["ts"] = now
        try:
            with open(NAMES_FILE, "r", encoding="utf-8") as f:
                m = json.load(f)
            _NAMES_CACHE["map"] = m if isinstance(m, dict) else {}
        except (OSError, ValueError):
            pass
    return str(_NAMES_CACHE["map"].get(chat) or chat)


def ob_call(action, payload, timeout=25):
    """调 OneBot HTTP API"""
    req = urllib.request.Request(
        OB_HTTP + "/" + action,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Authorization": "Bearer " + OB_TOKEN,
                 "Content-Type": "application/json"},
        method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))



# SENT_LOG / INGEST_FILE 见文件顶部


def ingest_write(items):
    """接收外部机器人推来的事件，追加成 JSONL；返回写入条数"""
    if not CFG["ingest"].get("enabled", True) or not INGEST_FILE:
        return 0, "ingest 未启用"
    if isinstance(items, dict):
        items = [items]
    if not isinstance(items, list):
        return 0, "需要对象或数组"
    n = 0
    try:
        os.makedirs(os.path.dirname(INGEST_FILE), exist_ok=True)
        with open(INGEST_FILE, "a", encoding="utf-8") as f:
            for it in items:
                if not isinstance(it, dict):
                    continue
                rec = dict(it)
                if not rec.get("ts"):
                    rec["ts"] = time.strftime("%H:%M:%S")
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
                n += 1
    except OSError as e:
        return n, "写入失败：%s" % e
    _MSG_CACHE["ts"] = 0          # 让缓存立刻失效
    return n, ""


def info():
    """给使用者看的「我接的是谁」信息（不含密钥）"""
    try:
        _parse_all()          # 顺便算一下现在能读到多少消息
    except Exception:
        pass
    return {
        "service": SERVICE,
        "container": BOT_CONTAINER or None,
        "onebot": OB_HTTP,
        "port": PORT,
        "config_file": CFG.get("config_file"),
        "log": {
            "source": _log_cfg().get("source"),
            "journal_unit": _log_cfg().get("journal_unit") or SERVICE or None,
            "file": _log_cfg().get("file") or None,
            "since_hours": _log_cfg().get("since_hours"),
            "max_lines": _log_cfg().get("max_lines"),
            "patterns": list((_log_cfg().get("patterns") or {}).keys()),
        },
        "ingest": {"enabled": CFG["ingest"].get("enabled", True), "file": INGEST_FILE},
        "sent_log": SENT_LOG or None,
        "qr": {"mode": CFG["qr"].get("mode")},
        "voice": {"engine": CFG["voice"].get("engine"), "dir": VOICE_DIR_IN_CONTAINER},
        "counts": {
            "messages": len(_MSG_CACHE["msgs"]),
            "conversations": len({m["chat"] for m in _MSG_CACHE["msgs"]}),
        },
    }


def log_sent(chat, text, voice=False):
    """把手动发送的消息记到日志文件，聊天记录才能看到"""
    try:
        os.makedirs(os.path.dirname(SENT_LOG), exist_ok=True)
        m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
        if not m:
            return
        key = ("group:" if m.group(1) == "群" else "private:") + m.group(2)
        body = ("【语音】" + text) if voice else text
        with open(SENT_LOG, "a", encoding="utf-8") as f:
            f.write("%s [发送] %s -> %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), key, body))
    except Exception as e:
        print("log_sent 失败", e)


def send_message(chat, text):
    """chat 形如『群 200000001』或『私聊 100000001』"""
    text = (text or "").strip()
    if not text:
        return {"ok": False, "msg": "内容为空"}
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return {"ok": False, "msg": "会话格式不对：%s" % chat}
    kind, ident = m.group(1), int(m.group(2))
    payload = {"message": [{"type": "text", "data": {"text": text}}]}
    if kind == "群":
        payload["group_id"] = ident
        action = "send_group_msg"
    else:
        payload["user_id"] = ident
        action = "send_private_msg"
    try:
        res = ob_call(action, payload)
    except Exception as e:
        return {"ok": False, "msg": "发送失败：%r" % (e,)}
    if res.get("status") == "ok":
        log_sent(chat, text)
        _MSG_CACHE["ts"] = 0     # 让缓存立即失效，重新拉最新
        return {"ok": True, "msg": "已发送", "message_id": (res.get("data") or {}).get("message_id")}
    return {"ok": False, "msg": res.get("wording") or res.get("message") or "发送失败"}



VOICE_DIR_IN_CONTAINER = CFG["voice"]["dir"]
SF_PY = CFG["voice"]["py"]
SF_BIN = CFG["voice"]["bin"]
SF_VOICE = CFG["voice"]["voice"]
SF_MODEL = CFG["voice"]["model"]
SF_SPEED = CFG["voice"]["speed"]
SF_PITCH = CFG["voice"]["pitch"]
SF_SILK_BIN = CFG["voice"]["silk_bin"]
VOICE_LANG_CONSOLE = os.environ.get("FISH_VOICE_LANG", "zh")


def _sf_key():
    if os.environ.get("FISH_SF_KEY"):
        return os.environ["FISH_SF_KEY"]
    try:
        with open(os.path.join(BOT_DIR, ".env"), "r", encoding="utf-8") as f:
            for ln in f:
                if ln.startswith("FISH_SF_KEY="):
                    return ln.split("=", 1)[1].strip()
    except Exception:
        pass
    return ""



def _ai_env():
    env = {}
    try:
        with open(os.path.join(BOT_DIR, ".env"), "r", encoding="utf-8") as f:
            for ln in f:
                ln = ln.strip()
                if "=" in ln and not ln.startswith("#"):
                    k, v = ln.split("=", 1)
                    env[k.strip()] = v.strip()
    except Exception:
        pass
    return env


def to_japanese(text):
    """把中文翻成日语口语（和插件同一套提示词）。失败返回空。"""
    env = _ai_env()
    base = env.get("FISH_AI_BASE_URL", "").rstrip("/")
    key = env.get("FISH_AI_KEY", "")
    model = env.get("FISH_AI_MODEL", "")
    if not (base and key and model):
        return ""
    prompt = ("把下面这句话翻译成日本人日常口语（年轻女性、亲昵、像在跟熟人聊天）。"
              "要求：1) 只用日语，不要出现任何中文；2) 尽量用平假名写着，别用生僻汉字；"
              "3) 长度和原句相当；4) 只输出译文，不要引号、不要解释。\n原句：" + (text or "")[:120])
    body = json.dumps({"model": model, "messages": [{"role": "user", "content": prompt}],
                       "temperature": 0.3, "max_tokens": 800}).encode("utf-8")
    try:
        req = urllib.request.Request(base + "/v1/chat/completions", data=body,
                                     headers={"Authorization": "Bearer " + key,
                                              "Content-Type": "application/json"})
        d = json.loads(urllib.request.urlopen(req, timeout=40).read().decode("utf-8"))
        m = (d.get("choices") or [{}])[0].get("message", {}) or {}
        out = (m.get("content") or "").strip() or (m.get("reasoning_content") or "").strip()[-120:]
    except Exception as e:
        print("translate err", e)
        return ""
    return pick_japanese(out)


def pick_japanese(out):
    bad = re.compile(r"所以|原句|注意|可以|比较|自然|就是|这句|翻译|假名|理解为|意思|读作|写法")

    def pick(text):
        text = re.sub(r"[「」『』\"\'*#`]", "", text)
        if "：" in text or ":" in text:
            text = re.split(r"[：:]", text)[-1]
        parts = re.split(r"(?<=[。！？～…])", text)
        keep = []
        for p in parts:
            p = p.strip()
            if not p or not re.search(r"[\u3040-\u30ff]", p):
                continue
            if bad.search(p) or re.search(r"[的了吗呢吧嘛]|我们|你们|这个|那个", p):
                continue
            keep.append(p)
        if keep:
            return "".join(keep)
        cands = re.findall(r"[\u3040-\u30ff\u4e00-\u9fff\u3005ー。、！？～…\s]{4,}", text)
        cands = [c.strip() for c in cands if re.search(r"[\u3040-\u30ff]", c) and not bad.search(c)]
        return max(cands, key=len).strip(" ，,。") if cands else ""

    jp = pick(out or "")
    return jp[:60] if len(jp) >= 2 else ""


def make_voice_file(text):
    """合成语音并放进协议端容器，返回容器内路径（失败返回空）"""
    key = _sf_key()
    if not key:
        return "", "没配置 FISH_SF_KEY"
    host = "/tmp/console_voice_%d.mp3" % int(time.time() * 1000)
    env = dict(os.environ)
    env["FISH_SF_KEY"] = key
    try:
        p1 = subprocess.run([SF_PY, SF_BIN, host, text, SF_VOICE, SF_MODEL, SF_SPEED, SF_PITCH],
                            capture_output=True, timeout=90, env=env)
        if p1.returncode != 0 or not os.path.exists(host):
            return "", (p1.stderr or b"")[:200].decode("utf-8", "ignore")
        norm = host + ".n.mp3"
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", host,
                        "-af", "loudnorm=I=-15:TP=-1.2:LRA=11",
                        "-ar", "24000", "-ac", "1", "-c:a", "libmp3lame",
                        "-b:a", "64k", norm], capture_output=True, timeout=30)
        src = norm if os.path.exists(norm) else host
        # 转 SILK
        src_use = src
        try:
            silk_path = src + ".silk"
            ps = subprocess.run([SF_PY, SF_SILK_BIN, src, silk_path, "24000"],
                                capture_output=True, timeout=60)
            if ps.returncode == 0 and os.path.exists(silk_path) and os.path.getsize(silk_path) > 2000:
                src_use = silk_path
        except Exception as e:
            print("silk 转换失败", e)
        ext = "silk" if src_use.endswith(".silk") else "mp3"
        name = "console_%d.%s" % (int(time.time() * 1000), ext)
        target = VOICE_DIR_IN_CONTAINER + "/" + name
        subprocess.run(["docker", "exec", BOT_CONTAINER, "sh", "-lc",
                        "mkdir -p " + VOICE_DIR_IN_CONTAINER], capture_output=True, timeout=30)
        p2 = subprocess.run(["docker", "cp", src_use, BOT_CONTAINER + ":" + target],
                            capture_output=True, timeout=60)
        if p2.returncode != 0:
            return "", (p2.stderr or b"")[:200].decode("utf-8", "ignore")
        subprocess.run(["docker", "exec", BOT_CONTAINER, "sh", "-lc",
                        "chmod 644 " + target], capture_output=True, timeout=20)
        for f in (host, norm):
            try:
                os.remove(f)
            except Exception:
                pass
        return target, ""
    except Exception as e:
        return "", repr(e)


def send_voice(chat, text):
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return {"ok": False, "msg": "会话格式不对：%s" % chat}
    text = (text or "").strip()
    if not text:
        return {"ok": False, "msg": "内容为空"}
    # 默认中文直接念；只有 FISH_VOICE_LANG=ja 时才翻日语
    spoken = text
    if VOICE_LANG_CONSOLE == "ja":
        ja = to_japanese(text)
        spoken = ja if ja else text
    path, err = make_voice_file(spoken)
    if not path:
        return {"ok": False, "msg": "语音生成失败：%s" % err}
    kind, ident = m.group(1), int(m.group(2))
    payload = {"message": [{"type": "record", "data": {"file": path}}]}
    if kind == "群":
        payload["group_id"] = ident
        action = "send_group_msg"
    else:
        payload["user_id"] = ident
        action = "send_private_msg"
    try:
        res = ob_call(action, payload, timeout=40)
    except Exception as e:
        return {"ok": False, "msg": "发送失败：%r" % (e,)}
    if res.get("status") == "ok":
        log_sent(chat, text, voice=True)
        _MSG_CACHE["ts"] = 0
        return {"ok": True, "msg": "语音已发送", "path": path, "ja": spoken}
    return {"ok": False, "msg": res.get("wording") or "发送失败"}


def action(name):
    if name == "start":
        return run(SUDO + "systemctl start " + SERVICE, timeout=60) or "已启动"
    if name == "restart":
        return run(SUDO + "systemctl restart " + SERVICE, timeout=60) or "已重启"
    if name == "stop":
        run("touch " + os.path.join(BOT_DIR, "data", "stop.flag"), timeout=10)
        time.sleep(11)
        return run(SUDO + "systemctl stop " + SERVICE, timeout=60) or "已关闭（发过告别）"
    if name in ("napcat-restart", "bot-restart"):
        return run(SUDO + "docker restart " + BOT_CONTAINER, timeout=120) or "协议端重启中（约 30 秒）"
    if name == "qr-refresh":
        click_qr_refresh()
        return "已点刷新，请重新看二维码"
    if name == "refresh":
        return "ok"
    return "未知操作"



# ---------------------------------------------------------------- 机器人控件（每群规则）
def _rules_path():
    base = globals().get("BOT_DIR") or "/opt/qqbot"
    return os.path.join(str(base), "data", "group_rules.json")


def _rules_load():
    try:
        with open(_rules_path(), "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def _rules_save(d):
    p = _rules_path()
    try:
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=1)
        return True, ""
    except OSError as e:
        return False, str(e)


DEFAULT_BOTCTL = {"only_at": False, "max_len": 26, "speed": 1.0, "chat_rate": -1.0}


def _gid_of_chat(chat):
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return ""
    return m.group(2) if m.group(1) == "群" else ""


def groups_list():
    """优先问协议端要群列表；拿不到就用聊天记录里的群"""
    out = []
    try:
        res = ob_call("get_group_list", {}, timeout=12)
        for g in ((res or {}).get("data") or []):
            out.append({"gid": str(g.get("group_id")), "name": g.get("group_name") or "",
                        "members": g.get("member_count") or 0})
    except Exception:
        pass
    if not out:
        seen = {}
        for c in conversations():
            gid = _gid_of_chat(c.get("chat"))
            if gid and gid not in seen:
                seen[gid] = True
                out.append({"gid": gid, "name": "", "members": 0})
    for g in out:
        r = _rules_load().get(g["gid"]) or {}
        g["only_at"] = bool(r.get("only_at", False))
        g["max_len"] = int(r.get("max_len", 0) or 0)
        try:
            g["chat_rate"] = float(r.get("chat_rate", -1.0))
        except (TypeError, ValueError):
            g["chat_rate"] = -1.0
        try:
            g["speed"] = float(r.get("speed", 1.0) or 1.0)
        except (TypeError, ValueError):
            g["speed"] = 1.0
    out.sort(key=lambda x: x["gid"])
    return out


def botctl_get(chat):
    gid = _gid_of_chat(chat)
    if not gid:
        return {"ok": False, "msg": "只支持群（私聊没有这些规则）"}
    r = _rules_load().get(gid) or {}
    cur = dict(DEFAULT_BOTCTL)
    cur["only_at"] = bool(r.get("only_at", False))
    cur["max_len"] = int(r.get("max_len", 0) or 0)
    try:
        cur["speed"] = float(r.get("speed", 1.0) or 1.0)
    except (TypeError, ValueError):
        cur["speed"] = 1.0
    try:
        cur["chat_rate"] = float(r.get("chat_rate", -1.0))
    except (TypeError, ValueError):
        cur["chat_rate"] = -1.0
    return {"ok": True, "chat": chat, "gid": gid, "rules": cur}


def botctl_set(body):
    chat = (body.get("chat") or "").strip()
    gid = _gid_of_chat(chat)
    if not gid:
        return {"ok": False, "msg": "只支持群（例：群 123456789）"}
    data = _rules_load()
    rec = data.get(gid)
    if not isinstance(rec, dict):
        rec = {}
        data[gid] = rec
    if "only_at" in body:
        rec["only_at"] = bool(body.get("only_at"))
    if "max_len" in body:
        try:
            ml = int(body.get("max_len") or 0)
        except (TypeError, ValueError):
            ml = 0
        rec["max_len"] = max(0, min(200, ml))
    if "speed" in body:
        try:
            sp = float(body.get("speed") or 1.0)
        except (TypeError, ValueError):
            sp = 1.0
        rec["speed"] = max(0.2, min(5.0, round(sp, 2)))
    if "chat_rate" in body:
        try:
            cr = float(body.get("chat_rate"))
        except (TypeError, ValueError):
            cr = -1.0
        if cr < 0:
            rec.pop("chat_rate", None)        # -1 = 跟随全局
        else:
            rec["chat_rate"] = max(0.0, min(1.0, round(cr, 2)))
    ok, err = _rules_save(data)
    if not ok:
        return {"ok": False, "msg": "保存失败：%s" % err}
    _MSG_CACHE["ts"] = 0
    return {"ok": True, "chat": chat, "gid": gid, "rules": botctl_get(chat)["rules"]}


class Handler(BaseHTTPRequestHandler):
    server_version = "FishWeb/1.0"

    def log_message(self, fmt, *args):
        pass

    def _auth_ok(self, q):
        tok = (q.get("token", [""])[0] or "")
        if not tok:
            tok = self.headers.get("X-Token", "")
        return hmac.compare_digest(tok, TOKEN)

    def _cors(self):
        """允许跨域（网页版单独托管时用；config 里 cors=true 才开）"""
        if not CFG.get("cors"):
            return
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, X-Token")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")

    def do_OPTIONS(self):
        self.send_response(204)
        self._cors()
        self.end_headers()

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self._cors()
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        path = u.path

        if path in ("/", "/index.html"):
            p = os.path.join(STATIC, "index.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no index.html", "text/plain; charset=utf-8")

        if path == "/manifest.json":
            return self._send(200, {
                "name": "肥鱼控制台", "short_name": "肥鱼", "start_url": "/",
                "display": "standalone", "background_color": "#141518",
                "theme_color": "#141518", "icons": [{"src": "/icon.png", "sizes": "512x512", "type": "image/png"}],
            })

        if path == "/icon.png":
            p = os.path.join(STATIC, "icon.png")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "image/png")
            return self._send(404, "no icon", "text/plain")

        if not self._auth_ok(q) and path != "/index.html":
            return self._send(401, {"error": "token"})

        if path == "/api/status":
            return self._send(200, status())
        if path == "/api/diag":
            return self._send(200, diag())
        if path == "/api/selfcheck":
            return self._send(200, selfcheck())
        if path == "/api/health":
            return self._send(200, health())
        if path == "/api/info":
            return self._send(200, info())
        if path == "/api/groups":
            return self._send(200, {"items": groups_list()})
        if path == "/api/botctl":
            chat = (q.get("chat", [""])[0] or "").strip()
            return self._send(200, botctl_get(chat))
        if path == "/api/messages":
            limit = int(q.get("limit", ["120"])[0])
            chat = (q.get("chat", [""])[0] or "").strip() or None
            body = {"messages": chat_messages(limit, chat)}
            if chat:
                body["chat"] = chat
                body["title"] = chat_title(chat)
            return self._send(200, body)
        if path == "/api/conversations":
            return self._send(200, {"items": conversations(int(q.get("limit", ["200"])[0]))})
        if path == "/api/logs":
            lines = last_lines("'[接收]' -e '[发送]'", since="60 min ago", limit=120)
            return self._send(200, {"lines": lines})
        if path == "/api/runlog":
            out = run(SUDO + "journalctl -u " + SERVICE + " --since '40 min ago' --no-pager | tail -120", timeout=30)
            return self._send(200, {"text": out})
        if path == "/api/desktop":
            if str(CFG["qr"].get("mode", "screen")).lower() == "off":
                return self._send(501, {"error": "qr.mode=off"})
            data = grab_desktop()
            if not data:
                return self._send(404, {"error": "没有截图"})
            return self._send(200, data, "image/png")
        if path == "/api/qr":
            if str(CFG["qr"].get("mode", "screen")).lower() == "off":
                return self._send(501, {"error": "qr.mode=off（协议端不支持截屏取码，请用该协议端自己的接口）"})
            data = grab_qr(fresh=(q.get("fresh", ["0"])[0] == "1"))
            if not data:
                return self._send(404, {"error": "没有二维码"})
            return self._send(200, data, "image/png")
        return self._send(404, {"error": "not found"})

    def do_POST(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        if not self._auth_ok(q):
            return self._send(401, {"error": "token"})
        n = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(n) if n else b"{}"
        try:
            body = json.loads(raw or b"{}")
        except Exception:
            body = {}
        path = u.path
        if path == "/api/botctl":
            return self._send(200, botctl_set(body or {}))
        if path == "/api/ingest":
            items = body.get("events") if isinstance(body.get("events"), list) else body
            n, err = ingest_write(items)
            if err:
                return self._send(400, {"ok": False, "msg": err, "written": n})
            return self._send(200, {"ok": True, "written": n})
        if path == "/api/send":
            chat = (body.get("chat") or "").strip()
            text = body.get("text") or ""
            if body.get("voice"):
                return self._send(200, send_voice(chat, text))
            return self._send(200, send_message(chat, text))
        if path == "/api/action":
            name = (body.get("name") or "").strip()
            return self._send(200, {"ok": True, "msg": action(name)})
        return self._send(404, {"error": "not found"})


def main():
    print("fatbot-console on :%d" % PORT, flush=True)
    print("  配置      : %s" % CFG.get("config_file"), flush=True)
    print("  服务/容器 : %s / %s" % (SERVICE, BOT_CONTAINER or "（无）"), flush=True)
    print("  OneBot    : %s" % OB_HTTP, flush=True)
    print("  日志来源  : %s" % _log_cfg().get("source"), flush=True)
    print("  ingest    : %s" % (INGEST_FILE or "关闭"), flush=True)
    if TOKEN in ("change-me-please", "change-me", ""):
        print("  ⚠ token 还是默认值，请改 config.json 的 token（或用 FATBOT_TOKEN）", flush=True)
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    srv.serve_forever()


if __name__ == "__main__":
    main()
