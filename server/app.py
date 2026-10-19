#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""肥鱼控制台 · 手机版 Web 后端（只用标准库）

监听 0.0.0.0:8090，带 token 鉴权，给手机 APK / 浏览器用。
所有操作都在本机执行（systemctl / docker / journalctl），不需要 SSH。
"""
import base64
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

TOKEN = os.environ.get("FISHWEB_TOKEN", "fishbot-2026")
PORT = int(os.environ.get("FISHWEB_PORT", "8090"))
BOT_DIR = "/opt/qqbot"
QR_LOCAL = "/tmp/fishweb_qr.png"
DESKTOP_LOCAL = "/tmp/fishweb_desktop.png"
BOT_CONTAINER = "snowluma"          # 协议端容器名（原 napcat）

STATIC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")
HEALTH_LOG = "/opt/qqbot/logs/health.log"


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


def click_qr_refresh():
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"xdotool mousemove %s click 1\"" % (BOT_CONTAINER, QR_CLICK), timeout=25)
    time.sleep(2.5)


def grab_qr(fresh=False):
    """从 SnowLuma 容器里的 Linux QQ 窗口截屏，裁出二维码那块。"""
    if fresh:
        click_qr_refresh()
    # 把鼠标挪开，别挡在二维码上
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"xdotool mousemove 1500 950\"" % BOT_CONTAINER, timeout=20)
    run(SUDO + "docker exec -u snowluma -e DISPLAY=:1 %s sh -lc "
        "\"ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -i :1 "
        "-vf '%s' -frames:v 1 -y /tmp/fishqr.png\"" % (BOT_CONTAINER, QR_CROP), timeout=40)
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


def _parse_all(force=False):
    """解析一次日志，缓存起来（点会话列表/进单聊都复用）"""
    now = time.time()
    if not force and _MSG_CACHE["msgs"] and now - _MSG_CACHE["ts"] < MSG_TTL:
        return _MSG_CACHE["msgs"]
    lines = last_lines("'[接收]' -e '[发送]'", since=MSG_SINCE, limit=MSG_TAIL)
    # 手动发送的日志（控制台/手机发的）也要合并进来
    try:
        if os.path.exists(SENT_LOG):
            with open(SENT_LOG, "r", encoding="utf-8") as f:
                extra = f.readlines()[-400:]
            lines = list(lines) + [ln.rstrip("\n") for ln in extra]
    except Exception:
        pass
    msgs = []
    for ln in lines:
        ts = ""
        m = re.search(r"(\d{2}:\d{2}:\d{2})", ln)
        if m:
            ts = m.group(1)
        if "[接收]" in ln:
            direction = "in"
            body = ln.split("[接收]", 1)[1].strip()
        else:
            direction = "out"
            body = ln.split("[发送]", 1)[1].strip()

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

        if "【非文本消息】" in text or text.startswith("【image】") or "【表情包】" in text:
            kind = "media"
            text = "[表情]" if "表情" in text else "[图片]"
        msgs.append({"dir": direction, "chat": chat_name, "who": who,
                     "text": text[:200], "kind": kind, "ts": ts})
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
            seen[key] = {"chat": key, "count": 0, "last": "", "ts": "", "dir": ""}
            order.append(key)
        c = seen[key]
        c["count"] += 1
        c["last"] = m["text"]
        c["ts"] = m["ts"]
        c["dir"] = m["dir"]
    out = [seen[k] for k in order]
    out.sort(key=lambda x: x["ts"], reverse=True)
    return out



OB_HTTP = os.environ.get("FISH_OB_HTTP", "http://127.0.0.1:3000")
OB_TOKEN = os.environ.get("FISH_OB_TOKEN",
                          "CHANGE_ME_ONEBOT_TOKEN")


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



SENT_LOG = "/opt/qqbot/data/manual_sent.log"


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



VOICE_DIR_IN_CONTAINER = "/app/data/voice"
SF_PY = "/opt/voice/.venv/bin/python"
SF_BIN = "/opt/voice/sf_tts.py"
SF_VOICE = os.environ.get("FISH_VOICE_SF_VOICE",
                          "speech:diona:db65ukk50mis739gtebg:wtktidljanikamlvhyaw")
SF_MODEL = "FunAudioLLM/CosyVoice2-0.5B"
SF_SPEED = os.environ.get("FISH_VOICE_SF_SPEED", "1.08")
SF_PITCH = os.environ.get("FISH_VOICE_SF_PITCH", "1.00")
SF_SILK_BIN = "/opt/voice/to_silk.py"
VOICE_LANG_CONSOLE = os.environ.get("FISH_VOICE_LANG", "zh")


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


class Handler(BaseHTTPRequestHandler):
    server_version = "FishWeb/1.0"

    def log_message(self, fmt, *args):
        pass

    def _auth_ok(self, q):
        tok = (q.get("token", [""])[0] or "")
        if not tok:
            tok = self.headers.get("X-Token", "")
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
        if path == "/api/messages":
            limit = int(q.get("limit", ["120"])[0])
            chat = (q.get("chat", [""])[0] or "").strip() or None
            return self._send(200, {"messages": chat_messages(limit, chat)})
        if path == "/api/conversations":
            return self._send(200, {"items": conversations(int(q.get("limit", ["200"])[0]))})
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
        try:
            body = json.loads(raw or b"{}")
        except Exception:
            body = {}
        path = u.path
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
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print("fishweb on :%d" % PORT, flush=True)
    srv.serve_forever()


if __name__ == "__main__":
    main()
