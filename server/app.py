#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""肥鱼控制台 · 手机版 Web 后端（只用标准库）

监听 0.0.0.0:8090，带 token 鉴权，给手机 APK / 浏览器用。
所有操作都在本机执行（systemctl / docker / journalctl），不需要 SSH。
"""
import base64
import datetime
import urllib.request
import hashlib
import hmac
import io
import json
import os
import re
import shlex
import shutil
import socket
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, quote, unquote

TOKEN = os.environ.get("FISHWEB_TOKEN", "fishbot-2026")
PORT = int(os.environ.get("FISHWEB_PORT", "8090"))
BOT_DIR = "/opt/qqbot"
QR_LOCAL = "/tmp/fishweb_qr.png"
DESKTOP_LOCAL = "/tmp/fishweb_desktop.png"
BOT_CONTAINER = "snowluma"          # 协议端容器名（原 napcat）

STATIC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")
# 拼音库（pypinyin）装在 webui 自己的目录里：`pip install --target webui/_pylibs pypinyin`，
# 不碰系统 python 的包。装不上也不影响别的功能 —— 只是标签搜索少了拼音这条路。
PYLIBS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_pylibs")
if os.path.isdir(PYLIBS) and PYLIBS not in sys.path:
    sys.path.insert(0, PYLIBS)
HEALTH_LOG = "/opt/qqbot/logs/health.log"
# 短链表：插件（肥鱼人设.py）写，webui 读。/s/<code> → 302 跳到真链。
SHORT_LINKS = os.path.join(BOT_DIR, "data", "short_links.json")

# ---------------------------------------------------------------- Web QQ（2026-10-28）
# 用户：「控制台的聊天页面一点都不消息同步，你干脆去网上找个开源的QQ内置进去好了」。
# 控制台那个聊天页不是真客户端：它靠 SSH 每 4 秒去服务器翻 journald 日志再重画，
# 所以必然慢、会丢、发不了图/文件。这里换成真东西 —— Stapxs QQ Lite 2.0（MIT）：
#   ① 静态：把它的构建产物放 static/qq/ 下，按它自己的 publicPath `/Stapxs-QQ-Lite-2.0/`
#      提供服务（这样不用改它的构建，前端资源里的绝对路径刚好对得上）。
#   ② 数据：它只连 WebSocket（connect.ts 里只有 `ws://地址?access_token=`）。
#      浏览器打不到服务器的 127.0.0.1:3001，所以在这里做一个**带 token 鉴权的透明 WS 反代**
#      `/qqws?access_token=…`：先验我们自己的 token，再用真正的 OneBot token 去连本机 3001，
#      握手 + 之后的帧原样双向搬运 —— **OneBot 的 token 永远不出服务器**。
#   ③ 页面本身不含任何秘密（就是个开源前端），所以静态资源不要 token 也能取；
#      真正的数据全在那条 WS 上，过不了鉴权就什么都拿不到。
QQ_DIR = os.path.join(STATIC, "qq")
QQ_BASE = "/Stapxs-QQ-Lite-2.0"
# Stapxs 的 bcui/js/main.js 和部分运行时逻辑是拿**根路径**去取样式的
# （`/css/append-light.css`、`/bcui/css/style.css`…），不认 publicPath。
# 这些前缀在控制台里没被占用，直接也映射到 static/qq/ 下，否则样式全 401、白屏。
QQ_ROOT_PREFIXES = ("/css/", "/bcui/", "/img/", "/assets/")
QQ_ROOT_FILES = ("/favicon.ico", "/robots.txt", "/manifest.webmanifest")

# ---------------------------------------------------------------- 真·QQ（noVNC，2026-10-29）
# 用户最新要求：「保留完整的 QQ 功能，消息同步要和真实 QQ 一样」。
# OneBot 客户端（Stapxs）做不到「完整 QQ」——红包/语音通话/空间/表情商店在协议里就没有。
# 能 100% 满足的只有服务器上那个**真的 QQ 客户端**（容器里 /opt/QQ/qq + Xvfb :1 + x11vnc + noVNC）。
# 但 6081 被云安全组挡着，所以把 noVNC 的静态页和它的 websockify 一起反代到 8090 下：
#   /vnc/<文件>              → 127.0.0.1:6081/<文件>         （静态页，公开，不含秘密）
#   /vnc/websockify/<TOKEN>  → ws 127.0.0.1:6081/websockify  （**token 放路径段**：noVNC 只会拼路径、不会带 query）
VNC_HOST, VNC_PORT = "127.0.0.1", 6081
VNC_WS_UP = "/websockify"
VNC_PREFIX = "/vnc/"
QQ_MIME = {
    ".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8", ".json": "application/json; charset=utf-8",
    ".webmanifest": "application/manifest+json; charset=utf-8",
    ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
    ".gif": "image/gif", ".svg": "image/svg+xml", ".ico": "image/x-icon",
    ".woff": "font/woff", ".woff2": "font/woff2", ".ttf": "font/ttf", ".eot": "application/vnd.ms-fontobject",
    ".map": "application/json; charset=utf-8", ".txt": "text/plain; charset=utf-8",
}


def onebot_ws_target():
    """从 /opt/qqbot/.env 的 ONEBOT_WS_URLS 里取出本机 OneBot WS 的 host/port/path/token。

    .env 里是 JSON 数组：ONEBOT_WS_URLS=["ws://127.0.0.1:3001/?access_token=xxx"]
    """
    try:
        with open(os.path.join(BOT_DIR, ".env"), "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                s = line.strip()
                if not s.startswith("ONEBOT_WS_URLS"):
                    continue
                raw = s.split("=", 1)[1].strip()
                try:
                    urls = json.loads(raw)
                except ValueError:
                    urls = [raw.strip("[]\"' ")]
                if isinstance(urls, str):
                    urls = [urls]
                for u in urls:
                    m = re.match(r"wss?://([^/:]+):(\d+)(/[^?]*)?\?access_token=(.+)", str(u))
                    if m:
                        return m.group(1), int(m.group(2)), m.group(3) or "/", m.group(4)
    except (OSError, ValueError):
        pass
    return None



def short_get(code):
    """查短码对应的真链；过期或不存在返回空串。"""
    code = (code or "").strip()
    if not code or not re.fullmatch(r"[0-9a-zA-Z]{4,16}", code):
        return ""
    try:
        with open(SHORT_LINKS, "r", encoding="utf-8") as f:
            d = json.load(f) or {}
    except (OSError, ValueError):
        return ""
    if not isinstance(d, dict):
        return ""
    rec = d.get(code)
    if not isinstance(rec, dict):
        return ""
    try:
        if float(rec.get("t") or 0) <= time.time():
            return ""
    except (TypeError, ValueError):
        return ""
    u = str(rec.get("u") or "")
    return u if u.startswith("http") else ""


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
    return run("systemctl is-active fishbot").strip()


def napcat_running():
    out = run(SUDO + "docker inspect -f '{{.State.Running}}' " + BOT_CONTAINER + " 2>/dev/null", timeout=10)
    return "true" in out.lower()


def last_lines(pat, since="30 min ago", limit=80):
    q = SUDO + "journalctl -u fishbot --since '%s' --no-pager" % since
    out = run(q + " | grep -F -e %s | tail -%d" % (pat, limit), timeout=30)
    return [x for x in out.replace("\r", "").split("\n") if x.strip()]


# ---- /画图（本地 ComfyUI）状态：给控制台「画图」卡片用（2026-11-04）----
# 插件把今天画了几张写在 data/draw_state.json、每个会话开没开写在 data/draw_switch.json；
# 隧道是「本机 plink -R 18188:127.0.0.1:8190」，所以从服务器这边 curl 127.0.0.1:18188 就能
# 同时知道「隧道通不通」和「本机 ComfyUI 通不通」（网关 /health 里带 comfy.reachable）。
DRAW_STATE_FILE = os.path.join(BOT_DIR, "data", "draw_state.json")
DRAW_SWITCH_FILE = os.path.join(BOT_DIR, "data", "draw_switch.json")
DRAW_URL = os.environ.get("FISH_DRAW_URL") or "http://127.0.0.1:18188"
DRAW_DAILY_LIMIT = 40
DRAW_PRICE = 250
DRAW_PERSONAS = (os.environ.get('FISH_DRAW_PERSONAS') or 'feiyu,dsh')


def draw_status():
    st = _load_json_file(DRAW_STATE_FILE)
    today = time.strftime("%Y-%m-%d")
    used = 0
    if str(st.get("date") or "") == today:
        try:
            used = max(0, int(st.get("used") or 0))
        except (TypeError, ValueError):
            used = 0
    sw = _load_json_file(DRAW_SWITCH_FILE)
    on = []
    for k, v in sw.items():
        if isinstance(v, dict) and v.get("on"):
            on.append({"chat": str(k), "at": str(v.get("at") or ""), "by": str(v.get("by") or "")})
    on.sort(key=lambda x: str(x.get("at") or ""), reverse=True)
    tunnel = False
    comfy = False
    health = {}
    err = ""
    try:
        with urllib.request.urlopen(DRAW_URL + "/health", timeout=4) as r:
            health = json.loads(r.read().decode("utf-8"))
        tunnel = str(health.get("status") or "") == "ok"
        comfy = bool(((health.get("comfy") or {}).get("reachable")))
    except Exception as e:
        err = type(e).__name__
    return {
        "ok": True,
        "url": DRAW_URL,
        "tunnel": tunnel,
        "comfy": comfy,
        "error": err,
        "used": used,
        "left": max(0, DRAW_DAILY_LIMIT - used),
        "daily": DRAW_DAILY_LIMIT,
        "price": DRAW_PRICE,
        "personas": [p.strip() for p in DRAW_PERSONAS.split(",") if p.strip()],
        "date": str(st.get("date") or ""),
        "last_at": str(st.get("at") or ""),
        "chats_on": on,
        "chats_on_count": len(on),
        "workflows": (health.get("workflows") or {}).get("count"),
        "gateway_version": str(health.get("version") or ""),
        "uptime": health.get("uptimeSeconds"),
    }


def status():
    svc = svc_state()
    nap = napcat_running()
    mem = ""
    try:
        parts = run("free -m").split("\n")[1].split()
        mem = "%s/%s MB" % (parts[2], parts[1])
    except Exception:
        mem = ""
    up = run("systemctl show fishbot -p ActiveEnterTimestamp --value").strip()
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


def diag():
    st = status()
    qr_age = -1
    try:
        out = run(SUDO + "docker exec napcat sh -c 'stat -c %Y %s'" % QR_IN_CONTAINER, timeout=20)
        m = re.search(r"(\d{9,})", out)
        if m:
            qr_age = int(time.time()) - int(m.group(1))
    except Exception:
        pass
    kick = run(SUDO + "docker logs --since 10m napcat 2>&1 | grep -c KickedOffLine", timeout=25).strip()
    try:
        kick = int(kick)
    except Exception:
        kick = 0
    if st["svc"] != "active":
        verdict, hint = "不在线：机器人服务停了", "点下面的「启动服务」"
    elif not st["napcat"]:
        verdict, hint = "不在线：SnowLuma 容器没起来", "点「重启 NapCat」"
    elif st["tx"] > 0 or st["rx"] > 0:
        verdict, hint = "在线，正在正常收发", ""
    elif kick > 0:
        verdict, hint = "不在线：账号被踢下线", "去「二维码」页扫码重新登录"
    elif 0 <= qr_age < 900:
        verdict, hint = "不在线：卡在扫码登录", "去「二维码」页扫码"
    else:
        verdict, hint = "不在线：OneBot 没连上", "检查 NapCat 是否已登录"
    return {"verdict": verdict, "hint": hint, "qr_age": qr_age, "kick": kick, "status": st}


QR_CROP = "crop=220:220:852:434,scale=660:660:flags=neighbor"
QR_CLICK = "961 581"      # 「刷新」按钮位置（过期时点它换新码）


def _qq_win():
    """找 QQ **登录窗口**的位置和大小（`(x, y, w, h)`，找不到返回 None）。

    2026-11-02：窗口位置一变，写死的 `QR_CROP` 就裁到别的地方去了（线上真踩到：
    取到的"二维码"其实是密码框）。所以改成**按窗口几何算**，窗口挪到哪都能裁准。
    """
    try:
        out = run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
                  "\"xdotool search --name QQ\"" % BOT_CONTAINER, timeout=20) or ""
    except Exception:
        return None
    for wid in [w for w in out.split() if w.strip().isdigit()][:6]:
        try:
            g = run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
                    "\"xdotool getwindowgeometry --shell %s\"" % (BOT_CONTAINER, wid),
                    timeout=20) or ""
        except Exception:
            continue
        m = dict(re.findall(r"([A-Z]+)=(-?\d+)", g))
        try:
            x, y = int(m.get("X", 0)), int(m.get("Y", 0))
            w, h = int(m.get("WIDTH", 0)), int(m.get("HEIGHT", 0))
        except Exception:
            continue
        if w >= 500 and h >= 300:          # 登录窗（小窗口是托盘/气泡，跳过）
            return x, y, w, h
    return None


def _qr_crop_and_click():
    """二维码在窗口里的相对位置（实测：窗口 620,310,680x440 时码在 1076,412,161²）。"""
    win = _qq_win()
    if not win:
        return QR_CROP, QR_CLICK
    x, y, w, h = win
    qx = int(x + 0.671 * w)
    qy = int(y + 0.232 * h)
    qs = int(0.237 * w)
    crop = "crop=%d:%d:%d:%d,scale=660:660:flags=neighbor" % (qs, qs, qx, qy)
    click = "%d %d" % (int(x + 0.785 * w), int(y + 0.439 * h))
    return crop, click


def click_qr_refresh():
    _crop, click = _qr_crop_and_click()
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"xdotool mousemove %s click 1\"" % (BOT_CONTAINER, click), timeout=25)
    time.sleep(2.5)


def grab_qr(fresh=False):
    """从 SnowLuma 容器里的 Linux QQ 窗口截屏，裁出二维码那块。"""
    if fresh:
        click_qr_refresh()
    # 把鼠标挪开，别挡在二维码上
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"xdotool mousemove 1500 950\"" % BOT_CONTAINER, timeout=20)
    crop, _click = _qr_crop_and_click()
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
        "-vf '%s' -frames:v 1 -y /tmp/fishqr.png\"" % (BOT_CONTAINER, crop), timeout=40)
    run(SUDO + "docker cp %s:/tmp/fishqr.png %s" % (BOT_CONTAINER, QR_LOCAL), timeout=20)
    if os.path.exists(QR_LOCAL) and os.path.getsize(QR_LOCAL) > 2000:
        with open(QR_LOCAL, "rb") as f:
            return f.read()
    return None


def grab_desktop():
    """整个远程桌面（需要手动处理验证码时用）。"""
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
        "-frames:v 1 -y /tmp/fishdesk.png\"" % BOT_CONTAINER, timeout=40)
    run(SUDO + "docker cp %s:/tmp/fishdesk.png %s" % (BOT_CONTAINER, DESKTOP_LOCAL), timeout=20)
    if os.path.exists(DESKTOP_LOCAL):
        with open(DESKTOP_LOCAL, "rb") as f:
            return f.read()
    return None


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
    out = run(SUDO + "journalctl -u fishbot --since '%s' --no-pager 2>/dev/null | grep -cF %s" % (since, pattern), timeout=25)
    try:
        return int(out.strip().splitlines()[-1])
    except Exception:
        return 0


def conn_state():
    """最后一个连接相关事件是「已连接」还是「重连中」。"""
    out = run(SUDO + "journalctl -u fishbot --since '25 min ago' --no-pager 2>/dev/null "
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
    """SnowLuma 日志里出现 hook 工作模式 / 收到消息，就算登录成功。"""
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

    add(st["svc"] == "active", "机器人服务 fishbot",
        "运行中" if st["svc"] == "active" else st["svc"])
    add(st["napcat"], "SnowLuma 容器", "在跑" if st["napcat"] else "没起来")
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
    if st["svc"] != "active":
        verdict, hint = "不在线：机器人服务停了", "点下面的「启动服务」"
    elif not st["napcat"]:
        verdict, hint = "不在线：SnowLuma 容器没起来", "点「重启 NapCat」"
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
MSG_TTL = 15          # 缓存 15 秒，点进会话基本是瞬间出
MSG_SINCE = "6 hours ago"
MSG_TAIL = 800

# journalctl 默认输出 `Oct 19 21:13:29 host unit[pid]: ...`
# manual_sent.log 是 `2026-10-19 21:13:29 [发送] ...`
_MON = {"Jan": 1, "Feb": 2, "Mar": 3, "Apr": 4, "May": 5, "Jun": 6,
        "Jul": 7, "Aug": 8, "Sep": 9, "Oct": 10, "Nov": 11, "Dec": 12}
_TS_FULL = re.compile(r"^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})")
_TS_JOURNAL = re.compile(r"^([A-Z][a-z]{2})\s+(\d{1,2})\s+(\d{2}):(\d{2}):(\d{2})")
_TS_HMS = re.compile(r"(\d{2}):(\d{2}):(\d{2})")


def line_dt(line):
    """从一行日志里取时间，取不到返回 None。"""
    now = datetime.datetime.now()
    m = _TS_FULL.match(line)
    if m:
        try:
            return datetime.datetime(*[int(x) for x in m.groups()])
        except ValueError:
            return None
    m = _TS_JOURNAL.match(line)
    if m:
        mon = _MON.get(m.group(1))
        if mon:
            try:
                dt = datetime.datetime(now.year, mon, int(m.group(2)), int(m.group(3)),
                                       int(m.group(4)), int(m.group(5)))
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


def _merge_by_time(journal, extra):
    """把「机器人日志」和「手动发送日志」按真实时间归并，返回 [(dt, line), ...]。

    以前是 journal 全量 + 手动发送直接拼在末尾，所以自己发的消息永远排在最下面。
    """
    tagged = [(line_dt(ln), 0, i, ln) for i, ln in enumerate(journal)]
    tagged += [(line_dt(ln), 1, i, ln) for i, ln in enumerate(extra)]
    known = [t for t in tagged if t[0] is not None]
    known.sort(key=lambda t: (t[0], t[1], t[2]))
    out = [(t[0], t[3]) for t in known]
    out += [(None, t[3]) for t in tagged if t[0] is None]   # 解析不出时间的贴最后
    return out


def _parse_entries(entries, maxlen=200):
    """把 [(dt, line), ...] 解析成消息列表。聊天页和每日归档共用一套规则。"""
    msgs = []
    _last_media = ("", None)      # (图 key, 时间)：用来掐掉同一条图的重复日志行
    for dt, ln in entries:
        ts = ""
        m = re.search(r"(\d{2}:\d{2}:\d{2})", ln)
        if m:
            ts = m.group(1)
        if "[接收]" in ln:
            direction = "in"
            body = ln.split("[接收]", 1)[1].strip()
        elif "[发送]" in ln:
            direction = "out"
            body = ln.split("[发送]", 1)[1].strip()
        else:
            # journalctl 报错、或日志里混进来的杂行：跳过（以前这里直接 split 会 IndexError）
            continue

        kind = "text"
        who = ""
        chat_name = ""
        text = body

        if direction == "in":
            m2 = re.match(r"^(群|私聊)\s*(\d+)?\s*(.*?)\s*->\s*(.*)$", body)
            if m2:
                chat_name = (m2.group(1) + " " + (m2.group(2) or "")).strip()
                who = (m2.group(3) or "").strip()
                text = (m2.group(4) or "").strip()
        else:
            m3 = re.match(r"^(group|private):(\d+)\s*->\s*(.*)$", body)
            if m3:
                chat_name = ("群 " + m3.group(2)) if m3.group(1) == "group" else ("私聊 " + m3.group(2))
                text = (m3.group(3) or "").strip()
                who = "我"
            else:
                m4 = re.match(r"^(群|私聊)\s*(\d+)\s*->\s*(.*)$", body)
                if m4:
                    chat_name = (m4.group(1) + " " + m4.group(2)).strip()
                    text = (m4.group(3) or "").strip()
                    who = "我"

        if not chat_name:
            # 「[接收] 群 707291183」这种只是调试行，没有内容，不当消息
            continue

        # ---- 图片 / 表情包（2026-10-28）----
        # 插件现在把图床地址写在日志末尾：`... 【图片】https://...` / `... 【表情】https://...`
        # 自己发的表情包是 `【表情包】st091.jpg`，那张图就在服务器的 stickers 目录里。
        img = ""
        stk = ""
        loc = ""
        mm = re.search(r"【(图片|表情)】((?:https?|file|stk)://\S+)", text)
        if mm:
            src = mm.group(2)
            kind = "sticker" if mm.group(1) == "表情" else "image"
            if src.startswith("stk://"):
                # 服务器 stickers 目录里的原图（她自己发的表情包/梗包）
                stk = os.path.basename(src[len("stk://"):])
            elif src.startswith("file://"):
                # 她自己发的图：插件把字节存在 data/chatimg/，这里只留文件名
                loc = os.path.basename(src)
            else:
                img = src
            text = text[:mm.start()].strip()
            if not text:
                text = "[表情]" if kind == "sticker" else "[图片]"
        else:
            ms = re.match(r"^【表情包】([A-Za-z0-9_.\-]{1,80})$", text)
            if ms:
                stk = ms.group(1)
                kind = "sticker"
                text = "[表情]"
            elif "【非文本消息】" in text or text.startswith("【image】") or "【表情包】" in text:
                kind = "media"
                text = "[表情]" if "表情" in text else "[图片]"

        # 引用：自己发的记成 `[引用]「谁：原话」正文`；插件发的是 `[引用]正文`（不知道引的哪条）
        quote = None
        if text.startswith("[引用]"):
            rest = text[len("[引用]"):].lstrip()
            mq = re.match(r"^「([^」]{0,120})」\s*(.*)$", rest, re.S)
            if mq:
                head, rest = mq.group(1), mq.group(2)
                if "：" in head:
                    qwho, qtext = head.split("：", 1)
                elif ":" in head:
                    qwho, qtext = head.split(":", 1)
                else:
                    qwho, qtext = "", head
                quote = {"who": qwho.strip(), "text": qtext.strip()}
            else:
                quote = {"who": "", "text": ""}
            text = rest.strip()

        msg = {"dir": direction, "chat": chat_name, "who": who,
               "text": text[:maxlen], "kind": kind, "ts": ts,
               "dt": dt.strftime("%Y-%m-%d %H:%M:%S") if dt else ""}
        # 同一张图 5 秒内重复（插件里 `_send_and_track` 记一行、调用方又记一行）→ 只留一条
        _mk = img or loc or stk
        if _mk and _last_media[0] == _mk and dt is not None and _last_media[1] is not None:
            try:
                if abs((dt - _last_media[1]).total_seconds()) <= 5:
                    continue
            except Exception:
                pass
        _last_media = (_mk, dt if dt is not None else _last_media[1])
        if img:
            msg["img"] = img
        if stk:
            msg["stk"] = stk
        if loc:
            msg["loc"] = loc
        if quote is not None:
            msg["quote"] = quote
        msgs.append(msg)
    return msgs


def _parse_all(force=False):
    """解析一次日志，缓存起来（点会话列表/进单聊都复用）"""
    now = time.time()
    if not force and _MSG_CACHE["msgs"] and now - _MSG_CACHE["ts"] < MSG_TTL:
        return _MSG_CACHE["msgs"]
    journal = last_lines("'[接收]' -e '[发送]'", since=MSG_SINCE, limit=MSG_TAIL)
    # 手动发送的日志（控制台/手机发的）也要合并进来
    extra = []
    try:
        if os.path.exists(SENT_LOG):
            with open(SENT_LOG, "r", encoding="utf-8") as f:
                extra = [ln.rstrip("\n") for ln in f.readlines()[-400:]]
    except Exception:
        pass
    msgs = _parse_entries(_merge_by_time(journal, extra), 200)
    _MSG_CACHE["msgs"] = msgs
    _MSG_CACHE["ts"] = now
    return msgs


def chat_messages(limit=120, only_chat=None):
    msgs = _parse_all()
    if only_chat:
        msgs = [m for m in msgs if m["chat"] == only_chat]
    return msgs[-limit:]


ROSTER_FILE = os.path.join(BOT_DIR, "data", "roster.json")
AFF_FILE = os.path.join(BOT_DIR, "data", "affinity.json")
# ⚠️ 和插件里的 `PERSONA_DATA_SHARE` **必须一致**：同一个角色的不同年龄段共用一本账
# （2026-11-08 用户：「成年鱼和九岁鱼好感度和鲸圆券应该是共享数据啊」）。
# 左边 = 人格 id，右边 = 共享到哪个人格的数据。
PERSONA_DATA_SHARE = {"feiyu_adult": "feiyu"}
# 人物词云：插件每隔 10 分钟写一份（uid -> {tags:[...]}），这里只读
TRAITS_FILE = os.path.join(BOT_DIR, "data", "traits.json")
TRAITS_MAX = 9


def _persona_data_files(persona):
    """花名册 / 好感度 / 词云三个文件（按人格分家）。

    默认人格还是 `data/*.json`；别的角色在 `data/personas/<id>/*.json`
    （和插件里的 `_pdata()` 一一对应）。传空 = 默认人格。

    ⚠️ 2026-11-08：**共享人格**（`PERSONA_DATA_SHARE`，如 `feiyu_adult` → `feiyu`）
    这三个文件读的是默认人格那份 —— 插件那边也是同一份，不然控制台会显示另一本账
    （用户：「成年鱼和九岁鱼好感度和鲸圆券应该是共享数据啊」）。
    """
    pid = str(persona or "").strip()
    try:
        default = PERSONA_DEFAULT
        ok = _persona_id_ok(pid)
    except NameError:                      # 定义在文件后面，理论上不会走到
        default, ok = "feiyu", bool(re.fullmatch(r"[A-Za-z0-9_\-]{1,32}", pid))
    pid = PERSONA_DATA_SHARE.get(pid, pid)
    if pid and pid != default and ok:
        d = os.path.join(BOT_DIR, "data", "personas", pid)
        return (os.path.join(d, "roster.json"), os.path.join(d, "affinity.json"),
                os.path.join(d, "traits.json"))
    return ROSTER_FILE, AFF_FILE, TRAITS_FILE


def _persona_title(persona):
    pid = str(persona or "").strip()
    try:
        for it in persona_registry_load().get("items") or []:
            if str(it.get("id")) == pid:
                return str(it.get("name") or pid)
    except Exception:
        pass
    return pid or "肥鱼"


def _load_json_file(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


# --------- 手动改好感度（控制台/网页用，走 token 鉴权）---------
AFF_MIN_V, AFF_MAX_V = -255, 256
AFF_CAP_INF_V = 10 ** 9          # 「上不封顶」（注册表 aff_cap = -1 或 "inf"）时手动改也不夹


def _aff_unlimited_of(it):
    """这个注册表条目是不是「不封顶」（缺失 ≠ 不封顶，别让 str(None) 骗了）。"""
    raw = (it or {}).get("aff_cap")
    if raw is None:
        return False
    if isinstance(raw, str):
        return raw.strip().lower() in ("inf", "infinity", "none", "off", "∞", "unlimited")
    try:
        return int(raw) < 0
    except (TypeError, ValueError):
        return False


def _aff_int(raw, dflt=0):
    """宽松转 int：`"inf"` / None / 空串 / 垃圾值一律给 dflt。

    （注册表里 `aff_cap` 可能是字符串 `"inf"` = 不封顶，裸 `int()` 会抛 ValueError
    把整个 API 打成 500 —— 2026-11-03 的 `/api/personas` 就是这么挂的。）
    """
    try:
        if isinstance(raw, str):
            s = raw.strip().lower()
            if s in ("", "inf", "infinity", "none", "off", "∞", "unlimited"):
                return dflt
        return int(raw)
    except (TypeError, ValueError):
        return dflt


def _aff_cap_v(persona=""):
    """手动改好感度/灯牌时的上限：跟插件一致 —— 注册表 aff_cap，-1/"inf" 表示不封顶。"""
    try:
        reg = persona_registry_load()
        pid = str(persona or reg.get("active") or "")
        for it in reg.get("items") or []:
            if str(it.get("id")) != pid:
                continue
            raw = it.get("aff_cap")
            if isinstance(raw, str) and raw.strip().lower() in (
                    "inf", "infinity", "none", "off", "∞", "unlimited"):
                return AFF_CAP_INF_V
            v = int(raw or 0)
            return AFF_CAP_INF_V if v < 0 else (v if v > 0 else AFF_MAX_V)
    except Exception:
        pass
    return AFF_MAX_V


def aff_set(uid, value=None, delta=None, note="", persona=""):
    """把某人的好感度设为 value 或加上 delta。

    和插件的 aff_add 区别：这里是**人工改**，不参与冷却、不写 log 自动记录，
    但会在 log 里留一条「手动调整」的痕迹（k='手动'），网页下拉里看得到是谁改的。
    `persona` 决定改哪份数据（空 = 肥鱼；taffy = 塔菲的灯牌）。
    """
    uid = re.sub(r"\D", "", str(uid or ""))[:12]
    if len(uid) < 5:
        return {"ok": False, "msg": "QQ 号看着不太对"}
    if value is None and delta is None:
        return {"ok": False, "msg": "要么给 value（设成多少），要么给 delta（加减多少）"}

    aff_file = _persona_data_files(persona)[1]
    data = _load_json_file(aff_file)
    rec = data.get(uid)
    if not isinstance(rec, dict):
        rec = {"v": 0, "name": uid}
        data[uid] = rec
    try:
        old = int(rec.get("v", 0) or 0)
    except (TypeError, ValueError):
        old = 0

    if value is not None:
        try:
            new = int(value)
        except (TypeError, ValueError):
            return {"ok": False, "msg": "value 得是整数"}
    else:
        try:
            new = old + int(delta)
        except (TypeError, ValueError):
            return {"ok": False, "msg": "delta 得是整数"}

    new = max(AFF_MIN_V, min(_aff_cap_v(persona), new))
    rec["v"] = new
    rec["at"] = time.strftime("%Y-%m-%d %H:%M")
    if new != old:
        log = rec.get("log")
        if not isinstance(log, list):
            log = []
        log.append({
            "t": time.strftime("%m-%d %H:%M"),
            "d": new - old,
            "v": new,
            "k": "手动",
            "r": (note or "控制台手动调整")[:80],
        })
        rec["log"] = log[-20:]
    data[uid] = rec

    # 原子写，别让插件读到半个文件
    tmp = aff_file + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        os.replace(tmp, aff_file)
    except OSError as e:
        return {"ok": False, "msg": "写文件失败：%s" % e}
    return {"ok": True, "uid": uid, "old": old, "new": new, "delta": new - old}


def _style_of(rec):
    """从最近几条发言归纳「说话方式」，硬上限 10 字（C2）。"""
    recent = [str(x) for x in (rec.get("recent") or []) if x]
    if not recent:
        return ""
    avg = sum(len(x) for x in recent) / max(1, len(recent))
    q = sum(x.count("？") + x.count("?") for x in recent)
    ex = sum(x.count("！") + x.count("!") for x in recent)
    tags = []
    if avg <= 6:
        tags.append("惜字如金")
    elif avg >= 16:
        tags.append("爱长篇")
    if q >= max(2, len(recent)):
        tags.append("爱提问")
    if ex >= 2:
        tags.append("情绪派")
    if any(("哈哈" in x) or ("233" in x) for x in recent):
        tags.append("爱笑")
    if any(len(x) <= 3 for x in recent) and avg > 8:
        tags.append("忽长忽短")
    if not tags:
        tags.append("正常聊")
    return "·".join(tags)[:10]


def _tags_of(traits, uid):
    """从 traits.json 里取这个人的词云（最多 9 个），没有就返回空列表。"""
    rec = (traits or {}).get(str(uid))
    if not isinstance(rec, dict):
        return []
    tags = rec.get("tags")
    if not isinstance(tags, list):
        return []
    out = []
    for t in tags:
        t = str(t or "").strip()[:8]
        if t and t not in out:
            out.append(t)
        if len(out) >= TRAITS_MAX:
            break
    return out


def whale_file_of(persona=""):
    """鲸圆券文件（和插件 `_pdata("whale.json")` 同一份）。

    默认人格 = `data/whale.json`；别的角色 = `data/personas/<id>/whale.json`。
    格式：`{uid: {"w": 余额, "at": 时间, "log": [{t,d,w,r}]}}`
    """
    pid = str(persona or "").strip()
    try:
        default, ok = PERSONA_DEFAULT, _persona_id_ok(pid)
    except NameError:
        default, ok = "feiyu", bool(re.fullmatch(r"[A-Za-z0-9_\-]{1,32}", pid))
    pid = PERSONA_DATA_SHARE.get(pid, pid)          # 共享人格（成年鱼）读默认人格那份
    if pid and pid != default and ok:
        return os.path.join(BOT_DIR, "data", "personas", pid, "whale.json")
    return os.path.join(BOT_DIR, "data", "whale.json")


def whale_get(uid, persona=""):
    rec = _load_json_file(whale_file_of(persona)).get(str(uid)) or {}
    try:
        return int(rec.get("w") or 0)
    except (TypeError, ValueError):
        return 0


def whale_set(uid, value=None, delta=None, note="", persona=""):
    """**人工改**鲸圆券：设成 value 或加减 delta（2026-11-05 用户：「让我可以自由设置数量」）。

    · 允许填**任意整数**（不像好感度夹 -255~256）；负数按 0 处理（券不该是负的）
    · 和 `aff_set` 一样在 log 里留一条「网页手动」，网页和 /券 都看得到
    · 落盘走 `.tmp` + `os.replace`，避免插件正好在读到半个文件
    """
    uid = re.sub(r"\D", "", str(uid or ""))[:12]
    if len(uid) < 5:
        return {"ok": False, "msg": "QQ 号看着不太对"}
    if value is None and delta is None:
        return {"ok": False, "msg": "要么给 value（设成多少），要么给 delta（加减多少）"}
    path = whale_file_of(persona)
    data = _load_json_file(path)
    rec = data.get(uid)
    if not isinstance(rec, dict):
        rec = {"w": 0}
        data[uid] = rec
    try:
        old = int(rec.get("w") or 0)
    except (TypeError, ValueError):
        old = 0
    try:
        new = int(value) if value is not None else (old + int(delta))
    except (TypeError, ValueError):
        return {"ok": False, "msg": "得是整数"}
    if new < 0:
        new = 0
    rec["w"] = new
    rec["at"] = time.strftime("%Y-%m-%d %H:%M")
    if new != old:
        log = rec.get("log")
        if not isinstance(log, list):
            log = []
        log.append({"t": time.strftime("%m-%d %H:%M"), "d": new - old, "w": new,
                    "r": (note or "网页手动调整")[:60]})
        rec["log"] = log[-20:]
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        os.replace(tmp, path)
        _fix_owner(path)
    except OSError as e:
        return {"ok": False, "msg": "写入失败：%r" % (e,)}
    return {"ok": True, "uid": uid, "old": old, "w": new, "delta": new - old}


def roster_all(persona=""):
    """C2：花名册（名字 / 好感度 / 词云）。

    `persona`：空/feiyu = 肥鱼那份；`taffy` = 塔菲那份（`data/personas/taffy/*.json`）。
    """
    roster_file, aff_file, traits_file = _persona_data_files(persona)
    roster = _load_json_file(roster_file)
    aff = _load_json_file(aff_file)
    traits = _load_json_file(traits_file)
    # 鲸圆券（2026-11-05：花名册里也要能看/改，用户：「把鲸圆券的调整设置也部署到花名册网页里」）
    whale = _load_json_file(whale_file_of(persona))
    out = []
    for gid, members in roster.items():
        if not isinstance(members, dict):
            continue
        rows = []
        for uid, rec in members.items():
            if not isinstance(rec, dict):
                continue
            a = aff.get(str(uid)) or {}
            if not isinstance(a, dict):
                a = {}
            try:
                av = int(a.get("v", 0) or 0)
            except (TypeError, ValueError):
                av = 0
            log = a.get("log")
            if not isinstance(log, list):
                log = []
            wrec = whale.get(str(uid)) or {}
            try:
                wv = int(wrec.get("w") or 0)
            except (TypeError, ValueError):
                wv = 0
            rows.append({
                "uid": str(uid),
                "name": str(rec.get("name") or a.get("name") or uid),
                "aff": av,
                "whale": wv,
                "count": int(rec.get("count", 0) or 0),
                "first": str(rec.get("first") or ""),
                "last": str(rec.get("last") or ""),
                "style": _style_of(rec),
                # 专属词云（插件归纳，最多 9 个）
                "tags": _tags_of(traits, uid),
                # 最近 N 条「影响好感度的话」（插件写进来的）
                "log": [x for x in log[-20:] if isinstance(x, dict)],
            })
        rows.sort(key=lambda r: (-r["count"], r["name"]))
        out.append({"gid": str(gid), "title": chat_title("群 %s" % gid),
                    "members": rows, "total": len(rows)})
    out.sort(key=lambda g: -g["total"])
    pid = str(persona or "").strip()
    return {"groups": out, "people": sum(g["total"] for g in out),
            "ts": time.strftime("%Y-%m-%d %H:%M"),
            "persona": pid or "feiyu", "persona_name": _persona_title(pid)}


def me_lookup(qq):
    """公开的个人好感度查询：只吐这一个人的数据。

    ⚠️ QQ 号不是密码，谁都能输别人的号 —— 所以这里**只给公开信息**：
    分值、排名区间（不给具体名次）、最近影响好感度的原话、聊过几条。
    绝不返回群列表、总人数、其他人的名字或 QQ 号。
    """
    qq = re.sub(r"\D", "", str(qq or ""))[:12]
    if len(qq) < 5:
        return {"ok": False, "msg": "QQ 号看着不太对"}

    aff = _load_json_file(AFF_FILE)
    a = aff.get(qq)
    if not isinstance(a, dict):
        return {"ok": False, "msg": "还没有这个人的记录。先在群里跟她说说话吧～"}

    try:
        av = int(a.get("v", 0) or 0)
    except (TypeError, ValueError):
        av = 0

    # 最近影响好感度的原话（最多 20 条，倒序）
    log = a.get("log")
    if not isinstance(log, list):
        log = []
    items = []
    for x in log[-20:]:
        if not isinstance(x, dict):
            continue
        items.append({
            "t": str(x.get("t") or ""),
            "d": int(x.get("d") or 0),
            "k": str(x.get("k") or ""),
            "r": str(x.get("r") or "")[:80],
        })
    items.reverse()

    # 聊过多少条 / 最近一次（从花名册里汇总，但不暴露是哪个群）
    count = 0
    last = ""
    roster = _load_json_file(ROSTER_FILE)
    for _gid, members in roster.items():
        if not isinstance(members, dict):
            continue
        rec = members.get(qq)
        if not isinstance(rec, dict):
            continue
        try:
            count = max(count, int(rec.get("count", 0) or 0))
        except (TypeError, ValueError):
            pass
        l = str(rec.get("last") or "")
        if l > last:
            last = l

    # 只给"印象档位"，不给精确名次
    return {
        "ok": True,
        "qq": qq,
        "name": str(a.get("name") or qq),
        "aff": av,
        "mood": _aff_mood_text(av),
        "count": count,
        "last": last,
        "tags": _tags_of(_load_json_file(TRAITS_FILE), qq),
        "log": items,
        "ts": time.strftime("%Y-%m-%d %H:%M"),
    }


def _aff_mood_text(v):
    """分值 → 一句印象描述（不暴露具体规则）。"""
    try:
        v = int(v)
    except (TypeError, ValueError):
        v = 0
    if v >= 200:
        return "已经黏到不行了"
    if v >= 120:
        return "很喜欢你"
    if v >= 50:
        return "挺喜欢你"
    if v >= 10:
        return "对你印象不错"
    if v > -10:
        return "没什么特别感觉"
    if v > -80:
        return "有点不喜欢你"
    return "很不待见你"


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
        c["dt"] = m.get("dt", "")
        c["dir"] = m["dir"]
    out = [seen[k] for k in order]
    for c in out:
        c["title"] = chat_title(c["chat"])
    out.sort(key=lambda x: x["dt"] or "", reverse=True)
    return out



OB_HTTP = os.environ.get("FISH_OB_HTTP", "http://127.0.0.1:3000")
OB_TOKEN = os.environ.get("FISH_OB_TOKEN",
                          "mbVTYSJGtMmi9LkNF_t2QjJLlX9pQhqyoGOPtINfzAE")


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


# ---------------------------------------------------------------- 原生聊天页的实时接口（2026-10-29）
# 老控制台的聊天页读的是 journald 日志（4 秒轮询、会丢、发不了图）。
# 这里给「肥鱼聊天」这个独立 App 用的是一手的 OneBot 数据：
#   /api/ob/convs    会话列表（群 + 好友，带最近一条）
#   /api/ob/history  某个会话的历史消息（get_group_msg_history / get_friend_msg_history）
#   /api/ob/members  群成员（@ 用）
#   /api/ob/send     发消息（文字 / @ / 引用 / 图片）
#   /api/ob/voice    发语音（走服务器 TTS，和老的 🎤 一样）
_OB_SELF = {"id": "", "ts": 0.0}


def ob_self_id():
    """机器人自己的 QQ（判断消息方向用），10 分钟缓存一次。"""
    now = time.time()
    if _OB_SELF["id"] and now - _OB_SELF["ts"] < 600:
        return _OB_SELF["id"]
    try:
        r = ob_call("get_login_info", {})
        uid = str((r.get("data") or {}).get("user_id") or "")
        if uid:
            _OB_SELF["id"] = uid
            _OB_SELF["ts"] = now
    except Exception:
        pass
    return _OB_SELF["id"]


def ob_parse_chat(chat):
    """'群 123' / 'group:123' / '私聊 9' / 'private:9' → ('group'|'private', '123')"""
    s = (chat or "").strip()
    s = s.replace("：", ":").replace(" ", ":")
    parts = [x for x in re.split(r"[:]", s) if x]
    if len(parts) < 2:
        return "", ""
    kind, cid = parts[0], parts[-1]
    if kind in ("群", "group", "g"):
        kind = "group"
    elif kind in ("私聊", "private", "p", "好友", "friend"):
        kind = "private"
    else:
        return "", ""
    if not cid.isdigit():
        return "", ""
    return kind, cid


def _ob_seg_text(msg):
    """把 OneBot 的 message 段数组压成「能显示的东西」。"""
    text_parts, imgs, ats, reply = [], [], [], ""
    voice = ""
    for seg in msg or []:
        if not isinstance(seg, dict):
            continue
        t = seg.get("type")
        d = seg.get("data") or {}
        if t == "text":
            text_parts.append(d.get("text") or "")
        elif t == "image":
            imgs.append({"url": d.get("url") or "", "sub": d.get("sub_type"),
                         "summary": d.get("summary") or ""})
        elif t == "at":
            qq = str(d.get("qq") or "")
            ats.append(qq)
            text_parts.append("@%s" % ("全体成员" if qq == "all" else qq))
        elif t == "reply":
            reply = str(d.get("id") or "")
        elif t == "face":
            text_parts.append("[表情]")
        elif t == "record":
            text_parts.append("[语音]")
            voice = d.get("url") or ""
        elif t == "video":
            text_parts.append("[视频]")
        elif t == "file":
            text_parts.append("[文件]")
    return "".join(text_parts).strip(), imgs, ats, reply, voice


def ob_norm_msg(m, self_id=""):
    sender = m.get("sender") or {}
    text, imgs, ats, reply, voice = _ob_seg_text(m.get("message"))
    uid = str(m.get("user_id") or "")
    ts = int(m.get("time") or 0)
    return {
        "id": str(m.get("message_id") or ""),
        "uid": uid,
        "who": clean_name(sender.get("card") or sender.get("nickname") or uid),
        "dir": "out" if (self_id and uid == str(self_id)) else "in",
        "text": text,
        "imgs": imgs,
        "ats": ats,
        "quote": reply,
        "voice": voice,
        "ts": ts,
        "dt": time.strftime("%m-%d %H:%M:%S", time.localtime(ts)) if ts else "",
    }


def ob_history(chat, count=40):
    kind, cid = ob_parse_chat(chat)
    if not kind:
        return {"ok": False, "msg": "会话格式不对"}
    count = max(5, min(200, int(count or 40)))
    self_id = ob_self_id()
    try:
        if kind == "group":
            r = ob_call("get_group_msg_history",
                        {"group_id": int(cid), "count": count}, timeout=15)
        else:
            try:
                r = ob_call("get_friend_msg_history",
                            {"user_id": int(cid), "count": count}, timeout=15)
            except Exception:
                r = ob_call("get_friend_msg_history",
                            {"user_id": int(cid), "message_seq": 0, "count": count}, timeout=15)
    except Exception as e:
        return {"ok": False, "msg": "取历史失败：%s" % e}
    if not _ob_ok(r):
        return {"ok": False, "msg": (r or {}).get("wording") or "OneBot 拒绝"}
    data = r.get("data") or {}
    msgs = data.get("messages") if isinstance(data, dict) else data
    out = [ob_norm_msg(m, self_id) for m in (msgs or []) if isinstance(m, dict)]
    out.sort(key=lambda x: x["ts"])
    return {"ok": True, "chat": chat, "messages": out, "self_id": self_id}


def ob_convs(limit=80):
    """会话列表：直接问 OneBot 要「当前还在的群 + 好友」。

    用户要求（2026-10-29）：一进 App 就要能看到**所有群**，不要「群收纳盒」那种分组；
    已退出 / 被踢的群**不显示** —— `get_group_list` 本身只返回当前在的群，
    所以这里**每次现取、不走 10 分钟的群名缓存**（缓存里可能还留着刚退掉的群）。
    """
    groups, friends = {}, {}
    try:
        r = ob_call("get_group_list", {}, timeout=20)
        for g in ((r.get("data") or []) if _ob_ok(r) else []):
            gid = str(g.get("group_id") or "")
            if gid:
                groups[gid] = clean_name(g.get("group_name") or gid)
    except Exception:
        groups = {}
    try:
        r = ob_call("get_friend_list", {}, timeout=20)
        for f in ((r.get("data") or []) if _ob_ok(r) else []):
            uid = str(f.get("user_id") or "")
            if uid:
                friends[uid] = clean_name(f.get("remark") or f.get("nickname") or uid)
    except Exception:
        friends = {}
    if not groups and not friends:            # 全挂了才退回缓存
        names = _name_cache()
        groups = names.get("groups") or {}
        friends = names.get("friends") or {}

    # 最近一条（只用来排序/预览，取不到也不影响列表出全）
    # conversations() 的字段是 {chat,title,count,last,ts,dt,dir}，预览要用 `last`；
    # 注意 ts 有时是字符串（日志解析出来的），排序前必须转成 int，否则 sort 里会
    # `TypeError: bad operand type for unary -: 'str'`（2026-10-29 踩过）。
    def _ts_int(v):
        try:
            return int(float(v))
        except (TypeError, ValueError):
            return 0

    last = {}
    try:
        for it in conversations(400):
            ch = it.get("chat") or ""
            ts = _ts_int(it.get("ts"))
            if ch and (ch not in last or ts > last[ch]["ts"]):
                last[ch] = {"ts": ts, "text": it.get("last") or ""}
    except Exception:
        pass

    rows = []
    for gid, title in groups.items():
        ch = "群 %s" % gid
        rows.append({"chat": ch, "title": title or ch, "kind": "group", "id": str(gid),
                     "last": (last.get(ch) or {}).get("text", ""),
                     "ts": _ts_int((last.get(ch) or {}).get("ts"))})
    for uid, title in friends.items():
        ch = "私聊 %s" % uid
        rows.append({"chat": ch, "title": title or ch, "kind": "private", "id": str(uid),
                     "last": (last.get(ch) or {}).get("text", ""),
                     "ts": _ts_int((last.get(ch) or {}).get("ts"))})
    # 群排前面（都是没聊过的就按名字），聊过的往上顶
    rows.sort(key=lambda x: (0 if x["kind"] == "group" else 1,
                             -(x.get("ts") or 0), x.get("title") or ""))
    return {"ok": True, "groups": len(groups), "friends": len(friends),
            "items": rows[:max(1, min(400, int(limit or 80)))]}


def ob_members(gid):
    try:
        r = ob_call("get_group_member_list", {"group_id": int(gid)}, timeout=20)
    except Exception as e:
        return {"ok": False, "msg": str(e)}
    if not _ob_ok(r):
        return {"ok": False, "msg": (r or {}).get("wording") or "取不到成员"}
    out = []
    for m in (r.get("data") or []):
        out.append({"uid": str(m.get("user_id")),
                    "name": clean_name(m.get("card") or m.get("nickname") or ""),
                    "role": m.get("role") or "member"})
    return {"ok": True, "items": out}


def ob_send(chat, text, reply="", ats=None, images=None, faces=None, image_b64=None):
    kind, cid = ob_parse_chat(chat)
    if not kind:
        return {"ok": False, "msg": "会话格式不对"}
    msg = []
    if reply:
        msg.append({"type": "reply", "data": {"id": str(reply)}})
    for a in (ats or []):
        a = str(a)
        if a.isdigit() or a == "all":
            msg.append({"type": "at", "data": {"qq": a}})
    if (ats or []) and text:
        msg.append({"type": "text", "data": {"text": " "}})
    if text:
        msg.append({"type": "text", "data": {"text": str(text)}})
    # QQ 系统表情（和 Stapxs 表情面板同一套 id）
    for f in (faces or []):
        f = str(f).strip()
        if f.isdigit():
            msg.append({"type": "face", "data": {"id": f}})
    for u in (images or []):
        msg.append({"type": "image", "data": {"file": u}})
    # 手机相册/相机选的本地图：base64 交给协议端
    if image_b64:
        try:
            b = str(image_b64)
            if "," in b:
                b = b.split(",", 1)[1]
            msg.append({"type": "image", "data": {"file": "base64://" + b}})
        except Exception:
            pass
    if not msg:
        return {"ok": False, "msg": "空消息"}
    act = "send_group_msg" if kind == "group" else "send_private_msg"
    payload = {"message": msg}
    payload["group_id" if kind == "group" else "user_id"] = int(cid)
    try:
        r = ob_call(act, payload, timeout=60)
    except Exception as e:
        return {"ok": False, "msg": "发送失败：%s" % e}
    ok = _ob_ok(r)
    return {"ok": ok, "msg": "" if ok else ((r or {}).get("wording") or "发送失败"),
            "message_id": str(((r or {}).get("data") or {}).get("message_id") or "")}


def ob_recall(message_id):
    """撤回一条消息（只能撤自己发的）——Stapxs 有，原生版也对齐。"""
    mid = str(message_id or "").strip()
    if not mid:
        return {"ok": False, "msg": "没有 message_id"}
    try:
        r = ob_call("delete_msg", {"message_id": int(mid)}, timeout=15)
    except Exception as e:
        return {"ok": False, "msg": "撤回失败：%s" % e}
    ok = _ob_ok(r)
    return {"ok": ok, "msg": "" if ok else ((r or {}).get("wording") or "撤回失败")}


def ob_faces():
    """QQ 系统表情目录（id ↔ 名字），148 个，服务器上现成的。"""
    p = os.path.join(BOT_DIR, "data", "face_catalog.json")
    items = []
    try:
        with open(p, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        if isinstance(d, dict):
            for k, v in d.items():
                if str(k).isdigit():
                    items.append({"id": str(k), "name": str(v)})
        items.sort(key=lambda x: int(x["id"]))
    except (OSError, ValueError):
        pass
    return {"ok": True, "items": items}





# ---------------- 群名 / 好友昵称（会话列表显示用） ----------------
_NAME_CACHE = {"ts": 0.0, "groups": {}, "friends": {}}
NAME_TTL = 600
_NAME_TAG = re.compile(r"<[^<>]{0,32}>")


def clean_name(s):
    """QQ 群名里常夹 <$ÿĀ…> 这类内部标记，显示前剔掉。"""
    s = _NAME_TAG.sub("", s or "")
    s = re.sub(r"[\x00-\x1f]", "", s)
    return s.strip()


def _name_cache():
    """群名 + 好友昵称，10 分钟刷新一次；拉不到就沿用上次的。"""
    now = time.time()
    if _NAME_CACHE["ts"] and now - _NAME_CACHE["ts"] < NAME_TTL:
        return _NAME_CACHE
    groups, friends = {}, {}
    try:
        for g in ((ob_call("get_group_list", {}, timeout=5) or {}).get("data") or []):
            gid = str(g.get("group_id") or "")
            nm = clean_name(g.get("group_name") or "")
            if gid and nm:
                groups[gid] = nm
    except Exception:
        pass
    try:
        for f in ((ob_call("get_friend_list", {}, timeout=5) or {}).get("data") or []):
            uin = str(f.get("user_id") or "")
            nm = clean_name(f.get("remark") or f.get("nickname") or "")
            if uin and nm:
                friends[uin] = nm
    except Exception:
        pass
    if not groups:
        groups = _NAME_CACHE["groups"]
    if not friends:
        friends = _NAME_CACHE["friends"]
    _NAME_CACHE.update({"ts": now, "groups": groups, "friends": friends})
    return _NAME_CACHE


def chat_title(chat):
    """『群 1000000003』→ 群名；『私聊 1000000002』→ 好友昵称；认不出就原样。"""
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return chat or "未知"
    kind, ident = m.group(1), m.group(2)
    c = _name_cache()
    if kind == "群":
        return c["groups"].get(ident) or ("群 " + ident)
    return c["friends"].get(ident) or ("私聊 " + ident)


SENT_LOG = "/opt/qqbot/data/manual_sent.log"


def log_sent(chat, text, voice=False, ats=None, quote=None):
    """把手动发送的消息记到日志文件，聊天记录才能看到

    @ / 引用也一起记进去（`[引用]「谁：原文」@某人 正文`），
    这样聊天页能把「引用」和「@了谁」显示出来，不然自己发的消息看不出区别。
    """
    try:
        os.makedirs(os.path.dirname(SENT_LOG), exist_ok=True)
        m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
        if not m:
            return
        key = ("group:" if m.group(1) == "群" else "private:") + m.group(2)
        body = ("【语音】" + text) if voice else text
        if quote:
            body = "[引用]" + quote_mark(quote) + body
        if ats:
            body = "".join("@" + (a.get("name") or a.get("uid") or "") + " "
                           for a in ats) + body
        with open(SENT_LOG, "a", encoding="utf-8") as f:
            f.write("%s [发送] %s -> %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), key, body))
    except Exception as e:
        print("log_sent 失败", e)


def quote_mark(quote):
    """引用标记：有原文就带上「谁：原话」，只有「[引用]」也能显示个引用角标。

    以前截 40 字，长引用在两端都只剩半句；现在放到 120，聊天页能看全（日志行也还读得下去）。
    """
    text = re.sub(r"\s+", " ", str((quote or {}).get("text") or "")).strip()[:120]
    who = str((quote or {}).get("who") or "").strip()
    if not text:
        return ""
    return "「%s」" % ((who + "：" + text) if who else text)


# ---------------- @ 某人 / 引用回复 ----------------
_MEMBER_CACHE = {}      # 群号 -> (时间, [成员…])，成员列表拉一次缓存 10 分钟
_MEMBER_TTL = 600


def group_members(gid, fresh=False):
    """群成员（uid + 显示名）。群里人多时 get_group_member_list 有点慢，所以缓存。"""
    key = str(gid)
    now = time.time()
    hit = _MEMBER_CACHE.get(key)
    if hit and not fresh and now - hit[0] < _MEMBER_TTL:
        return hit[1]
    try:
        res = ob_call("get_group_member_list", {"group_id": int(gid)}, timeout=12)
        rows = []
        for mb in (res.get("data") or []):
            uid = str(mb.get("user_id") or "")
            if not uid:
                continue
            nick = clean_name(mb.get("nickname") or "")
            card = clean_name(mb.get("card") or "")
            rows.append({"uid": uid, "name": card or nick or uid, "nick": nick,
                         "role": mb.get("role") or "", "robot": bool(mb.get("is_robot"))})
        if rows:
            _MEMBER_CACHE[key] = (now, rows)
            return rows
    except Exception as e:
        print("group_members 失败", e)
    return hit[1] if hit else []


def members_of(chat):
    """会话可 @ 的人：群 → 群成员；私聊 → 对方一个人。"""
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return []
    if m.group(1) == "私聊":
        uid = m.group(2)
        return [{"uid": uid, "name": chat_title(chat) or uid, "nick": "", "role": ""}]
    return group_members(m.group(2))


def _hist_text(h):
    """把 OneBot 历史里的消息段拼回可读文本（和插件日志同一套写法）。"""
    out = []
    for seg in (h.get("message") or []):
        t = seg.get("type")
        d = seg.get("data") or {}
        if t == "text":
            out.append(str(d.get("text") or ""))
        elif t == "at":
            out.append("@" + str(d.get("qq") or ""))
        elif t == "face":
            out.append("[表情]")
        elif t in ("image", "mface"):
            out.append("[图片]")
    return "".join(out).strip()


def _norm_text(s):
    return re.sub(r"\s+", "", s or "").replace("[引用]", "")


def find_reply_id(chat, quote):
    """找到「被引用那条」的 message_id。

    控制台只看得到日志（昵称 + 文本 + 秒级时间），没有消息 id，
    所以在 OneBot 最近 40 条历史里按「文本一致 + 时间最近 + 昵称对上」对一条出来。
    对不上就返回 None，调用方退化成纯文本引用（不影响发送）。
    """
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m or not isinstance(quote, dict):
        return None
    want = _norm_text(str(quote.get("text") or ""))
    if len(want) < 1:
        return None
    who = str(quote.get("who") or "").strip()
    ts = str(quote.get("ts") or "").strip()
    try:
        if m.group(1) == "群":
            res = ob_call("get_group_msg_history",
                          {"group_id": int(m.group(2)), "count": 40}, timeout=12)
        else:
            res = ob_call("get_friend_msg_history",
                          {"user_id": int(m.group(2)), "count": 40}, timeout=12)
        hist = (res.get("data") or {}).get("messages") or []
    except Exception as e:
        print("find_reply_id 拉历史失败", e)
        return None

    best, best_score = None, None
    for h in hist:
        got = _norm_text(_hist_text(h))
        if not got:
            continue
        if got[:12] != want[:12] and want[:12] not in got:
            continue
        sender = h.get("sender") or {}
        nm = clean_name(sender.get("card") or sender.get("nickname") or "")
        nick_ok = bool(who) and (nm == who or who in nm or (nm and nm in who))
        diff = 0
        if ts:
            try:
                ht = time.strftime("%H:%M:%S", time.localtime(int(h.get("time") or 0)))
                a = [int(x) for x in ts.split(":")]
                b = [int(x) for x in ht.split(":")]
                diff = abs(a[0] * 3600 + a[1] * 60 + a[2] - (b[0] * 3600 + b[1] * 60 + b[2]))
            except Exception:
                diff = 0
        score = -3 * diff + (60 if nick_ok else 0) + (10 if got == want else 0)
        if best_score is None or score > best_score:
            best, best_score = h.get("message_id"), score
    return best


def _norm_ats(at):
    """@ 列表归一化：接受 ["123"] 也接受 [{"uid":"123","name":"小明"}]"""
    out = []
    if isinstance(at, str):
        at = [at]
    for a in (at or []):
        if isinstance(a, dict):
            uid = str(a.get("uid") or a.get("qq") or "").strip()
            nm = str(a.get("name") or "").strip()
        else:
            uid = str(a or "").strip()
            nm = ""
        if uid and uid not in [x["uid"] for x in out]:
            out.append({"uid": uid, "name": nm})
    return out


def send_message(chat, text, at=None, quote=None, dry=False):
    """chat 形如『群 1000000003』或『私聊 1000000002』

    at:    [{"uid","name"}] 或 ["uid"] —— 会拼成 OneBot 的 at 段
    quote: {"who","text","ts"} —— 先在历史里找出 message_id，拼 reply 段
    dry:   True 只返回将发送的消息段，不真发（自检/联调用）
    """
    text = (text or "").strip()
    ats = _norm_ats(at)
    if not text and not ats:
        return {"ok": False, "msg": "内容为空"}
    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return {"ok": False, "msg": "会话格式不对：%s" % chat}
    kind, ident = m.group(1), int(m.group(2))

    segs = []
    reply_id = None
    if isinstance(quote, dict) and (quote.get("text") or quote.get("who")):
        if not text:
            return {"ok": False, "msg": "引用时还要写点什么"}
        reply_id = find_reply_id(chat, quote)
        if reply_id is not None:
            segs.append({"type": "reply", "data": {"id": str(reply_id)}})
    used_ats = []
    if ats:
        if kind != "群":
            # 私聊没法 @，直接忽略（别让用户以为发出去了 @）
            used_ats = []
        else:
            used_ats = ats
            for a in ats:
                segs.append({"type": "at", "data": {"qq": a["uid"]}})
                segs.append({"type": "text", "data": {"text": " "}})
    if text:
        segs.append({"type": "text", "data": {"text": text}})

    if dry:
        return {"ok": True, "msg": "预览（没真发）", "dry": True,
                "segments": segs, "reply_matched": reply_id is not None,
                "at": [a["uid"] for a in used_ats]}

    payload = {"message": segs}
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
        log_sent(chat, text, ats=used_ats,
                 quote=quote if isinstance(quote, dict) and (quote.get("text") or quote.get("who")) else None)
        _MSG_CACHE["ts"] = 0     # 让缓存立即失效，重新拉最新
        return {"ok": True, "msg": "已发送",
                "message_id": (res.get("data") or {}).get("message_id"),
                "reply_matched": reply_id is not None}
    return {"ok": False, "msg": res.get("wording") or res.get("message") or "发送失败"}



VOICE_DIR_IN_CONTAINER = "/app/data/voice"
SF_PY = "/opt/voice/.venv/bin/python"
SF_BIN = "/opt/voice/sf_tts.py"
SF_VOICE = os.environ.get("FISH_VOICE_SF_VOICE",
                          "speech:diona:db65ukk50mis739gtebg:wtktidljanikamlvhyaw")
SF_MODEL = "FunAudioLLM/CosyVoice2-0.5B"
SF_SPEED = os.environ.get("FISH_VOICE_SF_SPEED", "1.08")
SF_PITCH = os.environ.get("FISH_VOICE_SF_PITCH", "1.00")
SF_SILK_BIN = "/opt/voice/to_silk.py"
BL_BIN = "/opt/voice/bl_tts.py"          # 百炼版 TTS（2026-10-25 起）
VOICE_LANG_CONSOLE = os.environ.get("FISH_VOICE_LANG", "zh")


def _bl_key():
    """百炼 key（优先环境变量，其次 .env）。"""
    if os.environ.get("FISH_BAILIAN_KEY"):
        return os.environ["FISH_BAILIAN_KEY"]
    try:
        with open("/opt/qqbot/.env", "r", encoding="utf-8") as f:
            for ln in f:
                if ln.startswith("FISH_BAILIAN_KEY="):
                    return ln.split("=", 1)[1].strip()
    except Exception:
        pass
    return ""


def _sf_key():
    if os.environ.get("FISH_SF_KEY"):
        return os.environ["FISH_SF_KEY"]
    try:
        with open("/opt/qqbot/.env", "r", encoding="utf-8") as f:
            for ln in f:
                if ln.startswith("FISH_SF_KEY="):
                    return ln.split("=", 1)[1].strip()
    except Exception:
        pass
    return ""


# ---------------- 音色（2026-10-25 起：唯一音色「肥鱼」）----------------
# 以前是 diona / diona_soft 两套，现在统一成用《10月25日.MP3》复刻的唯一音色。
# 引擎是阿里云百炼（FISH_VOICE_ENGINE=bailian），复刻音色的模型必须跟克隆时一致。
VOICE_STYLES = {
    "feiyu": {
        "name": "肥鱼",
        "desc": "唯一音色：用《10月25日.MP3》复刻（百炼 qwen-audio，只给肥鱼）",
        "uri": "qwen-audio-3.1-tts-flash-feiyu-16a0cbc78c5e49bca7cd47880fc90a08",
        "model": "qwen-audio-3.1-tts-flash",
    },
}
# 老控制台/旧 APK 还会按 diona / diona_soft 来调，一律当「肥鱼」，别报错
VOICE_STYLE_ALIASES = {"diona": "feiyu", "diona_soft": "feiyu"}
VOICE_STYLE_FILE = "/opt/qqbot/data/voice_style.json"


def _read_env_kv(key, default=""):
    try:
        with open("/opt/qqbot/.env", "r", encoding="utf-8") as f:
            for ln in f:
                ln = ln.strip()
                if ln.startswith(key + "="):
                    return ln.split("=", 1)[1].strip()
    except Exception:
        pass
    return default


def _voice_style_now():
    """现在只有一套音色，恒为 feiyu（肥鱼）。"""
    return "feiyu"


def _main_voice_uri():
    """当前主音色（唯一音色「肥鱼」的 voice_id）。"""
    return VOICE_STYLES["feiyu"]["uri"]


def _main_voice_model():
    return VOICE_STYLES["feiyu"].get("model") or "qwen-audio-3.1-tts-flash"


def voice_style_get():
    """当前音色 + 可选项（只有「肥鱼」一个）。"""
    return {
        "ok": True,
        "current": "feiyu",
        "options": [{"id": k, "name": v["name"], "desc": v["desc"]}
                    for k, v in VOICE_STYLES.items()],
    }


def voice_style_set(style):
    """改音色风格：写 .env 的 FISH_VOICE_STYLE，返回新旧值（提示需要重启）。"""
    style = (style or "").strip()
    style = VOICE_STYLE_ALIASES.get(style, style)
    if style not in VOICE_STYLES:
        # 只有一套音色了：不认识的也当「肥鱼」，别让旧界面报错
        style = "feiyu"
    old = _read_env_kv("FISH_VOICE_STYLE", "diona")
    lines = []
    found = False
    try:
        with open("/opt/qqbot/.env", "r", encoding="utf-8") as f:
            for ln in f:
                if re.match(r"^\s*FISH_VOICE_STYLE\s*=", ln):
                    lines.append("FISH_VOICE_STYLE=%s\n" % style)
                    found = True
                else:
                    lines.append(ln)
    except OSError as e:
        return {"ok": False, "msg": "读 .env 失败：%s" % e}
    if not found:
        if lines and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        lines.append("# 音色风格：diona=原音色 / diona_soft=更软的新音色\n")
        lines.append("FISH_VOICE_STYLE=%s\n" % style)
    tmp = "/opt/qqbot/.env.tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            f.writelines(lines)
        os.replace(tmp, "/opt/qqbot/.env")
    except OSError as e:
        return {"ok": False, "msg": "写 .env 失败：%s" % e}
    return {"ok": True, "old": old, "current": style,
            "need_restart": False,           # 热切换，改完立刻生效
            "name": VOICE_STYLES[style]["name"]}



# ---------------- 每个人格一套音色（2026-10-30 用户要求）----------------
# 以前音色只能配「肥鱼」一个（.env 的 FISH_VOICE_BL_VOICE + VOICE_STYLES 里写死一套）。
# 现在按人格存在注册表 `data/personas.json`，插件每条消息热读 → 改完下一条消息生效，不用重启：
#
#   voice                （bool）这个角色会不会**主动**发语音（随机语音 / 短句库）
#   voice_bailian        百炼音色：预置名（Cherry / Serena…）或复刻音色的 voice_id
#   voice_bailian_model  复刻音色必须跟克隆时一致（qwen-audio-3.1-tts-flash）；
#                        预置音色用 qwen3-tts-flash
#   voice_name           给人看的名字（「复刻·肥鱼」这种）
#
# ⚠️ 默认人格（feiyu）没写时回落 .env；**别的人格没写就是没音色**，不许借肥鱼的嗓子。
VOICE_PRESETS = [
    {"id": "Cherry", "name": "Cherry · 甜女声", "model": "qwen3-tts-flash"},
    {"id": "Serena", "name": "Serena · 温柔女声", "model": "qwen3-tts-flash"},
    {"id": "Ethan", "name": "Ethan · 男声", "model": "qwen3-tts-flash"},
    {"id": "Chelsie", "name": "Chelsie · 少女声", "model": "qwen3-tts-flash"},
]
VOICE_MODELS = [
    {"id": "qwen3-tts-flash", "name": "qwen3-tts-flash · 预置音色用这个"},
    {"id": "qwen-audio-3.1-tts-flash", "name": "qwen-audio-3.1-tts-flash · 复刻音色用这个"},
]


def _voice_env_defaults():
    return {
        "engine": _read_env_kv("FISH_VOICE_ENGINE", "bailian"),
        "voice": _read_env_kv("FISH_VOICE_BL_VOICE", ""),
        "model": _read_env_kv("FISH_VOICE_BL_MODEL", "qwen3-tts-flash"),
    }


def persona_voice_of(it):
    """这个角色**实际**用的音色：注册表优先，只有默认人格才回落 .env。

    返回 (voice, model, source)；source 是 "registry" / "env" / ""（没音色）。
    ⚠️ 和插件里的 `_persona_bl_voice()` 必须一致：别的人格没写 = 没音色。
    """
    pid = str((it or {}).get("id") or "")
    env = _voice_env_defaults()
    v = str((it or {}).get("voice_bailian") or "").strip()
    m = str((it or {}).get("voice_bailian_model") or "").strip()
    src = "registry" if v else ""
    if not v and pid == PERSONA_DEFAULT:
        v = env["voice"]
        src = "env" if v else ""
    if not m:
        # 复刻音色的 voice_id 形如 qwen-audio-3.1-tts-flash-xxx，必须配 qwen-audio 模型
        if v.startswith("qwen-audio-3.1-tts-flash-"):
            m = "qwen-audio-3.1-tts-flash"
        else:
            m = env["model"] or "qwen3-tts-flash"
    return v, m, src


def voice_options():
    """每个人格的音色 + 可选项（控制台「她的声音」子页面用）。"""
    reg = persona_registry_load()
    env = _voice_env_defaults()
    items = []
    for it in reg["items"]:
        pid = str(it["id"])
        v, m, src = persona_voice_of(it)
        items.append({
            "id": pid,
            "name": str(it.get("name") or pid),
            "voice": bool(it.get("voice", True)),
            "voice_bailian": str(it.get("voice_bailian") or ""),
            "voice_bailian_model": str(it.get("voice_bailian_model") or ""),
            "voice_name": str(it.get("voice_name") or ""),
            "effective": v,
            "effective_model": m,
            "source": src,
            "ready": bool(v),
            "active": pid == reg["active"],
        })
    return {"ok": True, "engine": env.get("engine"), "env_voice": env.get("voice"),
            "env_model": env.get("model"), "presets": VOICE_PRESETS, "models": VOICE_MODELS,
            "default": PERSONA_DEFAULT, "items": items}


def voice_preview_bytes(pid, text):
    """按某个角色的音色合成一句，返回 (mp3 bytes, 错误)。试听用，不进协议端。"""
    reg = persona_registry_load()
    it = None
    for x in reg["items"]:
        if str(x.get("id")) == str(pid or ""):
            it = x
            break
    if it is None:
        it = {"id": PERSONA_DEFAULT}
    v, m, _src = persona_voice_of(it)
    if not v:
        return None, "「%s」还没配音色" % str(it.get("name") or it.get("id"))
    key = _bl_key()
    if not key:
        return None, "没配置 FISH_BAILIAN_KEY"
    text = (text or "").strip()[:60] or ("你好，我是%s。" % str(it.get("name") or ""))
    out = "/tmp/voice_preview_%d.mp3" % int(time.time() * 1000)
    env = dict(os.environ)
    env["FISH_BAILIAN_KEY"] = key
    try:
        p = subprocess.run([SF_PY, BL_BIN, out, text, v, m, "1.0", "1.0"],
                           capture_output=True, timeout=90, env=env)
        if p.returncode != 0 or not os.path.exists(out):
            return None, (p.stderr or b"")[:200].decode("utf-8", "ignore")
        with open(out, "rb") as f:
            data = f.read()
        try:
            os.remove(out)
        except OSError:
            pass
        return data, ""
    except Exception as e:
        return None, repr(e)


def _ai_env():
    env = {}
    try:
        with open("/opt/qqbot/.env", "r", encoding="utf-8") as f:
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
    key = _bl_key()
    if not key:
        return "", "没配置 FISH_BAILIAN_KEY"
    host = "/tmp/console_voice_%d.mp3" % int(time.time() * 1000)
    env = dict(os.environ)
    env["FISH_BAILIAN_KEY"] = key
    try:
        p1 = subprocess.run([SF_PY, BL_BIN, host, text, _main_voice_uri(),
                             _main_voice_model(), "1.0", "1.0"],
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
        return run(SUDO + "systemctl start fishbot", timeout=60) or "已启动"
    if name == "restart":
        return run(SUDO + "systemctl restart fishbot", timeout=60) or "已重启"
    if name == "stop":
        run("touch /opt/qqbot/data/stop.flag", timeout=10)
        time.sleep(11)
        return run(SUDO + "systemctl stop fishbot", timeout=60) or "已关闭（发过告别）"
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


DEFAULT_BOTCTL = {"only_at": False, "max_len": 26, "speed": 1.0, "chat_rate": -1.0,
                  "poke_rate": 1.0, "ambient": -1.0, "ai": None,
                  # 刷屏惊讶（2026-10-28）：None = 跟随全局 FISH_BURST；True 本群开；False 本群关
                  "burst": None}
# 「默默发表情」的挡位（和插件 .env 的 FISH_AMBIENT_STICKER 默认值 0.03 对齐）：
# -1 表示这个群没单独设过 → 用默认 3%；0 表示这个群关掉
AMBIENT_STEPS = [0.0, 0.01, 0.02, 0.03, 0.05, 0.08, 0.10, 0.15, 0.20, 0.30, 0.50]


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
        try:
            g["poke_rate"] = float(r.get("poke_rate", 1.0))
        except (TypeError, ValueError):
            g["poke_rate"] = 1.0
        try:
            g["ambient"] = float(r["ambient"]) if "ambient" in r else -1.0
        except (TypeError, ValueError):
            g["ambient"] = -1.0
        g["burst"] = r.get("burst", None) if isinstance(r, dict) else None
    out.sort(key=lambda x: x["gid"])
    return out


# ---------------- 拉黑别的机器人（全局，不分群） ----------------
# 名单由用户手动填 QQ 号；插件的 recorder 每条消息都会查一次这个文件，
# 命中的号直接不记录、不回复（改完立刻生效，不用重启 fishbot）。
BOT_BLACKLIST_FILE = "/opt/qqbot/data/bot_blacklist.json"


def _blacklist_load():
    """返回 [{"uid": "123", "note": "备注", "ts": "添加时间"}]，坏文件当空名单。"""
    try:
        with open(BOT_BLACKLIST_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return []
    items = d.get("items") if isinstance(d, dict) else d
    if not isinstance(items, list):
        return []
    out = []
    for it in items:
        if isinstance(it, dict):
            uid = str(it.get("uid") or "").strip()
            note = str(it.get("note") or "")
            ts = str(it.get("ts") or "")
        else:
            uid, note, ts = str(it or "").strip(), "", ""
        if uid.isdigit() and uid not in [x["uid"] for x in out]:
            out.append({"uid": uid, "note": note, "ts": ts})
    return out


def _blacklist_save(items):
    os.makedirs(os.path.dirname(BOT_BLACKLIST_FILE), exist_ok=True)
    tmp = BOT_BLACKLIST_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"items": items, "ts": time.strftime("%Y-%m-%d %H:%M:%S")},
                  f, ensure_ascii=False, indent=2)
    os.replace(tmp, BOT_BLACKLIST_FILE)
    run(SUDO + "chown qqbot:qqbot " + BOT_BLACKLIST_FILE + " 2>/dev/null")


def bot_blacklist_get():
    items = _blacklist_load()
    return {"ok": True, "items": items, "count": len(items)}


def bot_blacklist_set(body):
    """action=add/remove；add 可带 note。返回完整名单，控制台据此重画。"""
    action = (body.get("action") or "").strip()
    uid = str(body.get("uid") or "").strip()
    if not uid.isdigit():
        return {"ok": False, "msg": "QQ 号只能是数字：%s" % uid}
    items = _blacklist_load()
    if action == "add":
        if uid in [x["uid"] for x in items]:
            return {"ok": False, "msg": "已经在名单里了：" + uid, "items": items, "count": len(items)}
        items.append({"uid": uid,
                      "note": str(body.get("note") or "").strip(),
                      "ts": time.strftime("%Y-%m-%d %H:%M")})
        _blacklist_save(items)
        return {"ok": True, "msg": "已拉黑 " + uid, "items": items, "count": len(items)}
    if action == "remove":
        left = [x for x in items if x["uid"] != uid]
        if len(left) == len(items):
            return {"ok": False, "msg": "名单里没有 " + uid, "items": items, "count": len(items)}
        _blacklist_save(left)
        return {"ok": True, "msg": "已解除 " + uid, "items": left, "count": len(left)}
    return {"ok": False, "msg": "action 只能是 add / remove"}


# ---------------- 机器人名单（2026-10-30：改成**手动**维护） ----------------
# 插件那边 `is_bot()` 读同一个文件：名单里的人 → 黑鱼用 BOT_AT_LINES 那套专属回复。
# 自动识别关掉了（误伤过真人），所以这份名单只有两种来源：这个控制台页面、`/bot QQ号`。
BOTS_FILE = "/opt/qqbot/data/bots.json"


def _bots_load():
    """{uid: {why, at, n, chat}} → [{"uid","why","at","chat"}]（新加的排前面）。"""
    try:
        with open(BOTS_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
    except (OSError, ValueError):
        return []
    if not isinstance(d, dict):
        return []
    out = []
    for uid, rec in d.items():
        uid = str(uid or "").strip()
        if not uid.isdigit():
            continue
        rec = rec if isinstance(rec, dict) else {}
        out.append({"uid": uid, "why": str(rec.get("why") or ""),
                    "at": str(rec.get("at") or ""), "chat": str(rec.get("chat") or "")})
    out.sort(key=lambda x: x.get("at") or "", reverse=True)
    return out


def _bots_save(items):
    data = {it["uid"]: {"why": it.get("why") or "", "at": it.get("at") or "",
                        "n": 0, "chat": it.get("chat") or ""} for it in items}
    os.makedirs(os.path.dirname(BOTS_FILE), exist_ok=True)
    tmp = BOTS_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(tmp, BOTS_FILE)
    run(SUDO + "chown ubuntu:ubuntu " + BOTS_FILE + " 2>/dev/null")


def bots_get():
    items = _bots_load()
    return {"ok": True, "items": items, "count": len(items)}


def bots_set(body):
    """action=add / remove；add 可带 why（备注）。返回完整名单，控制台据此重画。"""
    action = str((body or {}).get("action") or "").strip()
    uid = str((body or {}).get("uid") or "").strip().lstrip("@")
    if not uid.isdigit():
        return {"ok": False, "msg": "QQ 号只能是数字", "items": _bots_load()}
    items = _bots_load()
    if action == "add":
        if uid in [x["uid"] for x in items]:
            return {"ok": False, "msg": "已经在名单里了：" + uid, "items": items, "count": len(items)}
        items.append({"uid": uid,
                      "why": str((body or {}).get("why") or "手动添加").strip(),
                      "at": time.strftime("%Y-%m-%d %H:%M"), "chat": ""})
        _bots_save(items)
        return {"ok": True, "msg": "记住了：" + uid + " 是机器人", "items": items, "count": len(items)}
    if action == "remove":
        left = [x for x in items if x["uid"] != uid]
        if len(left) == len(items):
            return {"ok": False, "msg": "名单里没有 " + uid, "items": items, "count": len(items)}
        _bots_save(left)
        return {"ok": True, "msg": "已移除 " + uid, "items": left, "count": len(left)}
    return {"ok": False, "msg": "action 只能是 add / remove"}


# ---------------- 每日群聊归档（极简明文，只留当天） ----------------
# 每天 00:00 到现在的群消息，按群各写一个 txt；只保留「今天」这一个目录，
# 换天后第一次采集时把昨天的整个删掉（用户要求：记录完第二天的就删前一天的）。
DAILY_ROOT = "/opt/qqbot/data/daily"
DAILY_MAXLEN = 500            # 归档里单条最多留这么多字（聊天页只留 200）
DAILY_INTERVAL = 300          # 每 5 分钟重建一次当天文件


def daily_day():
    return time.strftime("%Y-%m-%d")


def daily_collect():
    """全量重建今天的归档文件，并清掉其它日期的目录。

    每次重建而不是追加：一天的消息量不大，重建天然去重、顺序稳定，
    也不怕中途崩溃留下半截文件。
    """
    today = daily_day()
    # journalctl 不认 "today 00:00" 这种写法（会回一句 Failed to parse timestamp），
    # 必须给完整时间；另外它失败时那行错误信息也会被 grep 之外的方式带进来，下面有兜底。
    since = time.strftime("%Y-%m-%d 00:00:00")
    journal = last_lines("'[接收]' -e '[发送]'", since=since, limit=50000)
    extra = []
    try:
        if os.path.exists(SENT_LOG):
            with open(SENT_LOG, "r", encoding="utf-8") as f:
                extra = [ln.rstrip("\n") for ln in f.readlines()[-3000:]]
    except Exception:
        pass
    msgs = _parse_entries(_merge_by_time(journal, extra), DAILY_MAXLEN)

    by_gid = {}
    for m in msgs:
        chat = m.get("chat") or ""
        if not chat.startswith("群 "):
            continue                                  # 只要群聊，私聊不归档
        if (m.get("dt") or "")[:10] != today:
            continue
        by_gid.setdefault(chat.split(" ", 1)[1], []).append(m)

    root = os.path.join(DAILY_ROOT, today)
    os.makedirs(root, exist_ok=True)
    _fix_owner(root, True)    # fishweb 是 root：目录也还回去（**必须 is_dir=True**，否则 664 会把 x 位抹掉）
    total = 0
    for gid, rows in by_gid.items():
        title = chat_title("群 " + gid)
        lines = ["# %s · %s · %d 条" % (title, today, len(rows))]
        for m in rows:
            who = (m.get("who") or "").strip() or ("我" if m.get("dir") == "out" else "?")
            body = (m.get("text") or "").replace("\n", " ").strip()
            if m.get("quote"):
                body = "[引用] " + body
            lines.append("%s %s：%s" % ((m.get("ts") or ""), who, body))
        p = os.path.join(root, gid + ".txt")
        tmp = p + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        os.replace(tmp, p)
        _fix_owner(p)             # 归档文件同理（ACL 兜得住，但审计「data/ 里没有 root」要干净）
        total += len(rows)

    # 只留当天：其它日期目录整个删掉
    dropped = []
    try:
        for name in os.listdir(DAILY_ROOT):
            p = os.path.join(DAILY_ROOT, name)
            if os.path.isdir(p) and name != today:
                shutil.rmtree(p, ignore_errors=True)
                dropped.append(name)
    except OSError:
        pass
    try:
        run(SUDO + "chown -R qqbot:qqbot " + DAILY_ROOT + " 2>/dev/null", timeout=10)
    except Exception:
        pass
    return {"ok": True, "date": today, "groups": len(by_gid), "messages": total,
            "dropped": dropped}


def daily_index():
    """今天各群的归档清单（花名册网页拿它画下载入口）。"""
    today = daily_day()
    root = os.path.join(DAILY_ROOT, today)
    items = []
    try:
        names = os.listdir(root)
    except OSError:
        names = []
    for fn in names:
        if not fn.endswith(".txt"):
            continue
        gid = fn[:-4]
        p = os.path.join(root, fn)
        try:
            st = os.stat(p)
            with open(p, "r", encoding="utf-8") as f:
                head = f.readline().strip()
                n = sum(1 for _ in f)
        except OSError:
            continue
        items.append({"gid": gid, "title": chat_title("群 " + gid),
                      "bytes": st.st_size, "count": n,
                      "mtime": time.strftime("%H:%M", time.localtime(st.st_mtime)),
                      "head": head})
    items.sort(key=lambda x: -x["count"])
    return {"ok": True, "date": today, "items": items}


def daily_file(gid):
    """某个群今天的归档文件路径（不存在返回 None）。"""
    gid = "".join(ch for ch in str(gid or "") if ch.isdigit())
    if not gid:
        return None
    p = os.path.join(DAILY_ROOT, daily_day(), gid + ".txt")
    return p if os.path.isfile(p) else None


def daily_loop():
    """后台线程：每 5 分钟重建一次。启动时也立刻跑一次。"""
    while True:
        try:
            r = daily_collect()
            if r.get("dropped"):
                print("daily: 删掉了旧日期 %s" % ",".join(r["dropped"]), flush=True)
        except Exception as e:
            print("daily 采集失败: %r" % (e,), flush=True)
        time.sleep(DAILY_INTERVAL)


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
    # 戳一戳回复概率：控制台挡位 0.01 / 0.10 … 1.00；没设过就是 1.0（每次都回）
    try:
        cur["poke_rate"] = float(r.get("poke_rate", 1.0))
    except (TypeError, ValueError):
        cur["poke_rate"] = 1.0
    # 默默发表情：这个群单独设过的概率；-1 = 没设过（插件用默认 3%）
    if "ambient" in r:
        try:
            cur["ambient"] = float(r.get("ambient"))
        except (TypeError, ValueError):
            cur["ambient"] = -1.0
    else:
        cur["ambient"] = -1.0
    cur["ambient_steps"] = AMBIENT_STEPS
    cur["ai"] = r.get("ai", None)          # None = 跟随全局；True/False = 本群单独设置
    cur["burst"] = r.get("burst", None)    # 同上：刷屏惊讶（那句「我眼睛都看花了」）
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
    if "ai" in body:
        av = body.get("ai")
        if av is None:
            rec.pop("ai", None)               # 跟随全局
        else:
            rec["ai"] = bool(av)
    if "burst" in body:
        bv = body.get("burst")
        if bv is None:
            rec.pop("burst", None)            # 跟随全局
        else:
            rec["burst"] = bool(bv)
    if "poke_rate" in body:
        try:
            pk = float(body.get("poke_rate"))
        except (TypeError, ValueError):
            pk = 1.0
        # 0.01 ~ 1.00；等于 1.0 视为默认，不落盘（保持 group_rules.json 干净）
        pk = max(0.01, min(1.0, round(pk, 2)))
        if pk >= 0.999:
            rec.pop("poke_rate", None)
        else:
            rec["poke_rate"] = pk
    if "ambient" in body:
        try:
            am = float(body.get("ambient"))
        except (TypeError, ValueError):
            am = -1.0
        if am < 0:                       # -1 = 跟随默认（3%）
            rec.pop("ambient", None)
        else:
            rec["ambient"] = max(0.0, min(1.0, round(am, 2)))
    ok, err = _rules_save(data)
    if not ok:
        return {"ok": False, "msg": "保存失败：%s" % err}
    _MSG_CACHE["ts"] = 0
    return {"ok": True, "chat": chat, "gid": gid, "rules": botctl_get(chat)["rules"]}


# ---- 性能模式：控制台三档开关，全局热生效 ----
# 插件每 10 分钟/每次调用前读一次这个文件（按 mtime 缓存），所以改完**不用重启**。
PERF_FILE = os.path.join(BOT_DIR, "data", "perf_mode.json")
TOKEN_FILE = os.path.join(BOT_DIR, "data", "token_usage.json")


def token_usage(days=7):
    """每日 token 用量（插件在每次 AI 调用后累加到 data/token_usage.json）。"""
    try:
        with open(TOKEN_FILE, "r", encoding="utf-8-sig") as f:
            raw = json.load(f) or {}
    except (OSError, ValueError):
        raw = {}
    d = raw.get("days") if isinstance(raw.get("days"), dict) else {}
    today = time.strftime("%Y-%m-%d")
    now = time.time()
    rows = []
    for i in range(max(1, min(30, days)) - 1, -1, -1):
        day = time.strftime("%Y-%m-%d", time.localtime(now - i * 86400))
        v = d.get(day) or {}
        try:
            rows.append({
                "date": day,
                "in": int(v.get("in") or 0),
                "out": int(v.get("out") or 0),
                "cached": int(v.get("cached") or 0),
                "calls": int(v.get("calls") or 0),
            })
        except (TypeError, ValueError):
            rows.append({"date": day, "in": 0, "out": 0, "cached": 0, "calls": 0})
    t = d.get(today) or {}
    return {"ok": True, "today": rows[-1] if rows else {"date": today, "in": 0, "out": 0,
                                                       "cached": 0, "calls": 0},
            "days": rows, "updated": str(raw.get("updated") or "")}
PERF_CHOICES = [
    {"mode": "low", "label": "低", "desc": "省钱档（现在的模式）：不思考、回答短、图搜 8 秒"},
    {"mode": "deft", "label": "正常", "desc": "中间档：轻思考、记得更多（24 条）、看图更爱联网"},
    {"mode": "high", "label": "高", "desc": "全开：中等思考、问题先上网查、看图必联网、token 不心疼"},
]
PERF_KEYS = ("low", "deft", "high")


def perf_get():
    mode = "low"
    try:
        with open(PERF_FILE, "r", encoding="utf-8-sig") as f:
            mode = str((json.load(f) or {}).get("mode") or "low").strip().lower()
    except (OSError, ValueError):
        pass
    if mode not in PERF_KEYS:
        mode = "low"
    upd = ""
    try:
        upd = time.strftime("%Y-%m-%d %H:%M", time.localtime(os.path.getmtime(PERF_FILE)))
    except OSError:
        pass
    return {"ok": True, "mode": mode, "choices": PERF_CHOICES, "updated": upd}


def perf_set(body):
    mode = str((body or {}).get("mode") or "").strip().lower()
    if mode not in PERF_KEYS:
        return {"ok": False, "msg": "档位只能是 low / deft / high"}
    try:
        os.makedirs(os.path.dirname(PERF_FILE), exist_ok=True)
        tmp = PERF_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"mode": mode, "updated": time.strftime("%Y-%m-%d %H:%M:%S")},
                      f, ensure_ascii=False)
        os.replace(tmp, PERF_FILE)
    except OSError as e:
        return {"ok": False, "msg": "保存失败：%s" % (e,)}
    r = perf_get()
    print("perf: -> %s" % mode, flush=True)
    return r


# ---- 图库（和插件共用 data/photolib/index.json，字段保持一致）----
def _fix_owner(path, is_dir=False):
    """fishweb 是 root 跑的，写出来的文件属主也是 root —— 机器人（ubuntu）就写不动了。

    2026-11-02 线上踩过：控制台上传一张图之后，群里 `/图库 加` 一律回「保存失败…」
    （PermissionError: /opt/qqbot/data/photolib/index.json.tmp）。写完 chown 一次就好。
    """
    global _BOT_UID, _BOT_GID
    try:
        if _BOT_UID is None:
            import pwd
            u = pwd.getpwnam("ubuntu")
            _BOT_UID, _BOT_GID = u.pw_uid, u.pw_gid
        os.chown(path, _BOT_UID, _BOT_GID)
        os.chmod(path, 0o775 if is_dir else 0o664)
    except Exception:
        pass


_BOT_UID, _BOT_GID = None, None

PHOTOLIB_DIR = os.path.join(BOT_DIR, "data", "photolib")
PHOTOLIB_FILE = os.path.join(PHOTOLIB_DIR, "index.json")
PHOTOLIB_MAX = 800
PHOTOLIB_MAX_DIST = 10


_VENV_PY = "/opt/qqbot/.venv/bin/python"
_PIL_STATE = {"ok": None}


def _pil_ok():
    """后端跑在系统 python3 上，**没有 Pillow**（2026-10-22 踩到：加图一直报「格式不支持」）。
    这里只探一次，没有就借 venv 里的 python 干活，不去动系统包。"""
    if _PIL_STATE["ok"] is None:
        try:
            from PIL import Image  # noqa: F401
            _PIL_STATE["ok"] = True
        except Exception:
            _PIL_STATE["ok"] = False
    return _PIL_STATE["ok"]


_DHASH_SNIPPET = (
    "import sys,io\n"
    "from PIL import Image\n"
    "raw=sys.stdin.buffer.read()\n"
    "im=Image.open(io.BytesIO(raw)); w,h=im.size\n"
    "g=im.convert('L').resize((9,8),Image.LANCZOS); px=list(g.getdata())\n"
    "bits=0\n"
    "for r in range(8):\n"
    "    b=r*9\n"
    "    for c in range(8):\n"
    "        bits=(bits<<1)|(1 if px[b+c]>px[b+c+1] else 0)\n"
    "print(bits,w,h)\n"
)


def _ph_dhash(raw):
    """64 位 dHash。返回 (hash, w, h)，不是图返回 None。和插件里的算法必须一致。"""
    if _pil_ok():
        try:
            import io as _io
            from PIL import Image
            im = Image.open(_io.BytesIO(raw))
            w, h = im.size
            g = im.convert("L").resize((9, 8), Image.LANCZOS)
            px = list(g.getdata())
            bits = 0
            for row in range(8):
                base = row * 9
                for col in range(8):
                    bits = (bits << 1) | (1 if px[base + col] > px[base + col + 1] else 0)
            return bits, int(w), int(h)
        except Exception:
            return None
    try:                                    # 没有 Pillow：喂给 venv 的 python 算
        r = subprocess.run([_VENV_PY, "-c", _DHASH_SNIPPET], input=raw,
                           capture_output=True, timeout=30)
        parts = (r.stdout or b"").decode().strip().split()
        if len(parts) >= 3:
            return int(parts[0]), int(parts[1]), int(parts[2])
    except Exception as e:
        print("photolib dhash 子进程失败: %r" % (e,), flush=True)
    return None


def _ph_tags(v):
    """标签清洗：`,`/`，`/`、`/空格/`|` 都算分隔，去重、最多 8 个、每个 ≤12 字。

    和插件的 `_ph_tags()` 必须一致（两边共用 data/photolib/index.json）。
    """
    import re as _re
    if isinstance(v, (list, tuple)):
        raw = [str(x) for x in v]
    else:
        raw = _re.split(r"[,，、;；|/\s]+", str(v or ""))
    out = []
    for t in raw:
        t = str(t or "").strip().strip("#＃")[:12]
        if t and t not in out:
            out.append(t)
    out = out[:8]
    out.sort(key=_tag_order_key)          # 2026-11-02 用户：标签按字母顺序排
    return out


def _tag_order_key(t):
    """标签排序键：**按拼音 A→Z**（中文才像字典一样顺眼）。
    2026-11-02 用户：「把标签弄成按照字母顺序排列的」。
    不引第三方库 —— GB2312 一级字库本身就是按拼音排的，直接拿编码字节比大小就行：
    `白`=B0D7 < `碧`=B1CC < `黑`=BADA < `巨`=BExx < `猫`=C3A8 < `泳`=D3BE。
    字母/数字排在中文前面（它们的字节更小），GB2312 里没有的字（生僻字）排最后。
    插件里有一份一模一样的 `_ph_tags()`，改这里记得同步那边。
    """
    out = []
    for ch in str(t or ""):
        try:
            out.append(ch.encode("gb2312"))
        except UnicodeEncodeError:
            out.append(b"\xff" + ch.encode("utf-8"))
    return b"".join(out)


_PY_CACHE = {}


def _tag_pinyin(t):
    """标签 → (全拼, 首字母)，给「拼音也能搜中文标签」用。

    2026-11-02 用户：「给标签加一个搜索，可以拼音搜索也可以中文搜索」。
    拼音靠 pypinyin（装在 `webui/_pylibs`，见文件头）；没装就返回空串，只损失拼音搜索。
    例：`白毛` → ('baimao', 'bm')；`白色T恤` → ('baiseTxu'.lower(), 'bstx')；
    `deepseek` 这种本来就是字母的原样留下 → ('deepseek', 'deepseek')。
    """
    t = str(t or "")
    hit = _PY_CACHE.get(t)
    if hit is not None:
        return hit
    py, ini = "", ""
    try:
        from pypinyin import lazy_pinyin
        parts = lazy_pinyin(t)
        py = "".join(parts).lower()
        ini = "".join(p[:1] for p in parts if p).lower()
    except Exception:
        pass
    _PY_CACHE[t] = (py, ini)
    return py, ini


def photolib_load():
    try:
        with open(PHOTOLIB_FILE, "r", encoding="utf-8-sig") as f:
            raw = json.load(f)
        items = raw.get("items") if isinstance(raw, dict) else raw
        return items if isinstance(items, list) else []
    except (OSError, ValueError):
        return []


def photolib_save(items):
    try:
        os.makedirs(PHOTOLIB_DIR, exist_ok=True)
        tmp = PHOTOLIB_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"items": items}, f, ensure_ascii=False, indent=1)
        os.replace(tmp, PHOTOLIB_FILE)
        _fix_owner(PHOTOLIB_FILE)
        return True
    except OSError as e:
        print("photolib 保存失败: %r" % (e,), flush=True)
        return False


# ---- 图集（相册）2026-11-03 用户：「照搬手机相册的操作」
#   · 每张图有一个 `album` 字段（图集 id）；新图默认进「未分类」
#   · 图集自己存在 albums.json：{albums: [{id, name, at}], default: id}
#   · 「作品/游戏」类图集按标签自动分（原神/明日方舟/…），名字可以改
ALBUMS_FILE = os.path.join(PHOTOLIB_DIR, "albums.json")
DEFAULT_ALBUM = "a_default"
DEFAULT_ALBUM_NAME = "未分类"
# 认得出来的「作品」标签（有这些标签的图会自动进同名图集）
WORK_TAGS = ("原神", "明日方舟", "绝区零", "碧蓝档案", "ba", "蔚蓝档案", "鸣潮", "星穹铁道",
             "卡拉比丘", "白银之城", "崩坏3", "初音未来", "东方", "deepseek")
# 同一个作品的不同写法 → 归到一个图集（不然 ba / 碧蓝档案 会各建一个）
WORK_ALIAS = {"ba": "碧蓝档案", "蔚蓝档案": "碧蓝档案"}


def _work_canon(tag):
    tag = str(tag or "")
    return WORK_ALIAS.get(tag, tag)


def _album_cfg_load():
    try:
        with open(ALBUMS_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        if isinstance(d, dict) and isinstance(d.get("albums"), list):
            return d
    except (OSError, ValueError):
        pass
    return {"albums": [], "default": DEFAULT_ALBUM}


def _album_cfg_save(d):
    try:
        os.makedirs(PHOTOLIB_DIR, exist_ok=True)
        tmp = ALBUMS_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=1)
        os.replace(tmp, ALBUMS_FILE)
        _fix_owner(ALBUMS_FILE)
        return True
    except OSError as e:
        print("图集保存失败: %r" % (e,), flush=True)
        return False


def _album_new_id(name):
    """稳定 id：用名字的 md5 前 8 位（同一个名字永远同一个 id，方便迁移）。"""
    return "a_" + hashlib.md5(str(name).encode("utf-8")).hexdigest()[:8]


def albums_ensure():
    """补齐默认集 + 把「作品标签」对应的集建出来；返回配置。"""
    d = _album_cfg_load()
    names = {str(a.get("name") or "") for a in d["albums"]}
    changed = False
    if DEFAULT_ALBUM_NAME not in names:
        d["albums"].insert(0, {"id": DEFAULT_ALBUM, "name": DEFAULT_ALBUM_NAME, "at": ""})
        changed = True
    if not d.get("default"):
        d["default"] = DEFAULT_ALBUM
        changed = True
    if changed:
        _album_cfg_save(d)
    return d


def _album_id_by_name(d, name):
    for a in d.get("albums") or []:
        if str(a.get("name") or "") == name:
            return str(a.get("id") or "")
    return ""


def _ph_album_of(it, d=None):
    """这张图属于哪个图集（没有就按标签推一个，再不行进默认）。"""
    aid = str(it.get("album") or "")
    if aid:
        return aid
    d = d or albums_ensure()
    tags = [str(t) for t in (it.get("tags") or [])]
    for t in tags:
        if t in WORK_TAGS:
            found = _album_id_by_name(d, _work_canon(t))
            if found:
                return found
    return d.get("default") or DEFAULT_ALBUM


def photolib_migrate_albums():
    """一次性迁移：给所有图分配 album（按作品标签分，其余进未分类）。

    幂等：已经有 album 的不动。
    """
    d = albums_ensure()
    items = photolib_load()

    # ① 先把「别名图集」并到正式名字（ba / 蔚蓝档案 → 碧蓝档案），免得各建一个
    canon_fix = {}
    for a in list(d.get("albums") or []):
        nm = str(a.get("name") or "")
        c = _work_canon(nm)
        if c != nm:
            canon_fix[str(a.get("id"))] = c
    if canon_fix:
        for it in items:
            cur = str(it.get("album") or "")
            if cur in canon_fix:
                it["album"] = ""          # 清空，下面按名字重新分
        # 删掉这些别名集
        d["albums"] = [a for a in d["albums"] if str(a.get("id")) not in canon_fix]
        _album_cfg_save(d)
        d = albums_ensure()

    # ② 补集：作品标签里真的有图用的
    used_works = set()
    for it in items:
        for t in (it.get("tags") or []):
            if str(t) in WORK_TAGS:
                used_works.add(_work_canon(t))
    added = 0
    for w in sorted(used_works):
        if not _album_id_by_name(d, w):
            d["albums"].append({"id": _album_new_id(w), "name": w, "at": ""})
            added += 1
    if added:
        _album_cfg_save(d)

    n = 0
    for it in items:
        if str(it.get("album") or ""):
            continue
        it["album"] = _ph_album_of(it, d)
        n += 1
    if n or canon_fix:
        photolib_save(items)
    return {"ok": True, "assigned": n, "albums": len(d["albums"]), "merged": len(canon_fix)}


def photolib_albums():
    """图集列表：id / 名字 / 张数 / 封面（命中最高那张）。"""
    d = albums_ensure()
    items = photolib_load()
    by = {}
    for it in items:
        aid = _ph_album_of(it, d)
        by.setdefault(aid, []).append(it)
    out = []
    for a in d.get("albums") or []:
        aid = str(a.get("id") or "")
        lst = by.get(aid) or []
        lst.sort(key=lambda x: (int(x.get("hits") or 0), str(x.get("at") or "")), reverse=True)
        cover = ""
        for it in lst:
            pid = str(it.get("id") or "")
            if pid and os.path.exists(_ph_thumb_path(pid)):
                cover = pid
                break
        out.append({"id": aid, "name": str(a.get("name") or ""), "count": len(lst),
                    "cover": cover, "default": aid == (d.get("default") or DEFAULT_ALBUM)})
    return {"ok": True, "default": d.get("default") or DEFAULT_ALBUM,
            "albums": out, "count": len(out)}


def order_by_hits(items, limit=2000):
    items = sorted(items, key=lambda x: (int(x.get("hits") or 0), str(x.get("at") or "")), reverse=True)
    return items[:limit]


def _ph_px(path):
    """读一张图的实际像素长边。**fishweb 跑在系统 python3 上，没有 PIL**，
    所以和 `_ph_dhash` 一样要留一条「调 venv python」的退路（2026-11-03 踩过）。"""
    try:
        if _pil_ok():
            from PIL import Image
            with Image.open(path) as im:
                return max(im.size)
        snippet = ("import sys\n"
                   "from PIL import Image\n"
                   "im=Image.open(sys.argv[1])\n"
                   "print(max(im.size))\n")
        r = subprocess.run([_VENV_PY, "-c", snippet, path],
                           capture_output=True, timeout=20)
        return int((r.stdout or b"0").decode().strip() or 0)
    except Exception as e:
        print("photolib 读尺寸失败 %r" % (e,), flush=True)
        return 0


def album_new(name):
    name = str(name or "").strip()[:20]
    if not name:
        return {"ok": False, "msg": "图集得有个名字"}
    d = albums_ensure()
    if _album_id_by_name(d, name):
        return {"ok": False, "msg": "已经有个叫「%s」的图集了" % name}
    d["albums"].append({"id": _album_new_id(name), "name": name, "at": time.strftime("%Y-%m-%d %H:%M")})
    if not _album_cfg_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "建好图集「%s」" % name, **photolib_albums()}


def album_rename(aid, name):
    name = str(name or "").strip()[:20]
    aid = str(aid or "")
    if not name:
        return {"ok": False, "msg": "新名字呢"}
    d = albums_ensure()
    hit = None
    for a in d["albums"]:
        if str(a.get("id")) == aid:
            hit = a
            break
    if hit is None:
        return {"ok": False, "msg": "没有这个图集"}
    old = str(hit.get("name") or "")
    if name == old:
        return {"ok": True, "msg": "名字没变", **photolib_albums()}
    if _album_id_by_name(d, name):
        return {"ok": False, "msg": "已经有个叫「%s」的图集了" % name}
    hit["name"] = name
    if not _album_cfg_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "「%s」改名为「%s」" % (old, name), **photolib_albums()}


def album_del(aid):
    aid = str(aid or "")
    d = albums_ensure()
    if aid == (d.get("default") or DEFAULT_ALBUM):
        return {"ok": False, "msg": "「未分类」不能删（它是兜底的）"}
    hit = None
    for a in d["albums"]:
        if str(a.get("id")) == aid:
            hit = a
            break
    if hit is None:
        return {"ok": False, "msg": "没有这个图集"}
    # 里面的图挪回未分类
    items = photolib_load()
    n = 0
    for it in items:
        if str(it.get("album") or "") == aid:
            it["album"] = d.get("default") or DEFAULT_ALBUM
            n += 1
    if n:
        photolib_save(items)
    d["albums"] = [a for a in d["albums"] if str(a.get("id")) != aid]
    if not _album_cfg_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "删了图集「%s」，%d 张图回到未分类" % (hit.get("name"), n),
            **photolib_albums()}


def album_move(ids, aid):
    ids = [str(x) for x in (ids or []) if str(x).strip()]
    aid = str(aid or "")
    if not ids:
        return {"ok": False, "msg": "没选图"}
    d = albums_ensure()
    names = {str(a.get("id")): str(a.get("name")) for a in d.get("albums") or []}
    if aid not in names:
        return {"ok": False, "msg": "没有这个图集"}
    items = photolib_load()
    n = 0
    for it in items:
        if str(it.get("id") or "") in ids:
            it["album"] = aid
            n += 1
    if not n:
        return {"ok": False, "msg": "这些图都不在图库里"}
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "把 %d 张图移进「%s」" % (n, names.get(aid)), **photolib_albums()}


def _ph_brief(it, d=None):
    """一条图库记录 → 给前端用的 dict（`photolib_list` 和 `lean` 版共用）。"""
    pid = str(it.get("id") or "")
    return {
        "id": pid,
        "name": str(it.get("name") or ""),
        "desc": str(it.get("desc") or ""),
        "tags": [str(x) for x in sorted((it.get("tags") or []), key=_tag_order_key)],
        "album": _ph_album_of(it, d if d is not None else albums_ensure()),
        "at": str(it.get("at") or ""),
        "hits": int(it.get("hits") or 0),
        "last": str(it.get("last") or ""),
        "w": int(it.get("w") or 0),
        "h": int(it.get("h") or 0),
        # 落盘文件真实长边（2026-11-03：老的 189 张是 512，控制台要标出来方便补原图）
        "px": int(it.get("file_px") or 0),
        "thumb": bool(pid and os.path.exists(_ph_thumb_path(pid))),
    }


def photolib_list(limit=2000, album=None):
    """列图库条目。

    ⚠️ 2026-11-03 修过一个坑：`limit` 本来是 **200**，用户清空后重传了 **326 张**，
    控制台只显示 200、「图库共 200 张」，剩下 126 张在界面上**根本看不到**
    （排序是 `(hits, at)` 倒序，被砍掉的是最老的那批）。默认抬到 2000，
    并且 `/api/photolib` 支持 `?limit=` 覆盖。`total` 给的是**截断前**的总数。
    """
    items = photolib_load()
    total = len(items)
    d = albums_ensure()
    if album:
        items = [it for it in items if _ph_album_of(it, d) == str(album)]
    items = order_by_hits(items, limit)
    out = [_ph_brief(it, d) for it in items]
    return {"ok": True, "count": len(items), "total": total, "items": out}


TAG_BANK_FILE = os.path.join(PHOTOLIB_DIR, "tag_bank.json")


def _tag_bank_load():
    """独立存一份标签库（2026-11-03 用户：「图全删了，但标签保留」）。

    标签本来是挂在每张图上的，一删图就全没了。这里单独存一份，
    图库空了标签库也还在，重传时照样能点选。
    """
    try:
        with open(TAG_BANK_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        if isinstance(d, dict) and isinstance(d.get("tags"), dict):
            return {str(k): int(v or 0) for k, v in d["tags"].items() if str(k).strip()}
    except (OSError, ValueError):
        pass
    return {}


def _tag_bank_save(cnt):
    try:
        os.makedirs(PHOTOLIB_DIR, exist_ok=True)
        tmp = TAG_BANK_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"tags": cnt, "updated": time.strftime("%Y-%m-%d %H:%M")},
                      f, ensure_ascii=False, indent=1)
        os.replace(tmp, TAG_BANK_FILE)
        _fix_owner(TAG_BANK_FILE)
        return True
    except OSError as e:
        print("标签库保存失败: %r" % (e,), flush=True)
        return False


def _tag_bank_merge(tags):
    """把用到的标签记进标签库（只加不减，删图不影响标签库）。"""
    tags = [str(t or "").strip() for t in (tags or [])]
    tags = [t for t in tags if t]
    if not tags:
        return
    cnt = _tag_bank_load()
    changed = False
    for t in tags:
        if t not in cnt:
            cnt[t] = cnt.get(t, 0)
            changed = True
    if changed:
        _tag_bank_save(cnt)


def photolib_clear_images():
    """**删掉所有图（文件 + 条目），但保留标签库和图集**（2026-11-03 用户要求）。

    用户：「把图库里面的图全部删了，我待会儿重新传，标签保留」
    → 先把现有标签全灌进 `tag_bank.json`，再清条目和 jpg 文件。
    """
    items = photolib_load()
    # ① 标签先存进独立标签库（清完图库，控制台里的标签还在）
    cnt = _tag_bank_load()
    for it in items:
        for t in (it.get("tags") or []):
            t = str(t or "").strip()
            if t:
                cnt[t] = cnt.get(t, 0) + 1
    _tag_bank_save(cnt)

    # ② 删文件（原图 + 缩略图）
    n_file = 0
    for it in items:
        pid = str(it.get("id") or "")
        if not pid:
            continue
        for fn in (pid + ".jpg", pid + ".thumb.jpg"):
            p = os.path.join(PHOTOLIB_DIR, fn)
            try:
                if os.path.exists(p):
                    os.remove(p)
                    n_file += 1
            except OSError as e:
                print("删图失败 %s: %r" % (fn, e), flush=True)

    # ③ 清条目
    if not photolib_save([]):
        return {"ok": False, "msg": "清空条目失败（标签库已保存）"}

    # ④ 自动识别队列、去重记录也清掉，免得残留指向不存在的图
    try:
        autotag_save({"enabled": autotag_load().get("enabled", True), "queue": [], "last": ""})
    except Exception as e:
        print("清识别队列失败 %r" % (e,), flush=True)
    try:
        _seen = os.path.join(BOT_DIR, "data", "setu_lib_seen.json")
        if os.path.exists(_seen):
            os.remove(_seen)
    except OSError:
        pass

    return {"ok": True, "msg": "清空了 %d 张图（删了 %d 个文件）；标签库保留 %d 个标签"
                              % (len(items), n_file, len(cnt)),
            "removed": len(items), "files": n_file, "tags": len(cnt),
            **photolib_albums()}


def photolib_tags(sort="name"):
    """标签库：把图库里用过的标签汇总，按用过次数排序（控制台一键点选用）。

    2026-11-02 用户：「把已经添加过的标签加入标签库，可以一键点选」。
    2026-11-03：再并上**独立的标签库文件**（图删光了标签也还在）。
    """
    cnt = {}
    for it in photolib_load():
        for t in (it.get("tags") or []):
            t = str(t or "").strip()
            if not t:
                continue
            cnt[t] = cnt.get(t, 0) + 1
    for k, v in _tag_bank_load().items():
        if k not in cnt:
            cnt[k] = 0            # 图库没有这张图了，但标签还留着（count=0）
    # py / py1：拼音全拼 + 首字母，客户端用它做「拼音搜索」
    out = []
    for k, v in cnt.items():
        py, ini = _tag_pinyin(k)
        out.append({"tag": k, "count": v, "py": py, "py1": ini})
    # 2026-11-02 用户：「每次标签的排列位置都会改变」——默认**按名字排**（稳定），
    # 想看常用的就传 sort=count。名字排序 = **拼音 A→Z**（见 `_tag_order_key`）。
    if str(sort or "name").lower() == "count":
        out.sort(key=lambda x: (-x["count"], _tag_order_key(x["tag"])))
    else:
        out.sort(key=lambda x: _tag_order_key(x["tag"]))
    return {"ok": True, "count": len(out), "tags": out, "similar": _ph_similar_tags(list(cnt))}


def _ph_lev(a, b):
    """编辑距离（标签都很短，直接 DP）。"""
    a, b = str(a), str(b)
    if a == b:
        return 0
    if not a or not b:
        return max(len(a), len(b))
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (0 if ca == cb else 1)))
        prev = cur
    return prev[-1]


def _ph_similar_tags(tags):
    """找出「很像」的标签对（编辑距离 ≤1，或一个包含另一个），给控制台做合并提示。

    2026-11-02 用户要的：「白毛 / 白发」这种重复写法要能合并。
    """
    tags = sorted({str(t or "").strip() for t in tags if str(t or "").strip()})
    out = []
    for i in range(len(tags)):
        for j in range(i + 1, len(tags)):
            a, b = tags[i], tags[j]
            if a == b:
                continue
            d = _ph_lev(a, b)
            contain = (a in b or b in a) and min(len(a), len(b)) >= 2
            # 只看「首字相同 + 后面差一个字」或「包含关系」：
            # 巨乳 / 贫乳 这种首字不同的往往是反义或不同类别，不能提示合并
            prefix_same = bool(a) and bool(b) and a[0] == b[0]
            if contain or (d <= 1 and prefix_same):
                out.append({"a": a, "b": b, "len_a": len(a), "len_b": len(b), "d": d})
    out.sort(key=lambda x: (x["d"], -max(x["len_a"], x["len_b"])))
    return out[:12]


def photolib_tag_merge(old, new):
    """把标签 old 并成 new（改所有用到它的条目），并顺手去重。"""
    old = str(old or "").strip()
    new = str(new or "").strip()
    if not old or not new:
        return {"ok": False, "msg": "要说清把哪个并成哪个"}
    if old == new:
        return {"ok": False, "msg": "两个是同一个标签"}
    items = photolib_load()
    n = 0
    for it in items:
        tgs = [str(x) for x in (it.get("tags") or [])]
        if old not in tgs:
            continue
        out = []
        for t in tgs:
            t2 = new if t == old else t
            if t2 not in out:
                out.append(t2)
        it["tags"] = out
        n += 1
    if not n:
        return {"ok": False, "msg": "没有条目用着「%s」" % old}
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "把「%s」并成「%s」，改了 %d 张" % (old, new, n), **photolib_list()}


def _ph_thumb_save(raw, pid):
    """保存一张：**原图**存 `<id>.jpg`，另存 `<id>.thumb.jpg` 给控制台预览。

    2026-11-03 用户：「为什么图片很不清晰…当时上传的不是原图吗」——
    以前这里 `thumbnail((512,512)) + q85` 直接**覆盖**成 512px，原图根本没留下，
    所以群里 `/图库` `/涩图` 抽出来的都是糊的。现在原图原样落盘。
    旧数据（只有 512 的）没法救，只能保持；新传的都是清楚的。
    """
    path = os.path.join(PHOTOLIB_DIR, pid + ".jpg")
    thumb = os.path.join(PHOTOLIB_DIR, pid + ".thumb.jpg")
    try:
        os.makedirs(PHOTOLIB_DIR, exist_ok=True)
        # ① 原图：**原样写字节**（不重编码，JPEG 转 JPEG 只会掉画质）
        try:
            from PIL import Image as _I
            import io as _io
            im0 = _I.open(_io.BytesIO(raw))
            fmt = (im0.format or "JPEG").upper()
            im0.close()
        except Exception:
            fmt = "JPEG"
        if fmt in ("JPEG", "JPG", "MPO"):
            with open(path, "wb") as f:      # JPEG 直存，一个字节都不动
                f.write(raw)
        else:
            # PNG/WebP/GIF 之类：**只转格式不缩尺寸**（QQ 对 webp 不稳）
            snippet = ("import sys,io\n"
                       "from PIL import Image\n"
                       "raw=sys.stdin.buffer.read()\n"
                       "im=Image.open(io.BytesIO(raw)).convert('RGB')\n"
                       "im.save(sys.argv[1],format='JPEG',quality=95)\n")
            subprocess.run([_VENV_PY, "-c", snippet, path], input=raw,
                           capture_output=True, timeout=60, check=True)
        _fix_owner(path)

        # ② 缩略图：512px 只给控制台看图用，跟发送无关
        if _pil_ok():
            import io as _io2
            from PIL import Image
            im = Image.open(_io2.BytesIO(raw)).convert("RGB")
            im.thumbnail((512, 512), Image.LANCZOS)
            im.save(thumb, format="JPEG", quality=85)
            _fix_owner(thumb)
        else:
            snippet = ("import sys,io\n"
                       "from PIL import Image\n"
                       "raw=sys.stdin.buffer.read()\n"
                       "im=Image.open(io.BytesIO(raw)).convert('RGB')\n"
                       "im.thumbnail((512,512),Image.LANCZOS)\n"
                       "im.save(sys.argv[1],format='JPEG',quality=85)\n")
            subprocess.run([_VENV_PY, "-c", snippet, thumb], input=raw,
                           capture_output=True, timeout=30, check=True)
            _fix_owner(thumb)
        return True
    except Exception as e:
        print("photolib 保存原图/缩略图失败: %r" % (e,), flush=True)
        return False


def _ph_thumb_path(pid):
    """控制台预览用哪张：有 `.thumb.jpg` 用缩略图，没有就退回原图（老的只有原图名）。"""
    th = os.path.join(PHOTOLIB_DIR, os.path.basename(str(pid or "")) + ".thumb.jpg")
    if os.path.exists(th):
        return th
    return os.path.join(PHOTOLIB_DIR, os.path.basename(str(pid or "")) + ".jpg")


def photolib_add(name, desc, raw, by="", tags=None):
    """加一张。**同名可以有多张**（同一个角色不同图）；
    但如果这张图和已有的某张几乎一样（汉明距离 ≤ PHOTOLIB_MAX_DIST），就当成同一张：
    只更新它的名字/描述，不再重复存。"""
    if not raw:
        return {"ok": False, "msg": "没收到图片"}
    got = _ph_dhash(raw)
    if not got:
        return {"ok": False, "msg": "这张图打不开（格式不支持？）"}
    h, w, ht = got
    items = photolib_load()
    name = (name or "").strip()[:30]
    desc = (desc or "").strip()[:300]
    tags = _ph_tags(tags)
    ar = round(w / float(ht or 1), 3)

    # ① 先看是不是同一张图（同图不同名也算同一张，避免重复占位）
    best, best_d = None, 999
    for it in items:
        try:
            d = int(bin(int(it.get("hash") or 0) ^ int(h)).count("1"))
        except (TypeError, ValueError):
            continue
        if d < best_d:
            best, best_d = it, d
    if best is not None and best_d <= PHOTOLIB_MAX_DIST:
        pid = str(best.get("id") or ("p%d" % int(time.time() * 1000)))
        if name:
            best["name"] = name
        if desc:
            best["desc"] = desc
        if tags:
            best["tags"] = tags
        best["at"] = best.get("at") or time.strftime("%Y-%m-%d %H:%M")
        # 2026-11-03：老图被压成 512 的，把原图**重传一次**就自动升级（不新增条目）
        up = ""
        try:
            p0 = os.path.join(PHOTOLIB_DIR, pid + ".jpg")
            old_px = _ph_px(p0) if os.path.exists(p0) else 0
            if max(w, ht) > old_px * 1.2:
                _ph_thumb_save(raw, pid)
                best["w"], best["h"] = w, ht
                up = "（顺手换成了原图 %d×%d）" % (w, ht)
        except Exception as e:
            print("photolib 升级原图失败: %r" % (e,), flush=True)
        if not photolib_save(items):
            return {"ok": False, "msg": "保存失败"}
        return {"ok": True, "id": pid,
                "msg": "这张已经在图库里了，已更新「%s」的信息%s（共 %d 张）"
                       % (best.get("name"), up, len(items)),
                **photolib_list()}

    if len(items) >= PHOTOLIB_MAX:
        return {"ok": False, "msg": "图库满了（%d 张），先删几张" % PHOTOLIB_MAX}
    name = name or ("图%02d" % (len(items) + 1))
    pid = "p%d" % int(time.time() * 1000)
    items.append({"id": pid, "name": name, "desc": desc, "tags": tags, "hash": h, "w": w, "h": ht, "ar": ar,
                  "by": str(by or "console"), "at": time.strftime("%Y-%m-%d %H:%M"), "hits": 0,
                  # 新图默认进「未分类」图集（2026-11-03 用户：照搬手机相册）
                  "album": albums_ensure().get("default") or DEFAULT_ALBUM})
    _tag_bank_merge(tags)                   # 用过的标签记进独立标签库（删图也不会丢）
    # 上传时没给标签 → 进「待识别」队列（开关关着就别排队），
    # 识别不在这里跑：控制台点「立即识别」才叫本机 DSH 干活（2026-11-02 用户要求）。
    if not tags and autotag_load().get("enabled", True):
        items[-1]["tag_queue"] = 1
    _ph_thumb_save(raw, pid)
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    same = sum(1 for x in items if str(x.get("name") or "") == name)
    extra = ("（同名现在共 %d 张）" % same) if same > 1 else ""
    return {"ok": True, "msg": "已加入「%s」%s（图库共 %d 张）" % (name, extra, len(items)),
            "id": pid, **photolib_list()}


def photolib_edit(pid, name=None, desc=None, tags=None, album=None, lean=False):
    """按 id 改一张图的名字/描述/图集（不重新上传图片）。

    2026-11-03 用户：「让我在改图片标签的时候就一键点击分类到哪个相册」→ 多收一个 `album`。
    2026-11-07 用户：「改图片名称和换相册的时候总是改完要卡一下」→ 加 `lean`：
      以前每次都把**整个图库列表**（最多 2000 条，含描述/标签/缩略图探测）序列化回传，
      控制台拿到后还会**重画整个图库**；改一个字段却付全量的代价。
      `lean=True` 只回这一条 + 它现在属于哪个图集，控制台就地改字即可。
    """
    pid = (pid or "").strip()
    if not pid:
        return {"ok": False, "msg": "要改哪一张？"}
    items = photolib_load()
    hit = None
    for it in items:
        if str(it.get("id") or "") == pid:
            hit = it
            break
    if hit is None:
        return {"ok": False, "msg": "图库里没有这张（id 不对？）"}
    if name is not None:
        name = str(name).strip()[:30]
        if name:
            hit["name"] = name
    if desc is not None:
        hit["desc"] = str(desc).strip()[:300]
    if tags is not None:
        hit["tags"] = _ph_tags(tags)
        hit.pop("tag_queue", None)          # 手动给了标签就不用再 AI 识别了
        _tag_bank_merge(hit["tags"])        # 用过的标签记进独立标签库
    if album is not None:
        _aid = str(album or "").strip()
        d = albums_ensure()
        if _aid and any(str(a.get("id")) == _aid for a in d["albums"]):
            hit["album"] = _aid
        elif not _aid:
            hit["album"] = d.get("default") or DEFAULT_ALBUM
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    if lean:
        _d = albums_ensure()
        return {"ok": True, "msg": "已更新「%s」" % hit.get("name"),
                "item": _ph_brief(hit, _d), "album": _ph_album_of(hit, _d),
                "total": len(items)}
    return {"ok": True, "msg": "已更新「%s」" % hit.get("name"), **photolib_list()}


def photolib_del(key):
    """删图。`key` 可以是 id、名字/描述关键词，也可以是 `名字#2`（同名多张时删第 2 张）。"""
    key = (key or "").strip()
    if not key:
        return {"ok": False, "msg": "要删哪张？"}
    items = photolib_load()
    drop_ids = set()

    # ① `名字#2` 形式：按同名分组内的序号删单张
    m = re.match(r"^(.+?)#(\d+)$", key)
    if m:
        base, idx = m.group(1).strip(), int(m.group(2))
        same = [it for it in items
                if str(it.get("name") or "") == base or base in str(it.get("name") or "")]
        if 1 <= idx <= len(same):
            drop_ids.add(str(same[idx - 1].get("id") or ""))
        else:
            return {"ok": False, "msg": "「%s」没有第 %d 张（共 %d 张）" % (base, idx, len(same))}
    else:
        for it in items:
            if key == str(it.get("id") or "") or key in str(it.get("name") or "") \
                    or key in str(it.get("desc") or ""):
                drop_ids.add(str(it.get("id") or ""))

    keep, drop = [], []
    for it in items:
        (drop if str(it.get("id") or "") in drop_ids else keep).append(it)
    if not drop:
        return {"ok": False, "msg": "图库里没有匹配「%s」的" % key}
    for it in drop:
        _pid0 = str(it.get("id") or "")
        for _fn in (_pid0 + ".jpg", _pid0 + ".thumb.jpg"):
            try:
                os.remove(os.path.join(PHOTOLIB_DIR, _fn))
            except OSError:
                pass
    if not photolib_save(keep):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "删掉 %d 张：%s" % (len(drop), "、".join(str(x.get("name")) for x in drop[:5])),
            **photolib_list()}


# ---- 图库「AI 自动打标签」的队列状态（2026-11-02 用户要求）----
# 开关 = 新图要不要进「待识别」队列；识别**只在控制台点「立即识别」时才跑**
# （本机 DSH headless 干活，见 04-脚本\工具\photolib_autotag.ps1）。
AUTOTAG_FILE = os.path.join(PHOTOLIB_DIR, "autotag.json")
_QUEUE_ON = ("1", "true", "yes", "on")


def _tag_queued(it):
    return str(it.get("tag_queue") or "").lower() in _QUEUE_ON


def autotag_load():
    try:
        with open(AUTOTAG_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def autotag_save(d):
    try:
        os.makedirs(PHOTOLIB_DIR, exist_ok=True)
        tmp = AUTOTAG_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=1)
        os.replace(tmp, AUTOTAG_FILE)
        _fix_owner(AUTOTAG_FILE)
        return True
    except OSError as e:
        print("autotag 保存失败: %r" % (e,), flush=True)
        return False


def autotag_status():
    d = autotag_load()
    items = photolib_load()
    q = sum(1 for it in items if _tag_queued(it))
    empty = sum(1 for it in items if not (it.get("tags") or []))
    return {"ok": True, "enabled": bool(d.get("enabled", True)),
            "queue": q, "empty": empty, "last": d.get("last") or {},
            "total": len(items)}


def autotag_set(body):
    """开/关，顺便允许 runner 回写 last（{at, ok, fail, note}）。"""
    d = autotag_load()
    if "enabled" in (body or {}):
        d["enabled"] = bool(body.get("enabled"))
    if isinstance((body or {}).get("last"), dict):
        d["last"] = body["last"]
    if not autotag_save(d):
        return {"ok": False, "msg": "保存失败"}
    return autotag_status()


def photolib_untagged(scope="queue", limit=12):
    """待识别清单：scope=queue（上传时入队的）/ empty（所有没标签的）。"""
    items = photolib_load()
    if str(scope or "queue") == "empty":
        pick = [it for it in items if not (it.get("tags") or [])]
    else:
        pick = [it for it in items if _tag_queued(it)]
    try:
        limit = max(1, min(200, int(limit)))
    except (TypeError, ValueError):
        limit = 12
    out = []
    for it in pick[:limit]:
        pid = str(it.get("id") or "")
        out.append({"id": pid, "name": str(it.get("name") or ""),
                    "w": int(it.get("w") or 0), "h": int(it.get("h") or 0),
                    "thumb": bool(pid and os.path.exists(_ph_thumb_path(pid)))})
    return {"ok": True, "scope": scope, "count": len(pick), "items": out}


def photolib_set_tags(mapping):
    """runner 回写：{id: [标签, ...]}；写完清掉队列标记。"""
    mapping = mapping if isinstance(mapping, dict) else {}
    items = photolib_load()
    n = 0
    for it in items:
        pid = str(it.get("id") or "")
        if pid not in mapping:
            continue
        tags = _ph_tags(mapping.get(pid))
        if not tags:
            continue
        it["tags"] = tags
        it.pop("tag_queue", None)
        it["auto_tag"] = time.strftime("%Y-%m-%d %H:%M")
        n += 1
    if not n:
        return {"ok": False, "msg": "没有可写的标签（id 对不上？）"}
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "写回 %d 张的标签" % n, "count": n}


def photolib_bulk_set(rows):
    """**一次写多张**的名字 / 标签 / 图集（自动打标签 runner 用）。

    2026-11-03：给 326 张图打标签时，走 `/api/photolib/edit` 要开 326 次 POST，
    每次还回一趟全量列表（326 条），慢得没法看 —— 所以加这个批量口。
    rows: [{"id": "...", "name": "可选（空/不传 = 不改名）",
            "tags": [...], "album": "可选（图集 id）"}]
    标签一律过 `_ph_tags()`（和插件同一套：去重、≤8 个、每个 ≤12 字、按拼音排序）。
    """
    rows = rows if isinstance(rows, list) else []
    if not rows:
        return {"ok": False, "msg": "没有要写的行"}
    items = photolib_load()
    by = {}
    for it in items:
        by[str(it.get("id") or "")] = it
    d = albums_ensure()
    alb_ok = {str(a.get("id") or "") for a in d.get("albums") or []}
    n_tags = n_names = n_alb = 0
    used_tags = []
    for row in rows:
        if not isinstance(row, dict):
            continue
        it = by.get(str(row.get("id") or "").strip())
        if it is None:
            continue
        if row.get("tags") is not None:
            tags = _ph_tags(row.get("tags"))
            if tags:
                it["tags"] = tags
                it.pop("tag_queue", None)
                it["auto_tag"] = time.strftime("%Y-%m-%d %H:%M")
                used_tags.extend(tags)
                n_tags += 1
        nm = str(row.get("name") or "").strip()[:30]
        if nm:
            it["name"] = nm
            n_names += 1
        aid = str(row.get("album") or "").strip()
        if aid and aid in alb_ok:
            it["album"] = aid
            n_alb += 1
    if not photolib_save(items):
        return {"ok": False, "msg": "保存失败"}
    if used_tags:
        _tag_bank_merge(used_tags)          # 只写一次标签库
    return {"ok": True, "msg": "标签 %d 张 / 命名 %d 张 / 图集 %d 张" % (n_tags, n_names, n_alb),
            "tags": n_tags, "names": n_names, "albums": n_alb}


def photolib_thumb(pid):
    p = _ph_thumb_path(pid)
    try:
        with open(p, "rb") as f:
            return f.read()
    except OSError:
        return None


def photolib_full(pid):
    """「看大图」要的原图（`<id>.jpg`）。老的 512 图也照样返回，客户端别放大太多就行。"""
    fn = os.path.basename(str(pid or ""))
    if not fn or not re.fullmatch(r"[A-Za-z0-9_\-]{1,64}", fn):
        return None
    p = os.path.join(PHOTOLIB_DIR, fn + ".jpg")
    try:
        with open(p, "rb") as f:
            return f.read()
    except OSError:
        return photolib_thumb(pid)


# ---- 多 API 渠道（和插件共用 data/api_profiles.json）----
# ---- API key 加密存储（纯标准库；插件和 webui 用同一套格式）----
SECRET_ENV = "FISH_SECRET_KEY"
SECRET_FILE = os.path.join(BOT_DIR, "data", ".apikey_secret")
_secret_cache = {"v": None}


def _secret_pass() -> str:
    """取加密口令：环境变量优先，其次 data/.apikey_secret（没有就生成一个）。"""
    if _secret_cache["v"]:
        return _secret_cache["v"]
    p = (os.environ.get(SECRET_ENV) or "").strip()
    if not p:
        try:
            with open(SECRET_FILE, "r", encoding="utf-8") as f:
                p = f.read().strip()
        except OSError:
            p = ""
        if not p:
            import secrets as _secrets
            p = _secrets.token_urlsafe(32)
            try:
                os.makedirs(os.path.dirname(SECRET_FILE), exist_ok=True)
                with open(SECRET_FILE, "w", encoding="utf-8") as f:
                    f.write(p)
                os.chmod(SECRET_FILE, 0o600)
                try:                       # 谁先建的就 chown 给 ubuntu，两边才用同一把口令
                    import pwd
                    u = pwd.getpwnam("ubuntu")
                    os.chown(SECRET_FILE, u.pw_uid, u.pw_gid)
                except (KeyError, OSError, AttributeError):
                    pass
            except OSError:
                pass
    _secret_cache["v"] = p
    return p


def _secret_keys():
    import hashlib
    import hmac as _hmac
    base = hashlib.pbkdf2_hmac("sha256", _secret_pass().encode(), b"fish-api-v1", 120000, 32)
    enc = _hmac.new(base, b"enc", hashlib.sha256).digest()
    mac = _hmac.new(base, b"mac", hashlib.sha256).digest()
    return enc, mac


def secret_enc(plain: str) -> str:
    """明文 key → enc:v1:... ; 空串原样返回。"""
    plain = str(plain or "")
    if not plain:
        return ""
    if plain.startswith("enc:v1:"):
        return plain
    try:
        import base64, hashlib, hmac as _hmac, secrets as _secrets
        enc, mac = _secret_keys()
        nonce = _secrets.token_bytes(16)
        out = bytearray()
        i = 0
        while len(out) < len(plain.encode()):
            out.extend(_hmac.new(enc, nonce + i.to_bytes(4, "big"), hashlib.sha256).digest())
            i += 1
        data = plain.encode()
        ct = bytes(a ^ b for a, b in zip(data, out))
        tag = _hmac.new(mac, nonce + ct, hashlib.sha256).digest()[:16]
        return "enc:v1:" + base64.b64encode(nonce + ct + tag).decode()
    except Exception:
        return plain


def secret_dec(val: str) -> str:
    """enc:v1:... → 明文；明文原样返回；解不开返回空串（调用方会退回 .env）。"""
    val = str(val or "")
    if not val:
        return ""
    if not val.startswith("enc:v1:"):
        return val
    try:
        import base64, hashlib, hmac as _hmac
        raw = base64.b64decode(val[7:])
        if len(raw) < 33:
            return ""
        nonce, ct, tag = raw[:16], raw[16:-16], raw[-16:]
        enc, mac = _secret_keys()
        want = _hmac.new(mac, nonce + ct, hashlib.sha256).digest()[:16]
        if not _hmac.compare_digest(tag, want):
            return ""                     # 被改过 / 口令不对
        out = bytearray()
        i = 0
        while len(out) < len(ct):
            out.extend(_hmac.new(enc, nonce + i.to_bytes(4, "big"), hashlib.sha256).digest())
            i += 1
        return bytes(a ^ b for a, b in zip(ct, out)).decode()
    except Exception:
        return ""


API_FILE = os.path.join(BOT_DIR, "data", "api_profiles.json")
API_STATE_FILE = os.path.join(BOT_DIR, "data", "api_last.json")


def _api_load():
    try:
        with open(API_FILE, "r", encoding="utf-8-sig") as f:
            raw = json.load(f) or {}
    except (OSError, ValueError):
        raw = {}
    if not isinstance(raw, dict):
        raw = {}
    items = [it for it in (raw.get("items") or []) if isinstance(it, dict)]
    return {"active": str(raw.get("active") or ""), "items": items}


def _api_save(data):
    try:
        os.makedirs(os.path.dirname(API_FILE), exist_ok=True)
        tmp = API_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        os.replace(tmp, API_FILE)
        # 里面有 key → 600；但**必须 chown 给 ubuntu**，否则以 ubuntu 跑的插件读不到，
        # 渠道功能会静默失效（2026-10-22 踩到：root:root 600，插件一直回退 .env）
        try:
            import pwd
            u = pwd.getpwnam("ubuntu")
            os.chown(API_FILE, u.pw_uid, u.pw_gid)
        except (KeyError, OSError):
            pass
        try:
            os.chmod(API_FILE, 0o600)      # 里面有 key，别给其他人读
        except OSError:
            pass
        return True
    except OSError as e:
        print("api 保存失败: %r" % (e,), flush=True)
        return False


def _mask(k):
    k = secret_dec(str(k or ""))
    if not k:
        return ""
    if len(k) <= 8:
        return "****"
    return k[:4] + "…" + k[-4:]


def api_last_note():
    """插件记下的「最近一次 AI 调用」：哪个渠道、成功没、什么错。"""
    try:
        with open(API_STATE_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f) or {}
        return {"at": str(d.get("at") or ""), "channel": str(d.get("channel") or ""),
                "model": str(d.get("model") or ""), "url": str(d.get("url") or ""),
                "ok": bool(d.get("ok")), "status": int(d.get("status") or 0),
                "err": str(d.get("err") or "")[:160]}
    except (OSError, ValueError):
        return {}


def api_profiles_get():
    d = _api_load()
    out = []
    for it in d["items"]:
        out.append({
            "name": str(it.get("name") or ""),
            "base": str(it.get("base") or ""),
            "path": str(it.get("path") or "/v1/chat/completions"),
            "model": str(it.get("model") or ""),
            "vision": str(it.get("vision") or ""),
            "note": str(it.get("note") or ""),
            "key_mask": _mask(it.get("key")),
            "has_key": bool(secret_dec(str(it.get("key") or ""))),
            "active": str(it.get("name") or "") == d["active"],
        })
    return {"ok": True, "active": d["active"], "items": out,
            "last": api_last_note(), "env": {"base": "", "model": ""}}


def api_profiles_save(body):
    """新增或修改（按名字定位）。

    · 只需要「名字」；地址/模型留空 = 沿用 .env（只想换 key 时，名字 + Key 填上就行）
    · key 传空 = 保留原来的 key（改地址/模型时不会把 key 抹掉）
    """
    name = str((body or {}).get("name") or "").strip()[:30]
    if not name:
        return {"ok": False, "msg": "起个名字吧（用来区分渠道）"}
    base = str((body or {}).get("base") or "").strip().rstrip("/")
    model = str((body or {}).get("model") or "").strip()
    newkey = str((body or {}).get("key") or "").strip()
    d = _api_load()
    items = d["items"]
    hit = None
    for it in items:
        if str(it.get("name") or "") == name:
            hit = it
            break
    if hit is None and not base and not model and not newkey:
        return {"ok": False, "msg": "至少填一项：地址 / 模型 / Key（留空的会沿用 .env）"}
    rec = hit if hit is not None else {"name": name}
    rec["name"] = name
    if base:
        rec["base"] = base
    elif "base" not in rec:
        rec["base"] = ""              # 空 = 插件侧沿用 AI_BASE
    path = str((body or {}).get("path") or "").strip()
    if path:
        rec["path"] = path
    elif "path" not in rec:
        rec["path"] = "/v1/chat/completions"
    if model:
        rec["model"] = model
    elif "model" not in rec:
        rec["model"] = ""
    vmodel = str((body or {}).get("vision") or "").strip()
    if vmodel:
        rec["vision"] = vmodel
    rec["note"] = str((body or {}).get("note") or "").strip()[:60]
    if newkey:
        rec["key"] = secret_enc(newkey)      # 落盘加密
    if hit is None:
        items.append(rec)
    if not d["active"]:
        d["active"] = name            # 第一个渠道自动设为当前
    if not _api_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": ("已更新「%s」" if hit is not None else "已新增「%s」") % name,
            **api_profiles_get()}


def api_profiles_use(body):
    name = str((body or {}).get("name") or "").strip()
    d = _api_load()
    names = [str(it.get("name") or "") for it in d["items"]]
    if name not in names:
        return {"ok": False, "msg": "没有这个渠道"}
    d["active"] = name
    if not _api_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "已切到「%s」" % name, **api_profiles_get()}


def api_profiles_del(body):
    name = str((body or {}).get("name") or "").strip()
    d = _api_load()
    keep = [it for it in d["items"] if str(it.get("name") or "") != name]
    if len(keep) == len(d["items"]):
        return {"ok": False, "msg": "没有这个渠道"}
    d["items"] = keep
    if d["active"] == name:
        d["active"] = str(keep[0].get("name") or "") if keep else ""
    if not _api_save(d):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "删掉了「%s」" % name, **api_profiles_get()}


def api_test(body):
    """**真打一次**验证渠道/key 能不能用（不改配置、不写盘）。

    body 二选一：
      · `{"name": "88api"}`                     → 用这个渠道已保存的地址/模型/key
      · `{"base","model","key"}`                → 用表单里刚填的（key 留空则沿用该渠道存的）
    返回 `{ok, status, ms, model, channel, reply|msg}`，前端直接把 msg 显示出来。
    """
    b = body or {}
    name = str(b.get("name") or "").strip()
    d = _api_load()
    # 2026-11-11：表单空着就测"当前生效的那个渠道"（手机/WPF 上点「测试」时最常这么用）
    if not name and not str(b.get("base") or "").strip() and not str(b.get("model") or "").strip() \
            and not str(b.get("key") or "").strip():
        name = str(d.get("active") or "").strip()
    it = None
    for x in d["items"]:
        if str(x.get("name") or "") == name and name:
            it = x
            break
    if name and it is None:
        return {"ok": False, "msg": "没有这个渠道：%s" % name}
    base = str(b.get("base") or (it or {}).get("base") or "").strip().rstrip("/")
    model = str(b.get("model") or (it or {}).get("model") or "").strip()
    key = str(b.get("key") or "").strip() or secret_dec(str((it or {}).get("key") or ""))
    if not (base and model and key):
        return {"ok": False,
                "msg": "地址 / 模型 / Key 不全（留空的会沿用该渠道已保存的值；新渠道三项都要填）"}
    if base.endswith("/v1/chat/completions"):
        url = base
    else:
        url = base + "/v1/chat/completions"
    payload = {"model": model, "max_tokens": 40, "temperature": 0.7,
               "messages": [{"role": "user", "content": "只回四个字：渠道正常"}]}
    t0 = time.time()
    try:
        import urllib.error as _ue
        req = urllib.request.Request(
            url, data=json.dumps(payload).encode("utf-8"), method="POST",
            headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=25) as r:
                status = r.status
                raw = r.read().decode("utf-8", "ignore")
        except _ue.HTTPError as e:
            return {"ok": False, "status": e.code, "ms": int((time.time() - t0) * 1000),
                    "model": model, "channel": name or "（未保存）",
                    "msg": "HTTP %d：%s" % (e.code,
                                            (e.read()[:200].decode("utf-8", "ignore") or "").strip()[:200])}
        ms = int((time.time() - t0) * 1000)
        try:
            j = json.loads(raw)
            reply = (((j.get("choices") or [{}])[0].get("message") or {}).get("content") or "")
        except ValueError:
            return {"ok": False, "status": status, "ms": ms, "model": model,
                    "channel": name or "（未保存）", "msg": "返回不是 JSON：%s" % raw[:120]}
        return {"ok": True, "status": status, "ms": ms, "model": model,
                "channel": name or "（未保存）", "reply": str(reply)[:80],
                "msg": "通了：%d ms · %s" % (ms, str(reply)[:40])}
    except Exception as e:
        return {"ok": False, "ms": int((time.time() - t0) * 1000), "model": model,
                "channel": name or "（未保存）",
                "msg": "%s: %s" % (type(e).__name__, str(e)[:140])}


# ---- 她的 QQ 资料：昵称 / 签名 / 头像 / 说说 ----
# 数据都走 OneBot 的 HTTP API，本地只缓存一份头像图给控制台显示。
QQ_UIN = str(os.environ.get("FISH_QQ_UIN") or "").strip()
AVATAR_CACHE = os.path.join(BOT_DIR, "data", "avatar_cache.bin")
AVATAR_TTL = 3600          # 头像缓存 1 小时
SIGN_MAX = 100             # QQ 签名上限（太长会被服务端截断）
NICK_MAX = 24


def _qq_uin():
    """当前登录的 QQ 号；环境变量没配就现场问 OneBot。"""
    global QQ_UIN
    if QQ_UIN:
        return QQ_UIN
    try:
        r = ob_call("get_login_info", {}, timeout=6) or {}
        QQ_UIN = str((r.get("data") or {}).get("user_id") or "")
    except Exception:
        QQ_UIN = ""
    return QQ_UIN


def qq_avatar_url(size=640):
    uin = _qq_uin()
    if not uin:
        return ""
    # t 参数骗过 QQ 的缓存，头像换完能立刻看到新的
    return "https://q1.qlogo.cn/g?b=qq&nk=%s&s=%d&t=%d" % (uin, size, int(time.time()))


def _ob_ok(resp):
    """OneBot 返回 {status:ok/failed, retcode, data, wording}"""
    if not isinstance(resp, dict):
        return False, "返回值不是 JSON"
    if resp.get("status") == "ok" or resp.get("retcode") == 0:
        return True, ""
    return False, str(resp.get("wording") or resp.get("message") or resp)


def profile_get():
    """读昵称/签名/等级。OneBot 挂了也返回，界面上做提示。"""
    out = {"ok": True, "uin": _qq_uin(), "nickname": "", "signature": "",
           "level": 0, "avatar": "/api/profile/avatar", "avatar_url": "",
           "sign_max": SIGN_MAX, "nick_max": NICK_MAX}
    try:
        r = ob_call("get_stranger_info",
                    {"user_id": int(out["uin"] or 0)}, timeout=8) or {}
        ok, err = _ob_ok(r)
        d = r.get("data") or {}
        if ok and d:
            out["nickname"] = d.get("nickname") or ""
            out["signature"] = d.get("long_nick") or ""
            out["level"] = d.get("qq_level") or d.get("qqLevel") or 0
        else:
            out["warn"] = "读资料失败：%s" % err
    except Exception as e:
        out["warn"] = "连不上 OneBot：%s" % type(e).__name__
    out["avatar_url"] = qq_avatar_url()
    # 控制台要直接显示头像，顺手塞一份 base64（约 60KB，能接受）
    try:
        import base64 as _b64
        buf = profile_avatar_bytes()
        if buf:
            out["avatar_b64"] = _b64.b64encode(buf).decode("ascii")
    except Exception:
        pass
    return out


def profile_avatar_bytes(force=False):
    """拿头像字节。优先本地缓存，过期就去 qlogo 拉；拉不到就用旧的。"""
    try:
        if (not force and os.path.isfile(AVATAR_CACHE)
                and time.time() - os.path.getmtime(AVATAR_CACHE) < AVATAR_TTL):
            with open(AVATAR_CACHE, "rb") as f:
                data = f.read()
            if data:
                return data
    except OSError:
        pass
    url = qq_avatar_url()
    if not url:
        return b""
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = resp.read()
        if data:
            try:
                with open(AVATAR_CACHE, "wb") as f:
                    f.write(data)
            except OSError:
                pass
            return data
    except Exception:
        pass
    try:
        with open(AVATAR_CACHE, "rb") as f:
            return f.read()
    except OSError:
        return b""


def profile_save(body):
    """改昵称 / 签名。两个都传就都改。"""
    body = body or {}
    nickname = str(body.get("nickname") or "").strip()
    signature = str(body.get("signature") or "").strip()
    if not nickname and not signature:
        return {"ok": False, "msg": "昵称和签名都空着，没东西可改"}
    if len(nickname) > NICK_MAX:
        return {"ok": False, "msg": "昵称最多 %d 个字" % NICK_MAX}
    if len(signature) > SIGN_MAX:
        return {"ok": False, "msg": "签名最多 %d 个字" % SIGN_MAX}
    done, fails = [], []
    if nickname:
        try:
            ok, err = _ob_ok(ob_call("set_qq_profile",
                                     {"nickname": nickname}, timeout=20))
        except Exception as e:
            ok, err = False, "%s: %s" % (type(e).__name__, e)
        (done if ok else fails).append(("昵称", err))
    if signature:
        try:
            ok, err = _ob_ok(ob_call("set_self_longnick",
                                     {"longNick": signature}, timeout=20))
            if not ok:
                # 老协议端只认这个名字
                ok, err = _ob_ok(ob_call("set_long_nick",
                                         {"longNick": signature}, timeout=20))
        except Exception as e:
            ok, err = False, "%s: %s" % (type(e).__name__, e)
        (done if ok else fails).append(("签名", err))
    r = profile_get()
    if fails:
        r.update({"ok": False,
                  "msg": "改好了 %s，但 %s 失败：%s" % (
                      "、".join(x[0] for x in done) or "（无）",
                      "、".join(x[0] for x in fails),
                      fails[0][1][:60])})
    else:
        r["msg"] = "改好了：" + "、".join(x[0] for x in done)
    return r


def _img_ok(data):
    """粗查一下是不是图片，别把坏数据塞给 QQ。"""
    if not data or len(data) < 200:
        return False
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return True
    if data[:2] == b"\xff\xd8":
        return True
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return True
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return True
    return False


def profile_avatar_set(body):
    """上传新头像。前端传 data:image/...;base64,xxx 或纯 base64。"""
    import base64
    raw = str((body or {}).get("image") or "").strip()
    if not raw:
        return {"ok": False, "msg": "没收到图片"}
    if "," in raw and raw.lstrip().startswith("data:"):
        raw = raw.split(",", 1)[1]
    raw = "".join(raw.split())
    try:
        data = base64.b64decode(raw)
    except Exception:
        return {"ok": False, "msg": "图片数据坏了"}
    return _avatar_apply(data)


def profile_avatar_set_raw(data):
    """直接收原始字节（控制台走这条路，省掉一遍 base64）。"""
    if not data:
        return {"ok": False, "msg": "没收到图片"}
    return _avatar_apply(data)


def _avatar_apply(data):
    import base64
    if not _img_ok(data):
        return {"ok": False, "msg": "这不是一张有效图片"}
    if len(data) > 6 * 1024 * 1024:
        return {"ok": False, "msg": "图片太大了（上限 6MB）"}
    b64 = base64.b64encode(data).decode("ascii")
    try:
        ok, err = _ob_ok(ob_call("set_qq_avatar",
                                 {"file": "base64://" + b64}, timeout=40))
    except Exception as e:
        ok, err = False, "%s: %s" % (type(e).__name__, e)
    if not ok:
        return {"ok": False, "msg": "换头像失败：%s" % err[:80]}
    # 本地缓存换成新图，控制台立刻能看到
    try:
        with open(AVATAR_CACHE, "wb") as f:
            f.write(data)
    except OSError:
        pass
    r = profile_get()
    r["msg"] = "头像换好了"
    return r


def qzone_list(limit=20):
    """说说列表。OneBot 不支持就返回空 + 说明。"""
    out = {"ok": True, "items": [], "total": 0}
    try:
        r = ob_call("get_qzone_msg_list", {}, timeout=15) or {}
        ok, err = _ob_ok(r)
        if not ok:
            out.update({"ok": False, "msg": "读说说失败：%s" % err[:60]})
            return out
        d = r.get("data") or {}
        out["total"] = int(d.get("total") or 0)
        for m in (d.get("msglist") or [])[:limit]:
            imgs = m.get("images") or m.get("pics") or []
            if not isinstance(imgs, list):
                imgs = []
            out["items"].append({
                "tid": str(m.get("tid") or ""),
                "content": str(m.get("content") or ""),
                "time": int(m.get("time") or 0),
                "comments": int(m.get("comment_num") or 0),
                "private": bool(m.get("is_private")),
                "images": [str(u) for u in imgs if u],
            })
    except Exception as e:
        out.update({"ok": False, "msg": "连不上 OneBot：%s" % type(e).__name__})
    return out


QZONE_MAX_IMAGES = 9          # 说说最多 9 张图（QQ 空间上限）
QZONE_IMG_MAX = 8 * 1024 * 1024


def qzone_post(body):
    """发说说。content 可以空（只发图），images 传 data URL 或纯 base64 列表。"""
    import base64
    body = body or {}
    content = str(body.get("content") or "").strip()
    raw_imgs = body.get("images") or []
    if not isinstance(raw_imgs, list):
        raw_imgs = []
    if not content and not raw_imgs:
        return {"ok": False, "msg": "内容和图片都空着"}
    if len(content) > 1500:
        return {"ok": False, "msg": "太长了（上限 1500 字）"}
    if len(raw_imgs) > QZONE_MAX_IMAGES:
        return {"ok": False, "msg": "最多 %d 张图" % QZONE_MAX_IMAGES}
    imgs = []
    for i, it in enumerate(raw_imgs):
        v = str(it or "").strip()
        if not v:
            continue
        if v.startswith("base64://"):
            v = v[len("base64://"):]
        elif v.startswith("http://") or v.startswith("https://"):
            imgs.append(v)
            continue
        if "," in v and v.lstrip().startswith("data:"):
            v = v.split(",", 1)[1]
        v = "".join(v.split())
        try:
            data = base64.b64decode(v)
        except Exception:
            return {"ok": False, "msg": "第 %d 张图数据坏了" % (i + 1)}
        if not _img_ok(data):
            return {"ok": False, "msg": "第 %d 张不是有效图片" % (i + 1)}
        if len(data) > QZONE_IMG_MAX:
            return {"ok": False, "msg": "第 %d 张太大（上限 8MB）" % (i + 1)}
        imgs.append("base64://" + base64.b64encode(data).decode("ascii"))
    params = {"content": content}
    if imgs:
        params["images"] = imgs          # 参数名就是 images，实测 image/pics 都被忽略
    try:
        r = ob_call("send_qzone_msg", params, timeout=60) or {}
        ok, err = _ob_ok(r)
    except Exception as e:
        ok, err = False, "%s: %s" % (type(e).__name__, e)
    print("qzone_post 文字%d字 图%d张 -> %s %s"
          % (len(content), len(imgs), "ok" if ok else "失败", "" if ok else err[:80]), flush=True)
    if not ok:
        return {"ok": False, "msg": "发说说失败：%s" % err[:80]}
    out = qzone_list()
    out["msg"] = ("发出去了（%d 张图）" % len(imgs)) if imgs else "发出去了"
    return out


def qzone_del(body):
    tid = str((body or {}).get("tid") or "").strip()
    if not tid:
        return {"ok": False, "msg": "没说要删哪条"}
    try:
        r = ob_call("delete_qzone_msg", {"tid": tid}, timeout=20) or {}
        ok, err = _ob_ok(r)
    except Exception as e:
        ok, err = False, "%s: %s" % (type(e).__name__, e)
    # 有日志才查得动：控制台说"删除失败"时，到 journalctl -u fishweb 看这一行
    print("qzone_del tid=%s -> %s %s"
          % (tid, "ok" if ok else "失败", "" if ok else err[:120]), flush=True)
    if not ok:
        return {"ok": False, "tid": tid, "msg": "删说说失败：%s" % err[:80]}
    out = qzone_list()
    out["msg"] = "删掉了"
    return out


# ---- 梗库（纯文字；一个梗可挂多个名字，分号隔开）----
# ---- 梗库表情包（和插件共用 data/meme_stickers）----
MEME_STICKER_DIR = os.path.join(BOT_DIR, "data", "meme_stickers")


def _meme_sticker_path(item):
    nm = str((item or {}).get("sticker") or "").strip()
    if not nm:
        return ""
    p = os.path.join(MEME_STICKER_DIR, os.path.basename(nm))
    return p if os.path.isfile(p) else ""


def _meme_sticker_save(item_id, raw):
    if not raw:
        return False, "没收到图片"
    base = re.sub(r"[^A-Za-z0-9_-]", "", str(item_id or ""))[:40]
    if not base:
        return False, "id 不对"
    try:
        os.makedirs(MEME_STICKER_DIR, exist_ok=True)
        p = os.path.join(MEME_STICKER_DIR, base + ".jpg")
        try:
            from PIL import Image
            import io as _io
            im = Image.open(_io.BytesIO(raw))
            if im.mode not in ("RGB", "L"):
                im = im.convert("RGB")
            im.thumbnail((320, 320), Image.LANCZOS)
            im.save(p, "JPEG", quality=88)
        except Exception:
            with open(p, "wb") as f:
                f.write(raw)
        try:
            import pwd
            u = pwd.getpwnam("ubuntu")
            os.chown(p, u.pw_uid, u.pw_gid)
        except (KeyError, OSError):
            pass
        return True, base + ".jpg"
    except OSError as e:
        return False, "存不下（%r）" % (e,)


MEME_FILE = os.path.join(BOT_DIR, "data", "memes.json")
MEME_MAX = 500


def _meme_load():
    try:
        with open(MEME_FILE, "r", encoding="utf-8-sig") as f:
            raw = json.load(f) or {}
    except (OSError, ValueError):
        raw = {}
    items = raw.get("items") if isinstance(raw, dict) else raw
    return [it for it in (items or []) if isinstance(it, dict)]


def _meme_save(items):
    try:
        os.makedirs(os.path.dirname(MEME_FILE), exist_ok=True)
        tmp = MEME_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"items": items[-MEME_MAX:]}, f, ensure_ascii=False, indent=1)
        os.replace(tmp, MEME_FILE)
        try:
            import pwd
            u = pwd.getpwnam("ubuntu")
            os.chown(MEME_FILE, u.pw_uid, u.pw_gid)   # 插件以 ubuntu 跑，必须能读
        except (KeyError, OSError):
            pass
        return True
    except OSError as e:
        print("梗库保存失败: %r" % (e,), flush=True)
        return False


def _meme_names(s):
    out = []
    for part in re.split(r"[;；,，\n]+", str(s or "")):
        nm = part.strip()[:24]
        if nm and nm not in out:
            out.append(nm)
    return out


def memes_get():
    items = _meme_load()
    items.sort(key=lambda x: (int(x.get("hits") or 0), str(x.get("at") or "")), reverse=True)
    return {"ok": True, "count": len(items),
            "items": [{"id": str(it.get("id") or ""),
                       "names": [str(x) for x in (it.get("names") or [])],
                       "desc": str(it.get("desc") or ""),
                       "jie": str(it.get("jie") or ""),
                       "has_sticker": bool(_meme_sticker_path(it)),
                       "hits": int(it.get("hits") or 0),
                       "at": str(it.get("at") or "")} for it in items]}


def memes_add(body):
    names = _meme_names((body or {}).get("names") or (body or {}).get("name"))
    desc = str((body or {}).get("desc") or "").strip()[:400]
    jie = str((body or {}).get("jie") or "").strip()[:120]
    if not names:
        return {"ok": False, "msg": "至少给一个梗名字（多个用分号隔开）"}
    if not desc:
        return {"ok": False, "msg": "还要一句介绍"}
    raw = None
    _b64 = str((body or {}).get("image_b64") or "").strip()
    if _b64:
        try:
            import base64 as _b
            raw = _b.b64decode(_b64)
        except Exception:
            raw = None
    items = _meme_load()
    for it in items:
        if set(names) & set(str(x) for x in (it.get("names") or [])):
            it["names"] = list(dict.fromkeys([str(x) for x in (it.get("names") or [])] + names))[:10]
            it["desc"] = desc
            if jie:
                it["jie"] = jie
            if raw:
                _ok, _nm = _meme_sticker_save(str(it.get("id") or ""), raw)
                if _ok:
                    it["sticker"] = _nm
            if not _meme_save(items):
                return {"ok": False, "msg": "保存失败"}
            return {"ok": True, "msg": "已更新「%s」" % "、".join(names), **memes_get()}
    if len(items) >= MEME_MAX:
        return {"ok": False, "msg": "梗库满了（%d 条）" % MEME_MAX}
    _pid = "m%d" % int(time.time() * 1000)
    _snm = ""
    if raw:
        _ok, _snm = _meme_sticker_save(_pid, raw)
        if not _ok:
            return {"ok": False, "msg": _snm}
    items.append({"id": _pid, "names": names[:10],
                  "desc": desc, "jie": jie, "sticker": _snm, "hits": 0,
                  "at": time.strftime("%Y-%m-%d %H:%M")})
    if not _meme_save(items):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "已加入「%s」" % "、".join(names), **memes_get()}


def memes_edit(body):
    pid = str((body or {}).get("id") or "").strip()
    key = str((body or {}).get("key") or "").strip()
    items = _meme_load()
    hit = None
    for it in items:
        if (pid and str(it.get("id") or "") == pid) or \
           (key and (key == str(it.get("id") or "") or key in [str(x) for x in (it.get("names") or [])])):
            hit = it
            break
    if hit is None:
        return {"ok": False, "msg": "梗库里没有这一条"}
    desc = str((body or {}).get("desc") or "").strip()
    if desc:
        hit["desc"] = desc[:400]
    jie2 = str((body or {}).get("jie") or "").strip()
    if jie2:
        hit["jie"] = jie2[:120]
    if str((body or {}).get("del_sticker") or "") in ("1", "true", "True"):
        _p = _meme_sticker_path(hit)
        if _p:
            try:
                os.remove(_p)
            except OSError:
                pass
        hit["sticker"] = ""
    _b64 = str((body or {}).get("image_b64") or "").strip()
    if _b64:
        try:
            import base64 as _b
            _ok, _nm = _meme_sticker_save(str(hit.get("id") or ""), _b.b64decode(_b64))
            if _ok:
                hit["sticker"] = _nm
        except Exception:
            pass
    names = _meme_names((body or {}).get("names"))
    if names:
        hit["names"] = names[:10]
    if not _meme_save(items):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "已更新", **memes_get()}


def memes_del(body):
    pid = str((body or {}).get("id") or "").strip()
    key = str((body or {}).get("key") or "").strip()
    items = _meme_load()
    keep = [it for it in items
            if not ((pid and str(it.get("id") or "") == pid)
                    or (key and (key == str(it.get("id") or "")
                                 or key in [str(x) for x in (it.get("names") or [])])))]
    if len(keep) == len(items):
        return {"ok": False, "msg": "梗库里没有这一条"}
    if not _meme_save(keep):
        return {"ok": False, "msg": "保存失败"}
    return {"ok": True, "msg": "已删除 %d 条" % (len(items) - len(keep)), **memes_get()}


# ---------------- 表情包库（控制台发表情用） ----------------
# 用的就是她自己的那一套：/opt/qqbot/stickers + data/sticker_index.json（`情绪|描述`）。
# 发出去的时候走 base64 + sub_type=1，QQ 里显示成「表情包」而不是原图 —— 和插件发图完全一样。
STICKER_DIR = os.environ.get("FISH_STICKER_DIR", os.path.join(BOT_DIR, "stickers"))
STICKER_INDEX = os.path.join(BOT_DIR, "data", "sticker_index.json")
STICKER_THUMB_DIR = os.path.join(BOT_DIR, "data", "sticker_thumbs")
STICKER_MAX_KB = 3000                 # 和插件 FISH_STICKER_MAX_KB 一致，太大的图不发
STICKER_EXTS = (".gif", ".png", ".jpg", ".jpeg")
STICKER_THUMB_SIZE = 96               # 缩略图边长（列表里显示 64dp，1.5x 屏够用）
STICKER_THUMB_LIMIT = 80              # 一次最多取多少张缩略图（别把回包撑太大）

_STK = {"ts": 0.0, "items": [], "by": {}, "sig": "", "moods": []}
_STK_LOCK = threading.Lock()


def sticker_index_map():
    """插件写的索引：{文件名: "情绪|描述"}。坏了就当空，按文件名照样能发。"""
    try:
        with open(STICKER_INDEX, "r", encoding="utf-8-sig") as f:
            raw = json.load(f) or {}
        return {str(k): str(v) for k, v in raw.items() if v}
    except (OSError, ValueError):
        return {}


def stickers_list(force=False):
    """扫一遍表情包目录（20 秒缓存）。情绪/描述来自 sticker_index.json。"""
    now = time.time()
    if not force and _STK["items"] and now - _STK["ts"] < 20:
        return {"ok": True, "count": len(_STK["items"]), "sig": _STK["sig"],
                "moods": _STK["moods"], "items": _STK["items"]}
    idx = sticker_index_map()
    items = []
    try:
        names = os.listdir(STICKER_DIR)
    except OSError as e:
        print("表情包目录读不到（%s）：%r" % (STICKER_DIR, e), flush=True)
        names = []
    for name in names:
        if not name.lower().endswith(STICKER_EXTS):
            continue
        p = os.path.join(STICKER_DIR, name)
        try:
            sz = os.path.getsize(p)
        except OSError:
            continue
        if sz <= 0 or sz > STICKER_MAX_KB * 1024:
            continue
        _parts = [x.strip() for x in (idx.get(name) or "").split("|")]
        items.append({"id": name,
                      "mood": (_parts[0] if _parts and _parts[0] else "其它"),
                      "desc": (_parts[1] if len(_parts) > 1 else ""),
                      # 2026-10-30：角色 / 场合 / **自定义标签**（第 5 段，逗号分隔）
                      "role": (_parts[2] if len(_parts) > 2 else ""),
                      "scene": (_parts[3] if len(_parts) > 3 else ""),
                      "tags": (_parts[4] if len(_parts) > 4 else ""),
                      "kb": int(sz / 1024)})
    items.sort(key=lambda x: (x["mood"], x["id"]))

    counts = {}
    for it in items:
        counts[it["mood"]] = counts.get(it["mood"], 0) + 1
    moods = [{"mood": k, "n": v} for k, v in sorted(counts.items(), key=lambda x: -x[1])]
    sig = hashlib.md5(("|".join("%s:%d" % (i["id"], i["kb"]) for i in items)).encode()).hexdigest()[:12]

    _STK.update({"ts": now, "items": items, "sig": sig, "moods": moods,
                 "by": {it["id"]: it for it in items}})
    return {"ok": True, "count": len(items), "sig": sig, "moods": moods, "items": items}


def _sticker_path(name):
    """名字 → 本地绝对路径（只认 stickers 目录里的普通文件名，防目录穿越）。"""
    nm = os.path.basename(str(name or "").strip())
    if not nm or len(nm) > 80 or not re.fullmatch(r"[A-Za-z0-9_.\-]+", nm):
        return ""
    if not nm.lower().endswith(STICKER_EXTS):
        return ""
    p = os.path.join(STICKER_DIR, nm)
    return p if os.path.isfile(p) else ""


def sticker_tags_save(body):
    """改一张表情包的情绪 / 画面 / 角色 / 场合 / **自定义标签**（第 5 段，逗号分隔）。

    2026-10-30 用户要求：所有表情包都能自己打标签，她自己按标签判断什么时候发哪张。
    只认 stickers 目录里真实存在的文件名；`|` 是分隔符，字段里的竖线会被换掉。
    """
    name = os.path.basename(str((body or {}).get("id") or "").strip())
    if not _sticker_path(name):
        return {"ok": False, "msg": "没有这张图"}
    idx = sticker_index_map()
    parts = (str(idx.get(name) or "").split("|") + ["", "", "", "", ""])[:5]
    for i, key in enumerate(("mood", "desc", "role", "scene", "tags")):
        if key in (body or {}):
            v = str(body.get(key) or "").strip().replace("|", "／")
            parts[i] = v[:120]
    line = "|".join(parts).rstrip("|")
    idx[name] = line
    try:
        os.makedirs(os.path.dirname(STICKER_INDEX) or ".", exist_ok=True)
        tmp = STICKER_INDEX + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(idx, f, ensure_ascii=False, indent=1, sort_keys=True)
        os.replace(tmp, STICKER_INDEX)
    except OSError as e:
        return {"ok": False, "msg": "写入失败：%r" % (e,)}
    _STK["ts"] = 0.0                      # 让列表缓存失效
    return {"ok": True, "id": name, "raw": line, "tags": parts[4]}


# ---------------- 群聊黑话（2026-10-30） ----------------
# 每个群一份小词典：{"<群号>": {"词": "说明", ...}}。
# 插件读它拼进上下文，她就能听懂群里那些外人不懂的说法，聊天时也能用上。
SLANG_FILE = os.path.join(BOT_DIR, "data", "slang.json")
SLANG_GLOBAL_KEY = "*"     # 全局黑话（所有群都认）存在这个键下
_SLANG_LOCK = threading.Lock()


def slang_all():
    try:
        with open(SLANG_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f) or {}
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def slang_get(gid):
    m = (slang_all().get(str(gid)) or {})
    if not isinstance(m, dict):
        m = {}
    return {"ok": True, "gid": str(gid), "count": len(m), "items": m}


def slang_all_api():
    """整库：{"ok": true, "count": 全部条数, "groups": {群号: {词: 说明}}}。

    控制台的「群聊黑话」页用它一次拿全，左边选群、右边增删改查（2026-11-01）。
    """
    data = slang_all()
    clean = {}
    total = 0
    for gid, items in data.items():
        if not isinstance(items, dict):
            continue
        keep = {str(k): str(v) for k, v in items.items() if str(k).strip() and str(v).strip()}
        if keep:
            clean[str(gid)] = keep
            total += len(keep)
    return {"ok": True, "count": total, "groups": clean}


def slang_save(body):
    """整组覆盖保存：{"gid": "123", "items": {"词": "说明"}}；items 传空 = 清掉这个群。

    gid 传 `*`（或 `global` / `全局`）表示**全局黑话**：所有群都生效（2026-11-01 加）。
    """
    gid = str((body or {}).get("gid") or "").strip()
    items = (body or {}).get("items")
    if gid.lower() in ("*", "global", "全局"):
        gid = SLANG_GLOBAL_KEY
    elif not gid.isdigit() or len(gid) > 20:
        return {"ok": False, "msg": "群号不对（只填数字；全局请用 *）"}
    if not isinstance(items, dict):
        return {"ok": False, "msg": "items 要是 {词: 说明}"}
    clean = {}
    for k, v in items.items():
        k = str(k).strip().replace("|", "／")[:20]
        v = str(v).strip().replace("\n", " ").replace("|", "／")[:80]
        if k and v:
            clean[k] = v
        if len(clean) >= 200:
            break
    with _SLANG_LOCK:
        d = slang_all()
        if clean:
            d[gid] = clean
        else:
            d.pop(gid, None)
        try:
            os.makedirs(os.path.dirname(SLANG_FILE) or ".", exist_ok=True)
            tmp = SLANG_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(d, f, ensure_ascii=False, indent=1, sort_keys=True)
            os.replace(tmp, SLANG_FILE)
        except OSError as e:
            return {"ok": False, "msg": "写入失败：%r" % (e,)}
    return {"ok": True, "gid": gid, "count": len(clean)}


# 缩略图一次批量做完：287 张一张一个子进程太慢，这里把待办列表喂给 venv 里的 python 一把刷完
_THUMB_SNIPPET = (
    "import sys,json\n"
    "from PIL import Image\n"
    "jobs=json.loads(sys.stdin.read())\n"
    "n=0\n"
    "for src,dst in jobs:\n"
    "    try:\n"
    "        im=Image.open(src).convert('RGB')\n"
    "        im.thumbnail(({size},{size}),Image.LANCZOS)\n"
    "        im.save(dst,format='JPEG',quality=82)\n"
    "        n+=1\n"
    "    except Exception as e:\n"
    "        sys.stderr.write('thumb fail %s: %r\\n'%(src,e))\n"
    "print(n)\n"
)


def _sticker_thumbs_make(pairs):
    """pairs: [[src, dst], ...] → 用 venv 里的 Pillow 生成 JPEG 缩略图。返回成功数。"""
    if not pairs:
        return 0
    try:
        os.makedirs(STICKER_THUMB_DIR, exist_ok=True)
        r = subprocess.run([_VENV_PY, "-c", _THUMB_SNIPPET.format(size=STICKER_THUMB_SIZE)],
                           input=json.dumps(pairs).encode(), capture_output=True, timeout=180)
        out = (r.stdout or b"").decode(errors="replace").strip()
        if (r.returncode != 0 or not out.isdigit()) and (r.stderr or b""):
            print("表情包缩略图生成有问题：%s" % (r.stderr or b"").decode(errors="replace")[:300], flush=True)
        try:
            import pwd
            u = pwd.getpwnam("ubuntu")
            os.chown(STICKER_THUMB_DIR, u.pw_uid, u.pw_gid)
            for _s, d in pairs:
                if os.path.exists(d):
                    os.chown(d, u.pw_uid, u.pw_gid)
        except (KeyError, OSError):
            pass
        return int(out) if out.isdigit() else 0
    except Exception as e:
        print("表情包缩略图失败：%r" % (e,), flush=True)
        return 0


def _sticker_thumb_file(name):
    """缩略图落盘路径。用「原名+.jpg」避免 st001.png / st001.jpg 撞名。"""
    return os.path.join(STICKER_THUMB_DIR, name + ".jpg")


def sticker_thumbs(names):
    """按名字批量取缩略图（缺的先一次性生成）。返回 {ok, items:[{id, b64}], missing:[...]}。"""
    want = []
    for x in (names or []):
        nm = os.path.basename(str(x or "").strip())
        if nm and nm not in want:
            want.append(nm)
    want = want[:STICKER_THUMB_LIMIT]

    srcs = [(nm, _sticker_path(nm)) for nm in want]
    srcs = [(nm, p) for nm, p in srcs if p]
    with _STK_LOCK:
        pairs = []
        for nm, p in srcs:
            dst = _sticker_thumb_file(nm)
            try:
                if os.path.isfile(dst) and os.path.getsize(dst) > 0 and \
                        os.path.getmtime(dst) >= os.path.getmtime(p):
                    continue
            except OSError:
                pass
            pairs.append([p, dst])
        if pairs:
            _sticker_thumbs_make(pairs)

    items, missing = [], []
    for nm, p in srcs:
        dst = _sticker_thumb_file(nm)
        try:
            with open(dst, "rb") as f:
                raw = f.read()
            if raw:
                items.append({"id": nm, "b64": base64.b64encode(raw).decode()})
                continue
        except OSError:
            pass
        missing.append(nm)
    return {"ok": True, "count": len(items), "items": items, "missing": missing}


def sticker_send(chat, ids, quote=None, at=None, text="", dry=False):
    """以她的身份发表情包（一张或几张）。

    ids:  文件名 / 文件名列表（就是 stickers 目录里的文件名）
    quote/at: 和发文字一样能带引用、@ 人
    dry:  True 只回要发的消息段，不真发
    """
    if isinstance(ids, str):
        ids = [ids]
    names = []
    for x in (ids or []):
        nm = os.path.basename(str(x or "").strip())
        if nm and nm not in names and _sticker_path(nm):
            names.append(nm)
    if not names:
        return {"ok": False, "msg": "没找到这张表情包（刷新一下列表）"}
    names = names[:3]

    m = re.match(r"^(群|私聊)\s*(\d+)$", (chat or "").strip())
    if not m:
        return {"ok": False, "msg": "会话格式不对：%s" % chat}
    kind, ident = m.group(1), int(m.group(2))
    text = (text or "").strip()

    segs = []
    reply_id = None
    if isinstance(quote, dict) and (quote.get("text") or quote.get("who")):
        reply_id = find_reply_id(chat, quote)
        if reply_id is not None:
            segs.append({"type": "reply", "data": {"id": str(reply_id)}})
    ats = _norm_ats(at) if kind == "群" else []
    for a in ats:
        segs.append({"type": "at", "data": {"qq": a["uid"]}})
        segs.append({"type": "text", "data": {"text": " "}})
    if text:
        segs.append({"type": "text", "data": {"text": text}})

    total = 0
    for nm in names:
        p = _sticker_path(nm)
        try:
            with open(p, "rb") as f:
                raw = f.read()
        except OSError as e:
            return {"ok": False, "msg": "读表情包失败：%r" % (e,)}
        total += len(raw)
        # sub_type=1 → QQ 里是「表情包」形态（和插件 sticker_segment 一致）
        segs.append({"type": "image",
                     "data": {"file": "base64://" + base64.b64encode(raw).decode(),
                              "sub_type": 1}})

    if dry:
        slim = []
        for s in segs:
            if s["type"] == "image":
                slim.append({"type": "image",
                             "data": {"sub_type": 1,
                                      "base64_len": len(s["data"]["file"])}})
            else:
                slim.append(s)
        return {"ok": True, "msg": "预览（没真发）", "dry": True,
                "segments": slim, "names": names, "bytes": total,
                "reply_matched": reply_id is not None,
                "at": [a["uid"] for a in ats]}

    payload = {"message": segs}
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
    if res.get("status") != "ok":
        return {"ok": False, "msg": res.get("wording") or res.get("message") or "发送失败"}

    # 日志里记成 `【表情】stk://文件名`：聊天页能直接画成图（2026-10-28）
    # 以前记的是「【表情包】情绪｜描述」，那种没有文件名，只能显示成 [表情] 占位符。
    for nm in names:
        log_sent(chat, "【表情】stk://" + nm, ats=ats if nm == names[0] else None,
                 quote=quote if (nm == names[0] and isinstance(quote, dict)
                                 and (quote.get("text") or quote.get("who"))) else None)
    _MSG_CACHE["ts"] = 0        # 让聊天页立刻能刷出来
    return {"ok": True, "msg": "已发出 %d 张表情包" % len(names),
            "message_id": (res.get("data") or {}).get("message_id"),
            "names": names, "bytes": total, "reply_matched": reply_id is not None}


# ---------------- 角色人格（多套人设隔离） ----------------
# 一个「人格」= personas/<id>/persona_prompt.txt（人设正文）+ wordbank.json（它自己的文案）。
# 插件每条消息都会看 mtime，切了立刻生效（不用重启）；**没写的文案落到中立兜底，不借别的角色**。
PERSONA_ROOT = os.path.join(BOT_DIR, "personas")
PERSONA_REGISTRY = os.path.join(BOT_DIR, "data", "personas.json")
PERSONA_DEFAULT = "feiyu"
# 和插件里的 _PERSONA_NEUTRAL 一一对应（少一个 = 那个角色会被报"缺"）
PERSONA_KEYS = [
    "CALL_NAMES", "REPLY_BOOK", "ADDONS", "STICKER_ASK_REPLIES",
    "LINK_FALLBACK", "LINK_EMPTY", "MEME_JIE_POOL",
    "AFF_HOW_LINES", "AFF_QUERY_LINES",
    "FEED_OK_LINES", "FEED_FULL_LINES", "FEED_STUFFED_LINES", "FEED_WAIT_LINES",
    "SHUT_REPLIES", "AFF_LOW_LINES", "AFF_HIGH_LINES", "TRAIT_FILLER_LOW",
    "IMAGE_FALLBACK_REPLIES", "AI_BUSY_LINES", "VISION_BUSY_REPLIES",
    "POKE_REPLIES", "REACTION_REPLIES", "POKE_ANGRY_REPLIES",
    "QQCTL_NAME_POOL", "QQCTL_SIGN_POOL", "BURST_REPLIES",
    "SHRINK_SYSTEM", "VISION_EXTRA", "FAREWELL_TEXT",
    "WEB_TITLE", "WEB_SITES",
]


def _persona_id_ok(pid):
    return bool(re.fullmatch(r"[A-Za-z0-9_\-]{1,32}", str(pid or "")))


def _chown_ubuntu(path):
    try:
        import pwd
        u = pwd.getpwnam("ubuntu")
        os.chown(path, u.pw_uid, u.pw_gid)
    except (KeyError, OSError, ImportError):
        pass


def persona_registry_load():
    try:
        with open(PERSONA_REGISTRY, "r", encoding="utf-8-sig") as f:
            d = json.load(f) or {}
    except (OSError, ValueError):
        d = {}
    if not isinstance(d, dict):
        d = {}
    items = [it for it in (d.get("items") or []) if isinstance(it, dict) and it.get("id")]
    if not items:
        items = [{"id": PERSONA_DEFAULT, "name": "肥鱼",
                  "desc": "默认人格（自带那套文案）",
                  "at": time.strftime("%Y-%m-%d %H:%M")}]
    active = str(d.get("active") or PERSONA_DEFAULT)
    if active not in [it["id"] for it in items]:
        active = items[0]["id"]
    return {"active": active, "items": items}


def persona_registry_save(reg):
    try:
        os.makedirs(os.path.dirname(PERSONA_REGISTRY), exist_ok=True)
        tmp = PERSONA_REGISTRY + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(reg, f, ensure_ascii=False, indent=1)
        os.replace(tmp, PERSONA_REGISTRY)
        _chown_ubuntu(PERSONA_REGISTRY)
        try:
            os.chmod(PERSONA_REGISTRY, 0o644)
        except OSError:
            pass
        return True
    except OSError as e:
        print("人格注册表保存失败: %r" % (e,), flush=True)
        return False


def _persona_dir(pid):
    return os.path.join(PERSONA_ROOT, str(pid or PERSONA_DEFAULT))


def persona_prompt_of(pid):
    try:
        with open(os.path.join(_persona_dir(pid), "persona_prompt.txt"), "r", encoding="utf-8-sig") as f:
            return f.read()
    except OSError:
        return ""


def persona_wordbank_of(pid):
    try:
        with open(os.path.join(_persona_dir(pid), "wordbank.json"), "r", encoding="utf-8-sig") as f:
            d = json.load(f) or {}
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def personas_list():
    reg = persona_registry_load()
    out = []
    for it in reg["items"]:
        pid = str(it["id"])
        wb = persona_wordbank_of(pid)
        missing = [k for k in PERSONA_KEYS if wb.get(k) in (None, "", [], {})]
        prompt = persona_prompt_of(pid)
        out.append({
            "id": pid,
            "name": str(it.get("name") or pid),
            "desc": str(it.get("desc") or ""),
            "at": str(it.get("at") or ""),
            "active": pid == reg["active"],
            "prompt_len": len(prompt.strip()),
            "sticker_dir": str(it.get("sticker_dir") or ""),
            "voice": bool(it.get("voice", True)),
            "coins": bool(it.get("coins", False)),
            "voice_uri": str(it.get("voice_uri") or ""),
            "voice_bailian": str(it.get("voice_bailian") or ""),
            "voice_bailian_model": str(it.get("voice_bailian_model") or ""),
            "voice_name": str(it.get("voice_name") or ""),
            "voice_effective": persona_voice_of(it)[0],
            "voice_effective_model": persona_voice_of(it)[1],
            "voice_source": persona_voice_of(it)[2],
            # ⚠️ `aff_cap` 可能是字符串 "inf"（不封顶，控制台写过这种值）→ 直接 int() 会
            # `ValueError: invalid literal for int() with base 10: 'inf'`，整个
            # `/api/personas` 500，控制台的「角色人格」页就空白（2026-11-03 21:00 撞到过）。
            "aff_cap": (AFF_CAP_INF_V if _aff_unlimited_of(it)
                        else _aff_int(it.get("aff_cap"), 0)),
            "aff_unlimited": _aff_unlimited_of(it),
            "aff_no_decay": bool(it.get("aff_no_decay", False)),
            "aff_card": str(it.get("aff_card") or ""),
            "menu_card": str(it.get("menu_card") or ""),
            "aff_card_stickers": list(it.get("aff_card_stickers") or []),
            "site": str(it.get("site") or ""),
            "missing": missing,
            "have": len(PERSONA_KEYS) - len(missing),
            "total": len(PERSONA_KEYS),
        })
    return {"ok": True, "active": reg["active"], "items": out,
            "keys": PERSONA_KEYS, "dir": PERSONA_ROOT}


def personas_use(body):
    pid = str((body or {}).get("id") or "").strip()
    reg = persona_registry_load()
    if pid not in [str(it["id"]) for it in reg["items"]]:
        return {"ok": False, "msg": "没有这个人格：%s" % pid}
    reg["active"] = pid
    if not persona_registry_save(reg):
        return {"ok": False, "msg": "保存失败"}
    print("persona: -> %s" % pid, flush=True)
    return {"ok": True, "msg": "已切到 %s（立刻生效）" % pid, **personas_list()}


def personas_new(body):
    """新建人格：默认拿 _template 复制一份（人设为空，文案为空 → 落到中立兜底）。"""
    pid = str((body or {}).get("id") or "").strip()
    name = str((body or {}).get("name") or "").strip() or pid
    desc = str((body or {}).get("desc") or "").strip()
    copy_from = str((body or {}).get("from") or "").strip()
    if not _persona_id_ok(pid):
        return {"ok": False, "msg": "id 只能用字母/数字/下划线/短横线（1~32 位）"}
    reg = persona_registry_load()
    if pid in [str(it["id"]) for it in reg["items"]]:
        return {"ok": False, "msg": "已经有人格叫 %s 了" % pid}
    d = _persona_dir(pid)
    try:
        os.makedirs(d, exist_ok=True)
        src = _persona_dir(copy_from) if copy_from else os.path.join(PERSONA_ROOT, "_template")
        for fn in ("wordbank.json", "persona_prompt.txt"):
            sp = os.path.join(src, fn)
            dp = os.path.join(d, fn)
            if os.path.isfile(sp) and not os.path.isfile(dp):
                with open(sp, "r", encoding="utf-8", errors="replace") as f:
                    data = f.read()
                with open(dp, "w", encoding="utf-8") as f:
                    f.write(data)
            elif not os.path.isfile(dp):
                with open(dp, "w", encoding="utf-8") as f:
                    f.write("{}\n" if fn.endswith(".json") else "")
            _chown_ubuntu(dp)
    except OSError as e:
        return {"ok": False, "msg": "建目录失败：%r" % (e,)}
    _chown_ubuntu(d)
    reg["items"].append({"id": pid, "name": name, "desc": desc,
                         "at": time.strftime("%Y-%m-%d %H:%M")})
    if not persona_registry_save(reg):
        return {"ok": False, "msg": "注册表保存失败"}
    return {"ok": True, "msg": "已新建人格 %s（文案待填）" % pid, **personas_list()}


BINDINGS_FILE = os.path.join(BOT_DIR, "data", "persona_bindings.json")


def _bindings_read():
    try:
        with open(BINDINGS_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        return {str(k): str(v) for k, v in d.items()} if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def persona_bindings_api(body):
    """按会话绑人格：{"group:123":"taffy"}。GET 不带参数=读，POST 带 chat/persona=写。"""
    data = _bindings_read()
    chat = str((body or {}).get("chat") or "").strip()
    pid = str((body or {}).get("persona") or "").strip()
    if not chat:
        return {"ok": True, "bindings": data}
    if not pid or pid in ("默认", "default", "reset"):
        data.pop(chat, None)
        verb = "已清除"
    else:
        data[chat] = pid
        verb = "已绑定"
    try:
        tmp = BINDINGS_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
        os.replace(tmp, BINDINGS_FILE)
        subprocess.run(["chown", "ubuntu:ubuntu", BINDINGS_FILE], timeout=10)
    except Exception as e:
        return {"ok": False, "msg": "保存失败：%r" % (e,)}
    print("bindings: %s %s -> %s" % (verb, chat, pid or "默认"), flush=True)
    return {"ok": True, "msg": "%s %s" % (verb, chat), "bindings": data}


def personas_edit(body):
    """改人格的显示名/描述，以及**这个角色自己的设置**：表情目录、有没有语音、站点。"""
    pid = str((body or {}).get("id") or "").strip()
    reg = persona_registry_load()
    hit = None
    for it in reg["items"]:
        if str(it.get("id")) == pid:
            hit = it
            break
    if hit is None:
        return {"ok": False, "msg": "没有这个人格：%s" % pid}
    for key in ("name", "desc"):
        if key in body and body.get(key) is not None:
            hit[key] = str(body.get(key) or "").strip()
    if "sticker_dir" in body:
        v = str(body.get("sticker_dir") or "").strip()
        if v:
            hit["sticker_dir"] = v
        else:
            hit.pop("sticker_dir", None)
    if "voice" in body:
        hit["voice"] = bool(body.get("voice"))
    if "coins" in body:
        hit["coins"] = bool(body.get("coins"))
    if "aff_cap" in body:
        raw = body.get("aff_cap")
        if _aff_unlimited_of({"aff_cap": raw}):
            hit["aff_cap"] = -1          # 不封顶（控制台发 "inf" 也认，统一落成 -1）
        else:
            v = _aff_int(raw, 0)
            if v != 0:
                hit["aff_cap"] = v          # -1 = 上不封顶（塔菲）
            else:
                hit.pop("aff_cap", None)
    if "aff_no_decay" in body:
        hit["aff_no_decay"] = bool(body.get("aff_no_decay"))
    if "menu_card" in body:
        v = str(body.get("menu_card") or "").strip()
        if v:
            hit["menu_card"] = v
        else:
            hit.pop("menu_card", None)
    if "aff_card" in body:
        v = str(body.get("aff_card") or "").strip()
        if v:
            hit["aff_card"] = v
        else:
            hit.pop("aff_card", None)
    if "aff_card_stickers" in body:
        v = body.get("aff_card_stickers")
        if isinstance(v, list) and v:
            hit["aff_card_stickers"] = [str(x) for x in v if str(x).strip()]
        else:
            hit.pop("aff_card_stickers", None)
    if "voice_uri" in body:
        v = str(body.get("voice_uri") or "").strip()
        if v:
            hit["voice_uri"] = v
        else:
            hit.pop("voice_uri", None)
    # 每个人格一套音色（2026-10-30）：百炼音色 / 模型 / 显示名
    for key in ("voice_bailian", "voice_bailian_model", "voice_name"):
        if key in body:
            v = str(body.get(key) or "").strip()
            if v:
                hit[key] = v
            else:
                hit.pop(key, None)
    if "site" in body:
        v = str(body.get("site") or "").strip()
        if v:
            hit["site"] = v
        else:
            hit.pop("site", None)
    if not persona_registry_save(reg):
        return {"ok": False, "msg": "保存失败"}
    print("persona: %s 设置已更新 %r" % (pid, {k: hit.get(k) for k in
                                              ("sticker_dir", "voice", "site")}), flush=True)
    return {"ok": True, "msg": "已更新 %s 的设置" % pid, **personas_list()}


def personas_del(body):
    pid = str((body or {}).get("id") or "").strip()
    reg = persona_registry_load()
    if pid == PERSONA_DEFAULT:
        return {"ok": False, "msg": "默认人格不能删"}
    if pid not in [str(it["id"]) for it in reg["items"]]:
        return {"ok": False, "msg": "没有这个人格"}
    if pid == reg["active"]:
        return {"ok": False, "msg": "它正在用，先切到别的人格再删"}
    reg["items"] = [it for it in reg["items"] if str(it["id"]) != pid]
    if not persona_registry_save(reg):
        return {"ok": False, "msg": "注册表保存失败"}
    try:
        import shutil as _sh
        _sh.rmtree(_persona_dir(pid), ignore_errors=True)
    except Exception:
        pass
    return {"ok": True, "msg": "已删除人格 %s" % pid, **personas_list()}


def personas_prompt_get(pid):
    pid = str(pid or "").strip() or persona_registry_load()["active"]
    return {"ok": True, "id": pid, "text": persona_prompt_of(pid),
            "path": os.path.join(_persona_dir(pid), "persona_prompt.txt")}


# ---- 底层人设（2026-11-08 用户要求：注入在所有人设之前，可按人格选择，实时生效）----
BASE_PROMPT_FILE = os.path.join(BOT_DIR, "data", "base_prompt.json")
BASE_PROMPT_MAX = 8000


def _base_prompt_read():
    try:
        with open(BASE_PROMPT_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def base_prompt_get():
    d = _base_prompt_read()
    text = str(d.get("text") or "")
    targets = [str(x) for x in (d.get("targets") or []) if str(x).strip()]
    return {"ok": True, "enabled": bool(d.get("enabled")), "targets": targets,
            "text": text, "at": str(d.get("at") or ""), "len": len(text),
            "max": BASE_PROMPT_MAX, "file": BASE_PROMPT_FILE}


def base_prompt_preview(persona=""):
    """这个人格现在会不会被注入（测试用：控制台点一下就看得出来，不用去群里发消息）。"""
    d = _base_prompt_read()
    pid = str(persona or "").strip()
    if not pid:
        try:
            pid = str(persona_registry_load().get("active") or PERSONA_DEFAULT)
        except Exception:
            pid = "feiyu"
    text = str(d.get("text") or "").strip()
    targets = [str(x) for x in (d.get("targets") or []) if str(x).strip()]
    if not d.get("enabled"):
        why, on = "总开关关着", False
    elif not text:
        why, on = "正文是空的", False
    elif targets and pid not in targets:
        why, on = "不在注入名单里（名单：%s）" % "、".join(targets), False
    else:
        why, on = ("所有人格都注入" if not targets else "在这个人格的名单里"), True
    return {"ok": True, "persona": pid, "inject": on, "why": why,
            "chars": (len(text) if on else 0),
            "head": ("【底层设定（公共前提，所有人格共用；下面才是这个角色自己的设定）】\n" + text) if on else ""}


def base_prompt_save(body):
    body = body or {}
    text = str(body.get("text") or "")
    if len(text) > BASE_PROMPT_MAX:
        return {"ok": False, "msg": "太长（%d 字，上限 %d）" % (len(text), BASE_PROMPT_MAX)}
    known = []
    try:
        known = [str(it["id"]) for it in persona_registry_load()["items"]]
    except Exception:
        pass
    raw_targets = body.get("targets")
    if isinstance(raw_targets, str):
        raw_targets = re.split(r"[,，、\s]+", raw_targets)
    targets = []
    for x in (raw_targets or []):
        x = str(x or "").strip()
        if not x or x in targets:
            continue
        if known and x not in known:
            return {"ok": False, "msg": "没有这个人格：%s" % x}
        targets.append(x)
    rec = {"enabled": bool(body.get("enabled")), "targets": targets, "text": text,
           "at": time.strftime("%Y-%m-%d %H:%M")}
    try:
        os.makedirs(os.path.dirname(BASE_PROMPT_FILE), exist_ok=True)
        tmp = BASE_PROMPT_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(rec, f, ensure_ascii=False, indent=1)
        os.replace(tmp, BASE_PROMPT_FILE)
        _chown_ubuntu(BASE_PROMPT_FILE)
        try:
            os.chmod(BASE_PROMPT_FILE, 0o644)
        except OSError:
            pass
    except OSError as e:
        return {"ok": False, "msg": "写文件失败：%r" % (e,)}
    print("base_prompt: 已保存（enabled=%s targets=%s %d 字）"
          % (rec["enabled"], targets or "全部", len(text)), flush=True)
    r = {"ok": True,
         "msg": "已保存（%d 字%s）—— 下一条消息就生效，不用重启"
                % (len(text), "，只给 " + "、".join(targets) if targets else "，所有人格")}
    r.update(base_prompt_get())
    return r


def personas_prompt_set(body):
    pid = str((body or {}).get("id") or "").strip()
    text = str((body or {}).get("text") or "")
    reg = persona_registry_load()
    if pid not in [str(it["id"]) for it in reg["items"]]:
        return {"ok": False, "msg": "没有这个人格"}
    p = os.path.join(_persona_dir(pid), "persona_prompt.txt")
    try:
        os.makedirs(_persona_dir(pid), exist_ok=True)
        tmp = p + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(text)
        os.replace(tmp, p)
        _chown_ubuntu(p)
        try:
            os.chmod(p, 0o644)
        except OSError:
            pass
    except OSError as e:
        return {"ok": False, "msg": "写文件失败：%r" % (e,)}
    print("persona: %s 人设正文已更新（%d 字）" % (pid, len(text)), flush=True)
    return {"ok": True, "msg": "已保存（%d 字，下一条消息生效）" % len(text), **personas_prompt_get(pid)}


# ---------------------------------------------------------------- 画图图库（2026-11-06）
# 插件那边每次 `/画图` 成功就把**原图**写进 data/drawlib/<id>.png + index.json（元数据）。
# 这里负责：列表 / 原图 / 缩略图（懒生成 + 缓存）/ 改标题标签备注收藏 / 删除。
# ⚠️ fishweb 是 root 跑的，插件是 ubuntu：**写盘后必须 _fix_owner**，否则下次插件写不进去
#    （2026-11-02 图库踩过一次，见 _fix_owner 的注释）。
DRAWLIB_DIR = os.path.join(BOT_DIR, "data", "drawlib")
DRAWLIB_FILE = os.path.join(DRAWLIB_DIR, "index.json")
DRAWLIB_THUMB_W = 480          # 缩略图宽度（按需生成，缓存在同目录 <id>.thumb.jpg）

_THUMB_SNIPPET = (
    "import sys,io\n"
    "from PIL import Image\n"
    "src,dst,w=sys.argv[1],sys.argv[2],int(sys.argv[3])\n"
    "im=Image.open(src); im=im.convert('RGB')\n"
    "if im.width>w:\n"
    "    im=im.resize((w,max(1,int(im.height*w/im.width))),Image.LANCZOS)\n"
    "im.save(dst,'JPEG',quality=86)\n"
)


def drawlib_load():
    """读 `/画图` 图库索引。坏文件当空表 —— 别让一条坏记录把整页打成 500。"""
    try:
        with open(DRAWLIB_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        if isinstance(d, dict):
            d = d.get("items")
        return [x for x in d if isinstance(x, dict)] if isinstance(d, list) else []
    except (OSError, ValueError):
        return []


def drawlib_save(items):
    try:
        os.makedirs(DRAWLIB_DIR, exist_ok=True)
        tmp = DRAWLIB_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(items or [], f, ensure_ascii=False, indent=1)
        os.replace(tmp, DRAWLIB_FILE)
        _fix_owner(DRAWLIB_FILE)
        return True
    except OSError as e:
        print("drawlib 保存失败: %r" % (e,), flush=True)
        return False


def _drawlib_path(rec):
    """记录 → 原图绝对路径（**只认 index 里的 file 名**，防目录穿越）。"""
    name = str((rec or {}).get("file") or "")
    if not name or os.path.basename(name) != name:
        return ""
    return os.path.join(DRAWLIB_DIR, name)


def drawlib_find(did):
    did = str(did or "").strip()
    for it in drawlib_load():
        if str(it.get("id") or "") == did:
            return it
    return None


def drawlib_thumb(rec):
    """缩略图字节：有缓存直接给，没有就现做一张存下来（PIL 不在就借 venv 的 python）。"""
    src = _drawlib_path(rec)
    if not src or not os.path.isfile(src):
        return None
    dst = os.path.join(DRAWLIB_DIR, os.path.splitext(os.path.basename(src))[0] + ".thumb.jpg")
    if os.path.isfile(dst) and os.path.getmtime(dst) >= os.path.getmtime(src):
        try:
            with open(dst, "rb") as f:
                return f.read()
        except OSError:
            pass
    if _pil_ok():
        try:
            import io as _io
            from PIL import Image
            im = Image.open(src).convert("RGB")
            if im.width > DRAWLIB_THUMB_W:
                im = im.resize(
                    (DRAWLIB_THUMB_W, max(1, int(im.height * DRAWLIB_THUMB_W / im.width))),
                    Image.LANCZOS)
            buf = _io.BytesIO()
            im.save(buf, "JPEG", quality=86)
            raw = buf.getvalue()
            with open(dst, "wb") as f:
                f.write(raw)
            _fix_owner(dst)
            return raw
        except Exception as e:
            print("drawlib 缩略图失败: %r" % (e,), flush=True)
    try:                                        # 系统 python 没有 Pillow：走 venv
        tmp_dst = dst + ".tmp"
        subprocess.run([_VENV_PY, "-c", _THUMB_SNIPPET, src, tmp_dst, str(DRAWLIB_THUMB_W)],
                       capture_output=True, timeout=60)
        if os.path.isfile(tmp_dst):
            os.replace(tmp_dst, dst)
            _fix_owner(dst)
            with open(dst, "rb") as f:
                return f.read()
    except Exception as e:
        print("drawlib 缩略图子进程失败: %r" % (e,), flush=True)
    return None


def _drawlib_tags(v):
    """标签清洗：`,`/`，`/`、`/空格/`|` 都算分隔，去重、最多 12 个、每个 ≤16 字。"""
    if isinstance(v, list):
        raw = [str(x) for x in v]
    else:
        raw = re.split(r"[,，、|\s]+", str(v or ""))
    out = []
    for t in raw:
        t = (t or "").strip()[:16]
        if t and t not in out:
            out.append(t)
    return out[:12]


def drawlib_list(limit=60, offset=0, q="", tag="", fav_only=False):
    items = drawlib_load()
    q = (q or "").strip().lower()
    tag = (tag or "").strip()

    def hit(it):
        if fav_only and not it.get("fav"):
            return False
        if tag and tag not in (it.get("tags") or []):
            return False
        if q:
            blob = " ".join([str(it.get(k) or "") for k in
                             ("title", "prompt", "user_want", "note", "style", "uid", "chat")])
            blob += " " + " ".join(str(x) for x in (it.get("tags") or []))
            if q not in blob.lower():
                return False
        return True

    sel = [it for it in items if hit(it)]
    try:
        limit = max(1, min(300, int(limit)))
    except (TypeError, ValueError):
        limit = 60
    try:
        offset = max(0, int(offset))
    except (TypeError, ValueError):
        offset = 0
    page = sel[offset:offset + limit]
    out = []
    for it in page:
        r = {k: it.get(k) for k in ("id", "file", "at", "uid", "chat", "persona", "prompt",
                                    "user_want", "style", "size", "cost_s", "model", "nsfw",
                                    "nref", "refs_kind", "title", "tags", "note", "fav", "bytes")}
        r["have_thumb"] = os.path.isfile(
            os.path.join(DRAWLIB_DIR,
                         os.path.splitext(str(it.get("file") or "x"))[0] + ".thumb.jpg"))
        out.append(r)
    tags = {}
    for it in items:
        for t in (it.get("tags") or []):
            tags[t] = tags.get(t, 0) + 1
    return {"ok": True, "total": len(sel), "all": len(items),
            "items": out, "tags": sorted(tags.items(), key=lambda kv: -kv[1])[:60]}


def drawlib_edit(did, title=None, tags=None, note=None, fav=None):
    did = str(did or "").strip()
    items = drawlib_load()
    hit = None
    for it in items:
        if str(it.get("id") or "") == did:
            hit = it
            break
    if hit is None:
        return {"ok": False, "msg": "图库里没这张"}
    if title is not None:
        hit["title"] = str(title).strip()[:80]
    if note is not None:
        hit["note"] = str(note).strip()[:500]
    if tags is not None:
        hit["tags"] = _drawlib_tags(tags)
    if fav is not None:
        hit["fav"] = bool(fav)
    hit["edited_at"] = time.strftime("%Y-%m-%d %H:%M:%S")
    if not drawlib_save(items):
        return {"ok": False, "msg": "写索引失败（权限？）"}
    return {"ok": True, "msg": "已保存", "item": hit}


def drawlib_del(did):
    did = str(did or "").strip()
    items = drawlib_load()
    keep, gone = [], None
    for it in items:
        if gone is None and str(it.get("id") or "") == did:
            gone = it
        else:
            keep.append(it)
    if gone is None:
        return {"ok": False, "msg": "图库里没这张"}
    src = _drawlib_path(gone)
    if src:
        try:
            os.remove(src)
        except OSError:
            pass
        try:
            os.remove(os.path.splitext(src)[0] + ".thumb.jpg")
        except OSError:
            pass
    if not drawlib_save(keep):
        return {"ok": False, "msg": "删了文件但索引没写进去，刷新看看"}
    return {"ok": True, "msg": "已删除"}


# ---------------------------------------------------------------- 画图模板（2026-11-06）
# 用户：「把画图功能预设模板作为一个页面添加到wpf控制台，就是二次元q版电影水墨脱衣，
#        这五个模板先加进去，然后得有增删改查功能」。
# 存 `data/draw_styles.json`，**插件按 mtime 热读**（改完下一条 `/画图` 就生效，不用重启 fishbot）。
# 文件不存在时这里给的就是插件 `draw_styles_builtin()` 那 5 个（⚠️ 改默认值要两边一起改，
# `04-脚本\测试\test_draw_styles.py` 会对拍：id / name / kind 必须一致）。
# 字段（和插件 `_draw_style_norm` 一一对应，多写的字段插件会忽略）：
#   id / name（关键词）/ kind(char 角色风 · scene 场景风 · act 动作) / aliases /
#   prompt / negative / size（空=默认尺寸）/ anime_neg / replace / enabled / note / builtin
DRAW_STYLES_FILE = os.path.join(BOT_DIR, "data", "draw_styles.json")
DRAW_STYLE_KINDS = ("char", "scene", "act", "wear", "post", "size")
DRAW_STYLE_ORDER = ("anime", "chibi", "cinema", "ink", "undress",
                    "watercolor", "impasto", "pixel", "sketch", "clay3d", "cyberpunk",
                    "bunny", "reverse_bunny", "bikini", "bandage", "jk", "maid",
                    "bust_big", "bust_flat")
DRAW_STYLE_DEFAULTS_EXTRA = [
    {"id": "watercolor", "name": "水彩", "kind": "char",
     "aliases": ["水彩画", "透明水彩", "watercolor", "aquarelle"],
     "prompt": "水彩画风，透明水彩晕染，颜色自然渗透融合，边缘柔和不清，纸纹与笔触可见，留白与淡彩层次，watercolor painting, wet on wet, soft edges, paper texture, translucent colors，",
     "negative": "", "size": "", "anime_neg": True, "replace": False,
     "enabled": True, "builtin": True,
     "note": "透明水彩：晕染 + 纸纹（参考图 5 类）"},
    {"id": "impasto", "name": "厚涂", "kind": "char",
     "aliases": ["油画", "厚涂风", "impasto", "oil"],
     "prompt": "厚涂油画质感，厚笔触堆积，颜料肌理与画布纹理明显，色彩浓郁饱和，明暗块面塑造体积，impasto, oil painting, thick brush strokes, canvas texture, rich colors，",
     "negative": "", "size": "", "anime_neg": True, "replace": False,
     "enabled": True, "builtin": True,
     "note": "厚涂/油画：厚笔触 + 画布纹理"},
    {"id": "pixel", "name": "像素", "kind": "char",
     "aliases": ["像素风", "pixel", "pixelart", "16bit"],
     "prompt": "像素画风，16 位游戏画面，有限色板，清晰像素块与硬边缘，无抗锯齿，pixel art, 16-bit, limited palette, crisp pixels, dithering, retro game sprite，",
     "negative": "", "size": "", "anime_neg": True, "replace": False,
     "enabled": True, "builtin": True,
     "note": "像素/16bit 复古游戏感"},
    {"id": "sketch", "name": "素描", "kind": "char",
     "aliases": ["线稿", "铅笔", "素描风", "sketch", "pencil"],
     "prompt": "铅笔素描，单色灰阶，排线塑造明暗，草稿感线条，纸张颗粒，pencil sketch, graphite, hatching, grayscale, rough lineart, paper grain，",
     "negative": "", "size": "", "anime_neg": True, "replace": False,
     "enabled": True, "builtin": True,
     "note": "铅笔素描/线稿：灰阶排线"},
    {"id": "clay3d", "name": "黏土3D", "kind": "char",
     "aliases": ["3d", "黏土", "黏土风", "clay", "render3d"],
     "prompt": "3D 渲染质感，软陶黏土角色，柔和次表面散射，圆润造型与柔和阴影，景深虚化，3d render, clay material, soft subsurface scattering, rounded shapes, octane render, depth of field，",
     "negative": "", "size": "", "anime_neg": True, "replace": False,
     "enabled": True, "builtin": True,
     "note": "3D/黏土手办感（注意：它会盖掉二次元赛璐璐）"},
    {"id": "cyberpunk", "name": "赛博朋克", "kind": "scene",
     "aliases": ["赛博", "cyberpunk", "霓虹"],
     "prompt": "赛博朋克场景，霓虹灯牌与全息广告，紫蓝色调，湿润反光地面，雨夜街道，体积光与镜头光晕，cyberpunk, neon signs, holographic ads, purple blue lighting, wet reflective streets, bloom，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "场景风：霓虹雨夜（可和人物风叠加）"},
    {"id": "bunny", "name": "兔女郎", "kind": "wear",
     "aliases": ["兔女郎装", "bunny", "bunnysuit"],
     "prompt": "穿着兔女郎装（bunny girl）：黑色亮面乳胶连体衣，强高光反光，胸前是两片分开的心形罩杯（不是一整片），中间深 V 露出乳沟，肩颈与手臂裸露，白色立领与黑色领结，白色袖口，黑色高跟鞋，黑色亮面兔耳发箍，自信笑容；腿上穿**中等网格的渔网袜**：菱形大小适中（比上一版密一档，像经典兔女郎那种），菱形均匀整齐、线条干净清楚、整条腿的网纹规整一致，不要大小不一、不要交叉处糊成一团，shiny black latex leotard, playboy bunny, two separate heart-shaped bra cups, deep cleavage, bare shoulders, white collar, black bowtie, white cuffs, rabbit ears headband, high heels, glossy highlights, fishnet tights with medium diamond pattern, neat regular small-to-medium mesh, clean distinct lines, even uniform grid, not messy, not cluttered,",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：兔女郎（连体衣 + 兔耳 + 中等菱格渔网袜；均匀整齐，别糊成一团）"},
    {"id": "reverse_bunny", "name": "逆兔女郎", "kind": "wear",
     "aliases": ["逆兔", "reverse bunny", "reverse_bunny"],
     "prompt": "穿着逆兔女郎装（reverse bunny）：黑色亮面连体衣两侧只剩窄条、胸腹与胯部全部敞开，胸前与阴部各用一枚心形贴遮住，白色立领与黑色领结，白色袖口，兔耳发箍，白色绒球尾，reverse bunny suit, open front, crotch fully open, no fabric over the crotch, thin hip straps, heart-shaped pastie on crotch, heart-shaped pasties on breasts, shiny black latex, white collar, black bowtie, white cuffs, bunny tail, rabbit ears headband, bare hips, exposed thighs, glossy skin，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：逆兔女郎（开胸 + 开裆，阴部心形贴）"},
    {"id": "bikini", "name": "泳装比基尼", "kind": "wear",
     "aliases": ["比基尼", "泳装", "bikini", "swimsuit"],
     "prompt": "穿着泳装：两件式比基尼，贴合身体曲线，露脐，泳池边的水光与湿发，bikini, two-piece swimsuit, swimwear, navel, wet skin, beach, sunlight，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：泳装/比基尼"},
    {"id": "bandage", "name": "绷带装", "kind": "wear",
     "aliases": ["绷带", "bandages", "mummy"],
     "prompt": "穿着绷带装：全身以白色医用绷带层层缠绕覆盖，露出少量皮肤，绷带褶皱与粗糙质感，bandage outfit, wrapped in bandages, mummy, white bandages, torn edges，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：绷带缠身"},
    {"id": "jk", "name": "日式JK", "kind": "wear",
     "aliases": ["日式jk", "jk", "水手服", "校服", "sailor", "日式"],
     "prompt": "穿着日式 JK 制服：水手领上衣与百褶裙，胸前领结，过膝袜，乐福鞋，校园感，serafuku, sailor collar, school uniform, pleated skirt, neckerchief, thighhighs, loafers，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：日式 JK/水手服"},
    {"id": "maid", "name": "女仆装", "kind": "wear",
     "aliases": ["女仆", "maid", "maidoutfit"],
     "prompt": "穿着女仆装：黑白女仆连衣裙，白色围裙与蕾丝花边，泡泡袖，女仆头饰，白色长袜，maid outfit, white apron, frills, puffy sleeves, maid headdress, white thighhighs，",
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "换装：女仆装（围裙 + 头饰）"},
    # 2026-11-08：胸部尺寸预设（`kind=size`）—— 和插件的 `DRAW_STYLE_EXTRA` **一字不差**
    # （`test_draw_styles.py` 的最后一段会把两边的默认表对拍，漂了就报错）。
    {"id": "bust_big", "name": "巨乳", "kind": "size",
     "aliases": ["大胸", "巨乳症", "大奶", "huge breasts", "big breasts", "busty", "f cup"],
     "prompt": ("【胸部尺寸：巨乳（这条**覆盖后面模板里「按原图身材比例画」那句**，优先级更高）】\n"
                "乳房体积很大，至少 F 罩杯以上：单侧乳房的宽度接近整个胸廓的一半，"
                "从锁骨下方一直垂到肋弓以下；受重力影响明显下垂、呈饱满的水滴形，"
                "下缘与胸廓之间压出柔和的褶皱和阴影；乳沟深，两乳内侧有挤压感；"
                "乳晕画大：直径差不多是两乳头间距的三分之一、颜色比肤色深、边缘清楚；乳头也大一圈、明显凸起、朝前偏下；乳房与身体的连接处有自然的重量感和拉伸感，"
                "皮肤被撑得很薄、有光泽。\n"
                "（huge heavy breasts, at least F cup, sagging teardrop shape, deep cleavage, "
                "large areola about one third of the nipple spacing, prominent bigger nipples, "
                 "gravity, natural weight）"),
     "negative": "", "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "note": "胸部尺寸：巨乳（F 罩杯以上，覆盖脱衣模板里「按原图身材」那句）"},
    {"id": "bust_flat", "name": "贫乳", "kind": "size",
     "aliases": ["平胸", "贫胸", "板胸", "飞机场", "flat chest", "small breasts", "aa cup"],
     "prompt": ("【胸部尺寸：贫乳 / 平板胸（这条**覆盖后面模板里「按原图身材比例画」那句**，优先级更高）】\n"
                "胸部**完全平贴**：胸廓正面就是一整片平缓的皮肤，锁骨到肋弓是一条平线，"
                "两乳之间有一道很浅的胸骨中线；乳房下缘和胸廓完全平接、看不出分界线，侧面轮廓是竖直的；"
                "就是**像少年 / 男孩子那样平的胸**（脸、腰、臀、肩仍然保持女性的线条）。"
                "乳头和乳晕画大一些：乳晕直径差不多是两乳头间距的三分之一、颜色明显比肤色深、边缘清楚；乳头也大一圈、明显凸起，都看得清楚。\n"
                "（flat as a boy's chest, flat ribcage, breast underside blending flat into the ribcage, "
                "shallow sternum line, keep the feminine face waist and hips, "
                "large areola about one third of the nipple spacing, prominent bigger nipples, "
                 "clearly visible）"),
     "negative": "大胸, 巨乳, 深乳沟, huge breasts, large breasts, big breasts, cleavage, busty,",
     "size": "", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True,
     "auto_ref": "bust_flat.png",
     "note": "胸部尺寸：贫乳（几乎没有隆起，侧面是直的；自动带一张平板胸参考图）"},
]

DRAW_STYLE_DEFAULTS = [
    {"id": "anime", "name": "二次元", "kind": "char",
     "aliases": ["动漫", "日系", "番剧", "anime"],
     "prompt": "二次元动漫插画，2D 动画风格，日系赛璐璐上色，扁平涂色，清晰线稿，"
               "anime illustration, 2D cel-shaded, flat colors, japanese anime art, "
               "not photorealistic，",
     "negative": "", "size": "", "anime_neg": True, "replace": False, "enabled": True,
     "builtin": True, "note": "文生图默认就是它（图生图默认保留原图画风）"},
    {"id": "chibi", "name": "Q版", "kind": "char",
     "aliases": ["q版", "q板", "chibi", "q萌", "萌版", "三头身"],
     "prompt": "Q版二头身角色，头大身小、脑袋约占全身一半，眼睛超大占脸一半以上带高光，"
               "五官极简（小嘴、几乎无鼻梁），脸颊腮红，手短脚圆、四肢粗短，深色粗描边，"
               "柔和的粉彩平涂上色，几乎无阴影，可爱治愈的贴纸感，纯净简单背景，"
               "chibi, super deformed, two-head-tall, huge sparkling eyes, minimal facial "
               "features, small mouth, blush, stubby limbs, thick dark outline, pastel flat "
               "colors, minimal shading, sticker style, clean simple background, cute，",
     "negative": "", "size": "", "anime_neg": True, "replace": False, "enabled": True,
     "builtin": True, "note": "二头身 / 超大眼睛 / 粗描边（参考图 2）"},
    {"id": "cinema", "name": "电影", "kind": "scene",
     "aliases": ["电影感", "电影画幅", "cinematic", "宽银幕"],
     "prompt": "电影画幅构图，宽银幕，电影级打光，戏剧性光影，暗部层次，浅景深，大气场景，"
               "cinematic composition, widescreen, film still, dramatic lighting, depth of field，",
     "negative": "", "size": "1376x576", "anime_neg": False, "replace": False,
     "enabled": True, "builtin": True, "note": "自动切成 2.39:1 宽银幕（1376x576）"},
    {"id": "ink", "name": "水墨", "kind": "scene",
     "aliases": ["水墨画", "国画", "写意", "ink"],
     "prompt": "中国水墨画风格，毛笔笔触，墨色晕染飞溅，大量留白，宣纸质感，写意，"
               "chinese ink wash painting, sumi-e, brush strokes, ink splatter, rice paper texture，",
     "negative": "", "size": "", "anime_neg": False, "replace": False, "enabled": True,
     "builtin": True, "note": "水墨 / 国画 / 写意（参考图 4）"},
    {"id": "undress", "name": "脱衣", "kind": "act",
     "aliases": ["脱衣模式", "undress"],
     "prompt": "角色完全赤裸（nude, fully naked, no clothes, bare skin）：身上没有任何衣物，上衣、裤子、裙子、内衣、内裤、胸罩、丝袜全部不存在，画面里不要残留任何布料；其余**一点都不要变**——保持原图的构图、景别、视角、取景范围、画风、笔触与线条，不要放大成特写，不要增加原图画风里没有的细节光影，原图取景之外不要凭空补；如果画面里能看到下半身，按**真实解剖**画出，位置别错：阴阜在耻骨上方；大阴唇在外侧、包住小阴唇；小阴唇薄而贴合、不外翻、不夸张；小阴唇之间是阴道前庭；尿道外口很小，位于阴蒂下方；阴道口在尿道外口下方、偏下位置，是竖直的开口，不要画成横向的缝或错位的洞；阴蒂头小巧，被阴蒂包皮自然覆盖，不外凸、不成肉粒；颜色柔和粉褐自然过渡，皮肤平滑；胸部按原图角色的身材自然呈现，乳头只在正常可见的角度才出现（位置在乳房中央偏下、颜色自然过渡），背面、远景、被手臂挡住这类看不到的角度就不要强行画出乳头；结构正确、位置正确，不要多余的缝、不要多余肢体、不要比例失调nude, fully naked, no clothes, bare skin, remove all clothing only, keep everything else identical, keep the original composition, framing, camera angle, art style, lineart and brush style, no zoom-in, no close-up crop, do not add detail or lighting that the original art style does not have, correct anatomy: mons pubis, outer labia covering inner labia, thin flat inner labia not flaring, vestibule between them, tiny urethral opening below the clitoris, vaginal opening below that, vertical opening, correct position, no misplaced slit, small clitoris covered by its hood, not protruding, no extra folds, no extra limbs, balanced proportions；**外阴形态**照下面画（这条优先级最高，覆盖上面「按真实解剖」里偏保守的措辞）：阴阜与小腹以下**完全无毛**、皮肤光洁（白虎），看不出毛囊和褶皱；整个外阴是**饱满隆起、圆润鼓起的馒头形**，肉感足、不干瘪、不下垂；两侧大阴唇**肥厚饱满**、紧密贴合、把中间挤成一道**竖直的浅缝**，闭合成一条细线，缝里只有柔和的阴影和一点高光，不外翻、不张开、不露出内部结构；皮肤白嫩细腻、透着淡粉，边缘有柔和高光，质感湿润平滑, hairless mons pubis, completely shaved, no pubic hair, plump rounded mons pubis, full and puffy, fleshy, not deflated, not sagging, thick full labia majora pressed together forming one closed vertical slit, smooth pale skin, soft pink tone, soft highlights, glossy moist skin, closed vulva with no inner details visible",
     "negative": "解剖错误, 多余肢体, 比例失调, 错位的开口, 多余的缝, 夸张阴蒂, 外凸肉粒, 巨大阴唇, 外翻, 遮挡, 马赛克, 模糊, bad anatomy, extra limbs, deformed, misplaced slit, extra folds, big clitoris, protruding clitoris, censored, mosaic, 阴毛, 多毛, 稀疏毛发, 干瘪, 下垂, 外翻, pubic hair, hairy, stubble, flat, deflated, sagging, gaping, ", "size": "", "anime_neg": False, "replace": True,
     "enabled": True, "builtin": True,
     "auto_ref": "undress1.png,undress2.png,undress3.png",
     "note": "带一张图打 `/画图 脱衣`：整段提示词换成上面这段（要 /nsfw on）；自动带 3 张参考图（饱满圆润的下体形态）"},
] + DRAW_STYLE_DEFAULTS_EXTRA



def draw_styles_load():
    """读模板表。文件不存在 = 用内置 5 个（**不落盘**，等第一次改/删再写）。

    文件在但是空表 = 真的没有模板（插件那边语义一致：删光了就是描述原样发）。
    """
    try:
        with open(DRAW_STYLES_FILE, "r", encoding="utf-8-sig") as f:
            d = json.load(f)
        if isinstance(d, dict):
            d = d.get("items")
        if isinstance(d, list):
            return [draw_style_norm(x, i) for i, x in enumerate(d) if isinstance(x, dict)]
    except (OSError, ValueError):
        pass
    return [draw_style_norm(x, i) for i, x in enumerate(DRAW_STYLE_DEFAULTS)]


def draw_styles_save(items):
    try:
        os.makedirs(os.path.dirname(DRAW_STYLES_FILE), exist_ok=True)
        tmp = DRAW_STYLES_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"at": time.strftime("%Y-%m-%d %H:%M:%S"),
                       "items": items or []}, f, ensure_ascii=False, indent=1)
        os.replace(tmp, DRAW_STYLES_FILE)
        _fix_owner(DRAW_STYLES_FILE)      # fishweb 是 root、插件是 ubuntu（老坑）
        return True
    except OSError as e:
        print("draw_styles 保存失败: %r" % (e,), flush=True)
        return False


def _ds_alias_list(v):
    if isinstance(v, list):
        raw = [str(x) for x in v]
    else:
        raw = re.split(r"[,，、|\s]+", str(v or ""))
    out = []
    for w in raw:
        w = (w or "").strip()[:24]
        if w and w not in out:
            out.append(w)
    return out


def draw_style_norm(it, i=0):
    """一条模板洗干净（和插件 `_draw_style_norm` 同一套规则）。"""
    d = dict(it or {})
    sid = re.sub(r"[^a-z0-9_]+", "", str(d.get("id") or "").strip().lower())[:32] or ("s%d" % i)
    name = str(d.get("name") or "").strip()[:24] or sid
    kind = str(d.get("kind") or "").strip().lower()
    if kind not in DRAW_STYLE_KINDS:
        kind = "char"
    size = str(d.get("size") or "").strip()
    if not re.fullmatch(r"\d{2,5}x\d{2,5}", size or ""):
        size = ""
    return {"id": sid, "name": name, "kind": kind,
            "aliases": _ds_alias_list(d.get("aliases")),
            "prompt": str(d.get("prompt") or ""),
            "negative": str(d.get("negative") or ""),
            "size": size,
            "anime_neg": bool(d.get("anime_neg")),
            "replace": bool(d.get("replace", kind == "act")),
            "enabled": bool(d.get("enabled", True)),
            "builtin": bool(d.get("builtin")),
            "note": str(d.get("note") or "")[:300],
            # 2026-11-08：后处理标记（马赛克），不产生提示词
            "post": str(d.get("post") or "")[:20],
            # 2026-11-08：构图模板用（图生图时锁人物），别被白名单扔掉
            "ref_hint": str(d.get("ref_hint") or "")[:400],
            # 2026-11-09：模板自带参考图（`贫乳` 用）
            "auto_ref": str(d.get("auto_ref") or "")[:120]}


def draw_styles_list():
    """控制台「画图模板」页的数据源。带上每条模板现在会命中哪些关键词。"""
    items = draw_styles_load()
    order = list(DRAW_STYLE_ORDER)
    for it in items:
        if it["id"] not in order:
            order.append(it["id"])
    items.sort(key=lambda x: order.index(x["id"]))
    for it in items:
        it["words"] = [it["name"]] + list(it.get("aliases") or [])
        it["in_use"] = it["id"] in DRAW_STYLE_ORDER
    return {"ok": True, "total": len(items), "items": items,
            "kinds": [["char", "角色风（和别的角色风互斥）"],
                      ["scene", "场景风（可以和别的场景风叠加）"],
                      ["size", "胸部尺寸（只贴一段话，不换画风，如巨乳/贫乳）"],
                      ["act", "动作（整段替换提示词，不顶风格词）"],
                      ["wear", "换装（追加服装描述，不改画风；两个换装写一起以最后一个为准）"]],
            "file": DRAW_STYLES_FILE}


def draw_style_upsert(body):
    """新增 / 修改。`id` 空 = 新增（自动发一个 id）。"""
    body = body or {}
    items = draw_styles_load()
    sid = re.sub(r"[^a-z0-9_]+", "", str(body.get("id") or "").strip().lower())[:32]
    old = None
    for it in items:
        if it["id"] == sid and sid:
            old = it
            break
    name = str(body.get("name") or "").strip()[:24]
    if not name:
        return {"ok": False, "msg": "名字不能空（它同时也是群里打的关键词）"}
    kind = str(body.get("kind") or ("char" if old is None else old["kind"])).strip().lower()
    if kind not in DRAW_STYLE_KINDS:
        return {"ok": False, "msg": "种类只能是 char / scene / act / wear / post / size"
                                    "（size = 胸部尺寸这种「只贴一段话」的预设，如巨乳/贫乳）"}
    # 关键词不许撞（撞了后加的那个永远轮不到，用户会以为没生效）
    for it in items:
        if it["id"] == sid:
            continue
        for w in [it["name"]] + list(it.get("aliases") or []):
            if w and w == name:
                return {"ok": False, "msg": "「%s」已经被模板「%s」占用了" % (name, it["name"])}
    prompt = str(body.get("prompt") or "")
    if kind not in ("act", "post") and not prompt.strip():
        return {"ok": False, "msg": "提示词不能空（画风模板全靠这一句）"}
    if len(prompt) > 4000 or len(str(body.get("negative") or "")) > 4000:
        return {"ok": False, "msg": "提示词太长了（4000 字以内）"}
    if not sid:
        sid = "t" + str(int(time.time()))[-6:] + str(len(items))
    rec = draw_style_norm({
        "id": sid, "name": name, "kind": kind,
        "aliases": body.get("aliases"),
        "prompt": prompt,
        "negative": body.get("negative"),
        "size": body.get("size"),
        "anime_neg": body.get("anime_neg", old["anime_neg"] if old else (kind == "char")),
        "replace": body.get("replace", old["replace"] if old else (kind == "act")),
        "enabled": body.get("enabled", True if old is None else old["enabled"]),
        "builtin": old["builtin"] if old else False,
        "note": body.get("note"),
        # 2026-11-08：构图模板（四视图）用：图生图时告诉模型「参考图是这个人」
        "ref_hint": body.get("ref_hint", old.get("ref_hint") if old else ""),
        "post": body.get("post", old.get("post") if old else ""),
        # 2026-11-11：`auto_ref`（模板自带参考图，`贫乳` 靠它带平板胸那张）
        # ⚠️ 这里原来**没传**，而 `draw_style_norm` 会写成 `str(d.get("auto_ref") or "")`
        # → 每次控制台保存都把 auto_ref 清空，`/画图 脱衣 贫乳` 于是只剩文字、画不平。
        "auto_ref": body.get("auto_ref", old.get("auto_ref") if old else ""),
    })
    if kind == "act":
        rec["replace"] = True
    if old is None:
        items.append(rec)
    else:
        items[items.index(old)] = rec
    if not draw_styles_save(items):
        return {"ok": False, "msg": "写文件失败（权限？）"}
    return {"ok": True, "msg": "已保存「%s」（群里下一条 /画图 就生效）" % name, "item": rec}


def draw_style_del(sid):
    sid = str(sid or "").strip()
    items = draw_styles_load()
    keep, gone = [], None
    for it in items:
        if gone is None and it["id"] == sid:
            gone = it
        else:
            keep.append(it)
    if gone is None:
        return {"ok": False, "msg": "没有这个模板"}
    if not draw_styles_save(keep):
        return {"ok": False, "msg": "写文件失败（权限？）"}
    extra = ""
    if gone["id"] in DRAW_STYLE_ORDER:
        extra = "（它是内置的，删了就没有这个画风词了；想回来点「恢复 5 个默认」）"
    return {"ok": True, "msg": "已删除「%s」%s" % (gone["name"], extra)}


def draw_styles_reset():
    """把 5 个内置默认写回文件（用户可能改坏了 / 删光了）。"""
    items = [draw_style_norm(x, i) for i, x in enumerate(DRAW_STYLE_DEFAULTS)]
    if not draw_styles_save(items):
        return {"ok": False, "msg": "写文件失败（权限？）"}
    return {"ok": True, "msg": "已恢复 5 个默认模板"}


# ---- 鱼的记忆（nonebot_plugin_fishmemory，2026-11-07）--------------------------
# WebUI 自己再加载一份纯逻辑模块（config/engine/store 都不 import nonebot），
# 这样「列表 / 召回」和机器人的行为完全一致，不用把算法抄第二遍。
FISHMEM_DIR = os.path.join(BOT_DIR, "data", "fishmemory")
_FMCACHE = {}


def _fishmem():
    """按路径加载记忆插件的纯逻辑部分；加载不上就返回 None（页面会说明原因）。"""
    if _FMCACHE.get("engine"):
        return _FMCACHE
    if _FMCACHE.get("bad"):
        return None
    try:
        import importlib
        import types
        root = os.path.join(BOT_DIR, "src", "nonebot_plugin_fishmemory")
        if "fmpkg" not in sys.modules:
            pkg = types.ModuleType("fmpkg")
            pkg.__path__ = [root]
            sys.modules["fmpkg"] = pkg
        cfg = importlib.import_module("fmpkg.config")
        cfg.set_data_root(FISHMEM_DIR)
        eng = importlib.import_module("fmpkg.engine")
        sto = importlib.import_module("fmpkg.store")
        _FMCACHE.update({"config": cfg, "engine": eng, "store": sto})
        return _FMCACHE
    except Exception as e:
        _FMCACHE["bad"] = True
        print("鱼的记忆加载失败: %r" % (e,), flush=True)
        return None


def fishmem_personas():
    try:
        return sorted([d for d in os.listdir(FISHMEM_DIR)
                       if os.path.isdir(os.path.join(FISHMEM_DIR, d))])
    except OSError:
        return []


def _fm_chat_of(filename):
    """group_123.json → group:123（store 里会话名就是这个形状）。"""
    base = filename[:-5] if filename.endswith(".json") else filename
    for pre in ("group_", "private_"):
        if base.startswith(pre):
            return pre[:-1] + ":" + base[len(pre):]
    return base


def fishmem_chats(persona):
    fm = _fishmem()
    persona = str(persona or "feiyu")
    base = os.path.join(FISHMEM_DIR, persona)
    rows = []
    try:
        names = [f for f in os.listdir(base) if f.endswith(".json")]
    except OSError:
        return rows
    for f in sorted(names):
        chat = _fm_chat_of(f)
        n_live = n_fact = 0
        try:
            d = fm["store"].load(persona, chat) if fm else {}
            items = [x for x in (d.get("items") or []) if isinstance(x, dict)]
            live = [x for x in items if not x.get("superseded")]
            n_live = len(live)
            n_fact = len([x for x in live if str(x.get("kind")) == "fact"])
        except Exception:
            pass
        rows.append({"chat": chat, "live": n_live, "facts": n_fact})
    return rows


def fishmem_view(persona, chat):
    fm = _fishmem()
    if fm is None:
        return {"ok": False, "error": "记忆插件没加载上（看 fishweb 日志里的『鱼的记忆加载失败』）"}
    persona = str(persona or "feiyu")
    chat = str(chat or "")
    if not chat:
        return {"ok": True, "persona": persona, "chat": "", "items": [], "stats": {},
                "cfg": {}, "personas": fishmem_personas(), "chats": fishmem_chats(persona)}
    st, eng, cfg = fm["store"], fm["engine"], fm["config"]
    d = st.load(persona, chat)
    now = time.time()
    items = []
    for it in d.get("items") or []:
        if not isinstance(it, dict):
            continue
        items.append({
            "id": it.get("id"),
            "text": it.get("text") or "",
            "kind": it.get("kind") or "event",
            "key": it.get("key") or "",
            "importance": round(float(it.get("importance") or 0), 3),
            "emotion": round(float(it.get("emotion") or 0), 3),
            "at": it.get("at") or "",
            "ts": float(it.get("ts") or 0),
            "date": eng.fmt_at(it.get("ts"), now),
            "vitality": round(eng.vitality(it, now), 3),
            "hits": int(it.get("hits") or 0),
            "superseded": bool(it.get("superseded")),
        })
    items.sort(key=lambda x: -float(x.get("ts") or 0))
    live = [x for x in items if not x["superseded"]]
    day = d.get("day") or {}
    return {
        "ok": True, "persona": persona, "chat": chat, "items": items,
        "stats": {"total": len(items), "live": len(live),
                  "facts": len([x for x in live if x["kind"] == "fact"]),
                  "events": len([x for x in live if x["kind"] != "fact"]),
                  "hits": sum(x["hits"] for x in items),
                  "today": int(day.get("n") or 0), "day": day.get("date") or "-"},
        "cfg": {"daily": cfg.CFG.daily, "max_inject": cfg.CFG.max_inject,
                "max_chars": cfg.CFG.max_chars, "facts": cfg.CFG.facts,
                "half_life": cfg.CFG.half_life, "evidence": cfg.CFG.evidence},
        "personas": fishmem_personas(), "chats": fishmem_chats(persona),
    }


def _fm_fix(st, persona, chat):
    """把写过的文件（含 .bak）属主还给 ubuntu —— fishweb 是 root，不还回去插件就写不动。"""
    base = st.path_of(persona, chat)
    for p in (base, base + ".bak"):
        try:
            _fix_owner(p)
        except Exception:
            pass


def fishmem_action(body):
    """add / del / forget / clear / recall —— 全走一个入口，省得加五条路由。"""
    fm = _fishmem()
    if fm is None:
        return {"ok": False, "error": "记忆插件没加载上"}
    st, eng = fm["store"], fm["engine"]
    act = str((body or {}).get("act") or "")
    persona = str((body or {}).get("persona") or "feiyu")
    chat = str((body or {}).get("chat") or "")
    if not chat:
        return {"ok": False, "error": "缺 chat（例：group:1000000004）"}
    try:
        if act == "add":
            text = str((body or {}).get("text") or "").strip()
            if not text:
                return {"ok": False, "error": "内容空的"}
            key = str((body or {}).get("key") or "").strip()
            d = st.load(persona, chat)
            cand = {"text": eng.to_third(text)[:160],
                    "kind": "fact" if key else "event",
                    "key": key,
                    "importance": 0.85 if key else 0.7,
                    "emotion": eng.emotion_of(text)}
            r = eng.upsert(d, cand)
            if r.get("ok"):
                st.save(persona, chat, d)
                _fm_fix(st, persona, chat)
            return {"ok": bool(r.get("ok")), "why": r.get("why"), "item": r.get("item")}
        if act == "del":
            did = str((body or {}).get("id") or "")
            d = st.load(persona, chat)
            before = len(d.get("items") or [])
            d["items"] = [x for x in (d.get("items") or []) if str(x.get("id")) != did]
            if len(d["items"]) == before:
                return {"ok": False, "error": "没找到 id=%s" % did}
            st.save(persona, chat, d)
            _fm_fix(st, persona, chat)
            return {"ok": True, "removed": before - len(d["items"])}
        if act == "forget":
            kw = str((body or {}).get("kw") or "").strip()
            if not kw:
                return {"ok": False, "error": "缺关键词"}
            d = st.load(persona, chat)
            keep = [x for x in (d.get("items") or [])
                    if kw not in (x.get("text") or "") and kw not in (x.get("key") or "")]
            n = len(d.get("items") or []) - len(keep)
            if n:
                d["items"] = keep
                st.save(persona, chat, d)
                _fm_fix(st, persona, chat)
            return {"ok": True, "removed": n}
        if act == "clear":
            st.wipe(persona, chat)
            return {"ok": True}
        if act == "recall":
            q = str((body or {}).get("q") or "").strip()
            d = st.load(persona, chat)
            r = eng.recall(d, q)
            return {"ok": True, "block": r["block"],
                    "facts": [{"text": i.get("text")} for i in r["facts"]],
                    "picked": [{"text": i.get("text"), "date": eng.fmt_at(i.get("ts"))}
                               for i in r["picked"]],
                    "skipped": [{"text": i.get("text"), "why": w} for i, w in r["skipped"]]}
        return {"ok": False, "error": "未知动作 %r" % act}
    except Exception as e:
        print("鱼的记忆动作失败 %r: %r" % (act, e), flush=True)
        return {"ok": False, "error": "内部错误：%r" % (e,)}



class Handler(BaseHTTPRequestHandler):
    server_version = "FishWeb/1.0"
    def log_message(self, fmt, *args):
        pass

    def _auth_ok(self, q):
        """两种方式：
          ① X-Token 头（控制台/脚本用，不放进 URL）
          ② ?token= 查询参数（网页/老入口）
        """
        tok = self.headers.get("X-Token", "")
        if not tok:
            tok = (q.get("token", [""])[0] or "")
        return hmac.compare_digest(tok, TOKEN)

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_file(self, path, filename):
        """把本地文件当附件下载（中文文件名用 filename* 给浏览器）。"""
        try:
            with open(path, "rb") as f:
                data = f.read()
        except OSError:
            return self._send(404, {"error": "文件不见了"})
        ascii_name = re.sub(r"[^A-Za-z0-9._-]+", "_", filename) or "chat.txt"
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Content-Disposition",
                         'attachment; filename="%s"; filename*=UTF-8\'\'%s'
                         % (ascii_name, quote(filename)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def _qq_static(self, path):
        """把 /Stapxs-QQ-Lite-2.0/xxx 映射到 static/qq/xxx（默认 index.html）。"""
        # 浏览器发来的是百分号编码（资源里有中文名，比如字体 `方正喵呜体-xxx.ttf`），
        # 不 unquote 就永远 404。
        if path.startswith(QQ_BASE):
            rel = unquote(path[len(QQ_BASE):].strip("/")) or "index.html"
        else:
            rel = unquote(path.lstrip("/"))
        root = os.path.abspath(QQ_DIR)
        full = os.path.abspath(os.path.join(root, rel))
        # 防目录穿越：必须还在 static/qq 里面
        if not (full == root or full.startswith(root + os.sep)):
            return self._send(404, "no", "text/plain; charset=utf-8")
        if os.path.isdir(full):
            full = os.path.join(full, "index.html")
        if not os.path.exists(full):
            # 前端是单页应用：没有后缀的路径回落到 index.html，带后缀的（js/css/png）老实 404
            if not os.path.splitext(rel)[1]:
                full = os.path.join(root, "index.html")
            if not os.path.exists(full):
                return self._send(404, "Web QQ 还没部署（缺 static/qq/）",
                                  "text/plain; charset=utf-8")
        ext = os.path.splitext(full)[1].lower()
        ctype = QQ_MIME.get(ext, "application/octet-stream")
        # gzip：服务器上行只有几兆，10MB 的前端不压缩要 40 秒才 onload
        # （Stapxs 的自动连接写在 window.onload 里，等不起）。.gz 由部署脚本预先生成。
        gz = full + ".gz"
        accept = (self.headers.get("Accept-Encoding") or "").lower()
        if "gzip" in accept and os.path.exists(gz):
            try:
                with open(gz, "rb") as f:
                    data = f.read()
            except OSError:
                data = None
            if data is not None:
                self.send_response(200)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Encoding", "gzip")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Vary", "Accept-Encoding")
                self.send_header("Cache-Control",
                                 "no-store" if rel == "index.html" else "public, max-age=3600")
                self.end_headers()
                return self.wfile.write(data)
        try:
            with open(full, "rb") as f:
                data = f.read()
        except OSError:
            return self._send(404, "no", "text/plain; charset=utf-8")
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        # 带 hash 的资源可以缓存；index.html 每次都要新，免得发了新版还加载旧壳
        if rel == "index.html":
            self.send_header("Cache-Control", "no-store")
        else:
            self.send_header("Cache-Control", "public, max-age=3600")
        self.end_headers()
        self.wfile.write(data)

    def _ws_relay(self, up_host, up_port, up_path, up_query="", err_name="上游"):
        """通用透明 WebSocket 反代：浏览器 ⇄ 本机某个 ws 服务。

        - 上游握手**沿用浏览器给的 Sec-WebSocket-Key** → 上游算出的 Sec-WebSocket-Accept
          对浏览器仍然有效，101 原样回过去；
        - 握手之后原样双向搬字节（WS 帧不拆不改，本来就该端到端）。
        """
        key = (self.headers.get("Sec-WebSocket-Key") or "").strip()
        if not key:
            return self._send(400, {"error": "不是 websocket 请求"})
        try:
            us = socket.create_connection((up_host, up_port), timeout=10)
        except OSError as e:
            return self._send(502, {"error": "连不上%s：%s" % (err_name, e)})
        target = up_path + (("?" + up_query) if up_query else "")
        req = ("GET %s HTTP/1.1\r\n"
               "Host: %s:%d\r\n"
               "Upgrade: websocket\r\n"
               "Connection: Upgrade\r\n"
               "Sec-WebSocket-Key: %s\r\n"
               "Sec-WebSocket-Version: 13\r\n"
               "\r\n" % (target, up_host, up_port, key))
        try:
            us.sendall(req.encode("utf-8"))
            us.settimeout(10)
            buf = b""
            while b"\r\n\r\n" not in buf:
                chunk = us.recv(4096)
                if not chunk:
                    break
                buf += chunk
        except OSError as e:
            try:
                us.close()
            except OSError:
                pass
            return self._send(502, {"error": "%s握手失败：%s" % (err_name, e)})
        head, _, rest = buf.partition(b"\r\n\r\n")
        first = (head.split(b"\r\n")[0] if head else b"")
        if b" 101" not in first:
            try:
                us.close()
            except OSError:
                pass
            return self._send(502, {"error": "%s 拒绝了 WS：%s"
                                             % (err_name, first.decode("utf-8", "replace")[:120])})
        self.wfile.write(head + b"\r\n\r\n" + rest)
        self.wfile.flush()
        self.close_connection = True
        try:
            us.settimeout(None)
        except OSError:
            pass

        def _pump(src, dst):
            try:
                while True:
                    d = src.recv(65536)
                    if not d:
                        break
                    dst.sendall(d)
            except OSError:
                pass
            finally:
                try:
                    dst.shutdown(socket.SHUT_WR)
                except OSError:
                    pass

        threading.Thread(target=_pump, args=(self.connection, us), daemon=True).start()
        _pump(us, self.connection)
        try:
            us.close()
        except OSError:
            pass

    def _qq_ws_proxy(self, q):
        """浏览器 ⇄ 本机 OneBot(3001)：token 走 `?access_token=`（Stapxs 只会拼这个参数）。"""
        tok = ""
        try:
            tok = (q.get("access_token", [""])[0] or "").strip()
        except (AttributeError, IndexError):
            tok = ""
        if not tok:
            tok = (self.headers.get("X-Token") or "").strip()
        if not hmac.compare_digest(str(tok), TOKEN):
            return self._send(401, {"error": "token"})
        up = onebot_ws_target()
        if not up:
            return self._send(503, {"error": "没找到 OneBot WS 配置（.env 的 ONEBOT_WS_URLS）"})
        host, port, wpath, wtok = up
        return self._ws_relay(host, port, wpath, "access_token=" + wtok, err_name="OneBot")

    # ---------------- 真·QQ（noVNC）----------------
    def _vnc_ws_proxy(self, path):
        """浏览器 ⇄ 本机 websockify(6081/websockify)。

        token 放在**路径最后一段**：noVNC 用 `?path=…` 只拼路径、不会带查询参数，
        所以我们把 token 塞进路径（`/vnc/websockify/<token>`），上游再还原成 `/websockify`。
        """
        tok = (path.rsplit("/", 1)[-1] or "").strip()
        if not hmac.compare_digest(tok, TOKEN):
            return self._send(401, {"error": "token"})
        return self._ws_relay(VNC_HOST, VNC_PORT, VNC_WS_UP, "", err_name="VNC")

    def _vnc_static(self, path):
        """把 noVNC 的静态页从容器那边取回来转发（websockify 自带 --web /opt/noVNC）。"""
        rel = path[len(VNC_PREFIX):] or "vnc.html"
        if not rel or rel.endswith("/"):
            rel += "vnc.html"
        if ".." in rel or rel.startswith("/"):
            return self._send(404, "no", "text/plain; charset=utf-8")
        url = "http://%s:%d/%s" % (VNC_HOST, VNC_PORT, rel)
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "fishweb-vnc"})
            with urllib.request.urlopen(req, timeout=10) as r:
                data = r.read()
                ctype = r.headers.get("Content-Type") or "application/octet-stream"
        except Exception as e:
            return self._send(502, "noVNC 取不到：%s" % e, "text/plain; charset=utf-8")
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "public, max-age=600")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        u = urlparse(self.path)
        q = parse_qs(u.query)
        path = u.path

        # ---- 真·QQ（noVNC）：静态页公开，WS 必须带 token（token 在路径最后一段）----
        if path.startswith(VNC_PREFIX):
            rest = path[len(VNC_PREFIX):]
            if rest.startswith("websockify"):
                return self._vnc_ws_proxy(path)
            return self._vnc_static(path)

        # ---- Web QQ（Stapxs QQ Lite 2.0）：静态资源 + WS 反代 ----
        # 放在 token 鉴权之前：页面/资源是公开的开源前端，秘密只在下面那条 WS 上。
        if path == "/qqws":
            return self._qq_ws_proxy(q)
        if (path == QQ_BASE or path.startswith(QQ_BASE + "/")
                or path.startswith(QQ_ROOT_PREFIXES) or path in QQ_ROOT_FILES):
            return self._qq_static(path)

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

        # 短链跳转：/s/<code> → 302 到真实地址。
        # 故意放在 token 鉴权之前 —— 用户点链接不可能带 token。
        if path.startswith("/s/"):
            code = path[3:].strip("/")
            real = short_get(code)
            if not real:
                return self._send(410, "这个链接过期了，让肥鱼重新发一次 /get 吧。",
                                  "text/plain; charset=utf-8")
            self.send_response(302)
            self.send_header("Location", real)
            self.send_header("Content-Length", "0")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            return

        # Web QQ 的启动页：把 token 预置进 Stapxs 的 localStorage 再跳过去。
        # 公开可取（页面里没有 token），带 ?token= 才有用。
        if path in ("/qq", "/qq.html"):
            p = os.path.join(STATIC, "qq.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no qq page", "text/plain; charset=utf-8")

        # 下载区（APK 之类）：/dl/<文件名>。放在鉴权之前——手机上点链接下载不可能带 token。
        # 只允许 static/dl/ 下的普通文件名，防目录穿越。
        if path.startswith("/dl/"):
            name = unquote(path[4:])
            if not re.fullmatch(r"[A-Za-z0-9_.\-]{1,80}", name or ""):
                return self._send(404, "no", "text/plain; charset=utf-8")
            dl = os.path.join(STATIC, "dl", name)
            if not os.path.isfile(dl):
                return self._send(404, "文件不存在", "text/plain; charset=utf-8")
            try:
                with open(dl, "rb") as f:
                    data = f.read()
            except OSError:
                return self._send(404, "读不到", "text/plain; charset=utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/vnd.android.package-archive"
                             if name.lower().endswith(".apk") else "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Content-Disposition", 'attachment; filename="%s"' % name)
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            return self.wfile.write(data)

        # 画图图库页面（公开：页面本身不含秘密，数据和图片走 /api 要 token）
        if path in ("/drawlib", "/drawlib.html"):
            p = os.path.join(STATIC, "drawlib.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no drawlib page", "text/plain; charset=utf-8")
        # 鱼的记忆页面（公开：页面不含秘密，数据走 /api/fishmem 要 token）
        if path in ("/fishmem", "/fishmem.html"):
            p = os.path.join(STATIC, "fishmem.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no fishmem page", "text/plain; charset=utf-8")

        # 个人好感度页：公开，但只吐自己的数据（放在 token 鉴权之前）
        if path in ("/me", "/me.html"):
            p = os.path.join(STATIC, "me.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no me page", "text/plain; charset=utf-8")
        if path == "/api/me":
            qq = (q.get("qq", [""])[0] or "").strip()
            r = me_lookup(qq)
            return self._send(200 if r.get("ok") else 404, r)

        if not self._auth_ok(q) and path not in ("/index.html", "/roster.html"):
            return self._send(401, {"error": "token"})

        if path == "/api/profile":
            return self._send(200, profile_get())
        if path == "/api/profile/avatar":
            data = profile_avatar_bytes(force=(q.get("force", [""])[0] == "1"))
            if not data:
                return self._send(404, {"error": "no avatar"})
            mt = "image/png" if data[:8] == b"\x89PNG\r\n\x1a\n" else "image/jpeg"
            return self._send(200, data, mt)
        if path == "/api/qzone":
            return self._send(200, qzone_list())
        if path == "/api/voice_style":
            return self._send(200, voice_style_get())
        # 每个人格一套音色（2026-10-30）：?id=&text= 试听，直接回 mp3
        if path == "/api/voice/options":
            return self._send(200, voice_options())
        if path == "/api/voice/preview":
            pid = (q.get("id", [""])[0] or PERSONA_DEFAULT).strip()
            text = (q.get("text", [""])[0] or "").strip()
            data, err = voice_preview_bytes(pid, text)
            if not data:
                return self._send(500, "试听失败：%s" % err, "text/plain; charset=utf-8")
            return self._send(200, data, "audio/mpeg")
        if path == "/api/bot_blacklist":
            return self._send(200, bot_blacklist_get())
        if path == "/api/bots":
            return self._send(200, bots_get())
        if path == "/api/perf":
            return self._send(200, perf_get())
        if path == "/api/memes/sticker":
            key = (q.get("id", [""])[0] or "").strip()
            for it in _meme_load():
                if str(it.get("id") or "") == key:
                    p = _meme_sticker_path(it)
                    if p:
                        try:
                            with open(p, "rb") as f:
                                return self._send(200, f.read(), "image/jpeg")
                        except OSError:
                            pass
            return self._send(404, {"error": "no sticker"})
        if path == "/api/memes":
            return self._send(200, memes_get())
        # ---- 表情包库（控制台聊天页发表情用）----
        if path == "/api/stickers":
            return self._send(200, stickers_list(force=(q.get("fresh", ["0"])[0] == "1")))
        if path == "/api/slang":
            # 群聊黑话（2026-10-30）：?gid=群号 → 只给这个群；不带 gid → 整库（控制台编辑页用）
            _gid = (q.get("gid", [""])[0] or "").strip()
            return self._send(200, slang_get(_gid) if _gid else slang_all_api())
        # ---- 角色人格（多套人设隔离）----
        if path == "/api/persona_bindings":
            return self._send(200, persona_bindings_api({}))
        if path == "/api/personas":
            return self._send(200, personas_list())
        if path == "/api/personas/prompt":
            return self._send(200, personas_prompt_get((q.get("id", [""])[0] or "")))
        if path == "/api/base_prompt":
            return self._send(200, base_prompt_get())
        if path == "/api/base_prompt/preview":
            return self._send(200, base_prompt_preview((q.get("persona", [""])[0] or "")))
        if path == "/api/stickers/thumbs":
            ids = [x for x in (q.get("ids", [""])[0] or "").split(",") if x.strip()]
            return self._send(200, sticker_thumbs(ids))
        if path == "/api/stickers/thumb":
            r = sticker_thumbs([(q.get("id", [""])[0] or "").strip()])
            if not r["items"]:
                return self._send(404, "no thumb", "text/plain; charset=utf-8")
            return self._send(200, base64.b64decode(r["items"][0]["b64"]), "image/jpeg")
        if path == "/api/apis":
            return self._send(200, api_profiles_get())
        if path == "/api/tokens":
            return self._send(200, token_usage(int((q.get("days", ["7"])[0] or 7))))
        if path == "/api/photolib":
            _alb = (q.get("album", [""])[0] or "").strip()
            try:
                _lim = int((q.get("limit", ["0"])[0] or 0))
            except (TypeError, ValueError):
                _lim = 0
            return self._send(200, photolib_list(limit=_lim if _lim > 0 else 2000,
                                                 album=_alb or None))
        if path == "/api/photolib/albums":
            return self._send(200, photolib_albums())
        # ---- 画图图库（2026-11-06）：列表 / 原图 / 缩略图 ----
        if path == "/api/drawlib":
            try:
                _lim = int((q.get("limit", ["60"])[0] or 60))
            except (TypeError, ValueError):
                _lim = 60
            try:
                _off = int((q.get("offset", ["0"])[0] or 0))
            except (TypeError, ValueError):
                _off = 0
            return self._send(200, drawlib_list(
                limit=_lim, offset=_off,
                q=(q.get("q", [""])[0] or ""),
                tag=(q.get("tag", [""])[0] or ""),
                fav_only=(q.get("fav", [""])[0] == "1")))
        if path in ("/api/drawlib/img", "/api/drawlib/thumb"):
            rec = drawlib_find((q.get("id", [""])[0] or "").strip())
            if rec is None:
                return self._send(404, {"error": "no such image"})
            if path.endswith("/thumb"):
                raw = drawlib_thumb(rec)
                if not raw:
                    return self._send(404, {"error": "no thumb"})
                return self._send(200, raw, "image/jpeg")
            src = _drawlib_path(rec)
            if not src or not os.path.isfile(src):
                return self._send(404, {"error": "file gone"})
            try:
                with open(src, "rb") as f:
                    data = f.read()
            except OSError:
                return self._send(404, {"error": "read fail"})
            mt = "image/png" if data[:8] == b"\x89PNG\r\n\x1a\n" else "image/jpeg"
            if (q.get("dl", [""])[0] == "1"):       # ?dl=1 → 浏览器直接下载原图
                self.send_response(200)
                self.send_header("Content-Type", mt)
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Content-Disposition",
                                 'attachment; filename="%s.png"' % str(rec.get("id") or "draw"))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                return self.wfile.write(data)
            return self._send(200, data, mt)
        # ---- 鱼的记忆（2026-11-07）：列表 + 会话清单 ----
        if path == "/api/fishmem":
            return self._send(200, fishmem_view((q.get("persona", ["feiyu"])[0] or "feiyu"),
                                                (q.get("chat", [""])[0] or "")))
        if path == "/api/fishmem/chats":
            return self._send(200, {"ok": True,
                                    "personas": fishmem_personas(),
                                    "chats": fishmem_chats(q.get("persona", ["feiyu"])[0] or "feiyu")})
        # ---- 画图模板（2026-11-06）：二次元 / Q版 / 电影 / 水墨 / 脱衣，能增删改查 ----
        if path == "/api/draw_styles":
            return self._send(200, draw_styles_list())
        # ---- 聊天里「她自己发的图」：插件存在 data/chatimg/，只按文件名取（防目录穿越）----
        if path == "/api/imgfile":
            fn = os.path.basename((q.get("f", [""])[0] or "").strip())
            if not fn or not re.fullmatch(r"[A-Za-z0-9_.\-]{1,80}", fn):
                return self._send(404, "no img", "text/plain; charset=utf-8")
            fp = os.path.join(BOT_DIR, "data", "chatimg", fn)
            if not os.path.isfile(fp):
                return self._send(404, "no img", "text/plain; charset=utf-8")
            try:
                with open(fp, "rb") as f:
                    blob = f.read()
            except OSError:
                return self._send(404, "no img", "text/plain; charset=utf-8")
            mime = ("image/png" if fn.lower().endswith(".png") else
                    "image/gif" if fn.lower().endswith(".gif") else
                    "image/webp" if fn.lower().endswith(".webp") else "image/jpeg")
            return self._send(200, blob, mime)
        if path == "/api/photolib/tags":
            return self._send(200, photolib_tags((q.get("sort", ["name"])[0] or "name")))
        if path == "/api/photolib/autotag":
            # 自动打标签：开关状态 + 待识别张数 + 上次结果
            return self._send(200, autotag_status())
        if path == "/api/photolib/untagged":
            return self._send(200, photolib_untagged((q.get("scope", ["queue"])[0] or "queue"),
                                                     (q.get("limit", ["12"])[0] or 12)))
        if path == "/api/photolib/thumb":
            data = photolib_thumb((q.get("id", [""])[0] or ""))
            if not data:
                return self._send(404, "no thumb", "text/plain; charset=utf-8")
            return self._send(200, data, "image/jpeg")

        if path == "/api/photolib/full":
            # 「看大图」用**原图**，不是 512 缩略图（2026-11-03 用户：点开大图也很糊）
            data = photolib_full((q.get("id", [""])[0] or ""))
            if not data:
                return self._send(404, "no image", "text/plain; charset=utf-8")
            return self._send(200, data, "image/jpeg")

        if path == "/api/status":
            return self._send(200, status())
        if path == "/api/draw/status":
            return self._send(200, draw_status())
        if path == "/api/diag":
            return self._send(200, diag())
        if path == "/api/selfcheck":
            return self._send(200, selfcheck())
        if path == "/api/health":
            return self._send(200, health())
        if path == "/api/groups":
            return self._send(200, {"items": groups_list()})
        if path == "/api/botctl":
            chat = (q.get("chat", [""])[0] or "").strip()
            return self._send(200, botctl_get(chat))
        if path == "/api/members":
            chat = (q.get("chat", [""])[0] or "").strip()
            fresh = q.get("fresh", ["0"])[0] == "1"
            if fresh:
                mm = re.match(r"^(群|私聊)\s*(\d+)$", chat)
                if mm and mm.group(1) == "群":
                    group_members(mm.group(2), fresh=True)
            return self._send(200, {"items": members_of(chat), "chat": chat})
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

        # ---- 原生聊天 App 用的 OneBot 实时接口 ----
        if path == "/api/ob/convs":
            return self._send(200, ob_convs(int(q.get("limit", ["80"])[0])))
        if path == "/api/ob/history":
            return self._send(200, ob_history((q.get("chat", [""])[0] or "").strip(),
                                             int(q.get("count", ["40"])[0])))
        if path == "/api/ob/members":
            return self._send(200, ob_members((q.get("gid", [""])[0] or "").strip()))
        if path == "/api/ob/faces":
            return self._send(200, ob_faces())
        if path == "/api/roster":
            return self._send(200, roster_all((q.get("persona", [""])[0] or "").strip()))
        if path == "/api/daily":
            gid = (q.get("gid", [""])[0] or "").strip()
            if gid:
                p = daily_file(gid)
                if not p:
                    return self._send(404, {"error": "今天还没有这个群的记录"})
                name = "%s_%s.txt" % (chat_title("群 " + gid), daily_day())
                return self._send_file(p, name)
            return self._send(200, daily_index())
        if path in ("/roster", "/roster.html"):
            p = os.path.join(STATIC, "roster.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no roster page", "text/plain; charset=utf-8")
        if path in ("/slang", "/slang.html"):
            # 群聊黑话编辑页（2026-11-01）：增删改查，按群存
            p = os.path.join(STATIC, "slang.html")
            if os.path.exists(p):
                with open(p, "rb") as f:
                    return self._send(200, f.read(), "text/html; charset=utf-8")
            return self._send(404, "no slang page", "text/plain; charset=utf-8")
        if path == "/api/logs":
            lines = last_lines("'[接收]' -e '[发送]'", since="60 min ago", limit=120)
            return self._send(200, {"lines": lines})
        if path == "/api/runlog":
            out = run(SUDO + "journalctl -u fishbot --since '40 min ago' --no-pager | tail -120", timeout=30)
            return self._send(200, {"text": out})
        if path == "/api/desktop":
            data = grab_desktop()
            if not data:
                return self._send(404, {"error": "没有截图"})
            return self._send(200, data, "image/png")
        if path == "/api/qr":
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
        path = u.path
        # 头像走原始字节，别当 JSON 解
        if path == "/api/profile/avatar/raw":
            r = profile_avatar_set_raw(raw)
            return self._send(200 if r.get("ok") else 400, r)
        try:
            body = json.loads(raw or b"{}")
        except Exception:
            body = {}
        if path == "/api/aff/set":
            # 手动改某人的好感度。走到这里说明已通过鉴权（登录 cookie 或 token）。
            r = aff_set(body.get("uid"), value=body.get("value"),
                        delta=body.get("delta"), note=body.get("note") or "",
                        persona=body.get("persona") or "")
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/whale/set":
            # 手动改鲸圆券（2026-11-05 花名册网页用）。value=设成多少 / delta=加减多少
            r = whale_set(body.get("uid"), value=body.get("value"),
                          delta=body.get("delta"), note=body.get("note") or "",
                          persona=body.get("persona") or "")
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/botctl":
            return self._send(200, botctl_set(body or {}))
        if path == "/api/perf":
            return self._send(200, perf_set(body or {}))
        if path == "/api/memes/add":
            r = memes_add(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/memes/edit":
            r = memes_edit(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/memes/del":
            r = memes_del(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/stickers/tags":
            # 表情包自定义标签（2026-10-30）：{id, mood?, desc?, role?, scene?, tags?}
            r = sticker_tags_save(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/slang":
            # 群聊黑话（2026-10-30）：整组覆盖 {"gid": "...", "items": {"词": "说明"}}
            r = slang_save(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/profile/save":
            r = profile_save(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/profile/avatar":
            r = profile_avatar_set(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/qzone/post":
            r = qzone_post(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/qzone/del":
            r = qzone_del(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/apis/save":
            r = api_profiles_save(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/apis/use":
            r = api_profiles_use(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/apis/del":
            r = api_profiles_del(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/apis/test":
            r = api_test(body or {})
            # 测试结果是"业务结论"，不是接口错误 → 一律 200，前端只看 ok/msg
            # （以前失败时返回 400，手机/WPF 上只会显示"服务器返回 400"，看不到真正原因）
            return self._send(200, r)
        if path == "/api/photolib/add":
            import base64 as _b64
            try:
                raw = _b64.b64decode(body.get("image_b64") or "")
            except Exception:
                raw = b""
            r = photolib_add(body.get("name"), body.get("desc"), raw, body.get("by") or "console",
                             tags=body.get("tags"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/autotag":
            # {enabled: bool} 开关；{last: {...}} 让 runner 回写上次结果
            r = autotag_set(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/tags_set":
            # runner 回写识别结果：{items: {id: [标签...]}}
            r = photolib_set_tags((body or {}).get("items"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/bulk_set":
            # 批量写 名字/标签/图集：{items: [{id, name?, tags?, album?}]}（2026-11-03）
            r = photolib_bulk_set((body or {}).get("items"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/clear_images":
            # 删所有图、保留标签库和图集（2026-11-03 用户要求）
            r = photolib_clear_images()
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/album/migrate":
            # 按「作品标签」把还没分集的图归到对应图集（幂等，可反复跑）
            r = photolib_migrate_albums()
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/album/new":
            r = album_new(body.get("name"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/album/rename":
            r = album_rename(body.get("id"), body.get("name"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/album/del":
            r = album_del(body.get("id"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/album/move":
            r = album_move(body.get("ids"), body.get("album"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/del":
            r = photolib_del(body.get("key"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/tag_merge":
            r = photolib_tag_merge(body.get("from"), body.get("to"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/photolib/edit":
            r = photolib_edit(body.get("id"), body.get("name"), body.get("desc"),
                              tags=body.get("tags"), album=body.get("album"),
                              lean=bool(body.get("lean")))
            return self._send(200 if r.get("ok") else 400, r)
        # ---- 画图图库：改标题/标签/备注/收藏、删除 ----
        if path == "/api/drawlib/edit":
            r = drawlib_edit(body.get("id"), title=body.get("title"), tags=body.get("tags"),
                             note=body.get("note"), fav=body.get("fav"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/drawlib/del":
            r = drawlib_del(body.get("id"))
            return self._send(200 if r.get("ok") else 400, r)
        # ---- 鱼的记忆：加 / 删 / 按词删 / 清空 / 召回测试 ----
        if path == "/api/fishmem":
            r = fishmem_action(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        # ---- 画图模板：新增/修改、删除、恢复默认 ----
        if path == "/api/draw_styles/save":
            r = draw_style_upsert(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/draw_styles/del":
            r = draw_style_del(body.get("id"))
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/draw_styles/reset":
            r = draw_styles_reset()
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/voice_style":
            return self._send(200, voice_style_set((body or {}).get("style")))
        if path == "/api/bot_blacklist":
            return self._send(200, bot_blacklist_set(body or {}))
        if path == "/api/bots":
            return self._send(200, bots_set(body or {}))
        if path == "/api/daily/collect":
            return self._send(200, daily_collect())
        if path == "/api/stickers/send":
            r = sticker_send((body.get("chat") or ""),
                             body.get("ids") or body.get("id"),
                             quote=body.get("quote"),
                             at=body.get("at") or body.get("ats"),
                             text=body.get("text") or "",
                             dry=bool(body.get("dry")))
            return self._send(200 if r.get("ok") else 400, r)
        # ---- 角色人格：切换 / 新建 / 删除 / 改人设正文 ----
        if path == "/api/personas/use":
            r = personas_use(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/personas/new":
            r = personas_new(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/persona_bindings":
            return self._send(200, persona_bindings_api(body or {}))
        if path == "/api/personas/edit":
            r = personas_edit(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/personas/del":
            r = personas_del(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/personas/prompt":
            r = personas_prompt_set(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/base_prompt/save":
            r = base_prompt_save(body or {})
            return self._send(200 if r.get("ok") else 400, r)
        if path == "/api/send":
            chat = (body.get("chat") or "").strip()
            text = body.get("text") or ""
            if body.get("voice"):
                return self._send(200, send_voice(chat, text))
            return self._send(200, send_message(
                chat, text,
                at=body.get("at") or body.get("ats"),
                quote=body.get("quote"),
                dry=bool(body.get("dry"))))
        if path == "/api/action":
            name = (body.get("name") or "").strip()
            return self._send(200, {"ok": True, "msg": action(name)})

        # ---- 原生聊天 App：发消息 / 发语音（走 OneBot，不走日志）----
        if path == "/api/ob/send":
            return self._send(200, ob_send(
                (body.get("chat") or "").strip(),
                body.get("text") or "",
                reply=str(body.get("reply") or ""),
                ats=body.get("ats") or [],
                images=body.get("images") or [],
                faces=body.get("faces") or [],
                image_b64=body.get("image_b64") or ""))
        if path == "/api/ob/recall":
            return self._send(200, ob_recall(body.get("message_id") or ""))
        if path == "/api/ob/voice":
            # 和老的 🎤 一样：服务器 TTS 合成她那把音色，再当语音发出去
            return self._send(200, send_voice((body.get("chat") or "").strip(),
                                              body.get("text") or ""))
        return self._send(404, {"error": "not found"})


def main():
    # 每日群聊归档：跟 fishweb 一起活，每 5 分钟重建一次（进程重启也不丢，journald 里还在）
    t = threading.Thread(target=daily_loop)
    t.daemon = True
    t.start()

    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print("fishweb on :%d" % PORT, flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
