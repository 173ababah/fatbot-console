#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""讯飞在线语音合成（WebSocket v2）→ 直接输出 mp3

用法：
    xf_tts.py <输出mp3> <文本> [发音人] [语速0-100] [音调0-100]
例：
    xf_tts.py /tmp/a.mp3 "晚安，爸爸" aisbabyxu 55 60
"""
import asyncio
import base64
import hashlib
import hmac
import json
import os
import ssl
import sys
from datetime import datetime
from email.utils import formatdate
from urllib.parse import urlencode

import websockets

APPID = os.environ.get("XF_APPID", "")
APIKEY = os.environ.get("XF_APIKEY", "")
APISECRET = os.environ.get("XF_APISECRET", "")

HOST = "tts-api.xfyun.cn"
PATH = "/v2/tts"
URL = "wss://" + HOST + PATH


def build_auth_url() -> str:
    date = formatdate(timeval=None, localtime=False, usegmt=True)
    origin = "host: %s\ndate: %s\nGET %s HTTP/1.1" % (HOST, date, PATH)
    sig = hmac.new(APISECRET.encode(), origin.encode(), hashlib.sha256).digest()
    sig_b64 = base64.b64encode(sig).decode()
    auth_origin = ('api_key="%s", algorithm="hmac-sha256", headers="host date request-line", signature="%s"'
                   % (APIKEY, sig_b64))
    auth = base64.b64encode(auth_origin.encode()).decode()
    return URL + "?" + urlencode({"authorization": auth, "date": date, "host": HOST})


async def synth(text: str, out_path: str, vcn: str = "aisbabyxu",
                speed: int = 50, pitch: int = 50, volume: int = 60) -> int:
    payload = {
        "common": {"app_id": APPID},
        "business": {
            "aue": "lame",           # 直接返回 mp3
            "sfl": 1,                # 流式
            "auf": "audio/L16;rate=16000",
            "vcn": vcn,
            "speed": speed,
            "pitch": pitch,
            "volume": volume,
            "tte": "UTF8",
        },
        "data": {
            "status": 2,
            "text": base64.b64encode(text.encode("utf-8")).decode(),
        },
    }
    total = 0
    ctx = ssl.create_default_context()
    async with websockets.connect(build_auth_url(), ssl=ctx, max_size=8 * 1024 * 1024) as ws:
        await ws.send(json.dumps(payload))
        with open(out_path, "wb") as fp:
            while True:
                raw = await asyncio.wait_for(ws.recv(), timeout=30)
                msg = json.loads(raw)
                code = msg.get("code")
                if code != 0:
                    raise RuntimeError("讯飞返回错误 %s: %s" % (code, msg.get("message")))
                data = msg.get("data") or {}
                audio = data.get("audio")
                if audio:
                    chunk = base64.b64decode(audio)
                    fp.write(chunk)
                    total += len(chunk)
                if data.get("status") == 2:
                    break
    return total


def main():
    if len(sys.argv) < 3:
        print("用法: xf_tts.py <输出mp3> <文本> [发音人] [语速] [音调]")
        return 2
    out = sys.argv[1]
    text = sys.argv[2]
    vcn = sys.argv[3] if len(sys.argv) > 3 else "aisbabyxu"
    speed = int(sys.argv[4]) if len(sys.argv) > 4 else 50
    pitch = int(sys.argv[5]) if len(sys.argv) > 5 else 50
    n = asyncio.run(synth(text, out, vcn, speed, pitch))
    print("OK %d bytes -> %s" % (n, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
