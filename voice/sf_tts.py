#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""硅基流动（SiliconFlow）语音合成 → mp3

用法：
    sf_tts.py <输出mp3> <文本> [音色] [模型] [语速]

环境变量：
    FISH_SF_KEY    API Key（sk-...）
    FISH_SF_URL    默认 https://api.siliconflow.cn/v1/audio/speech

模型：
    FunAudioLLM/CosyVoice2-0.5B   ← 中文自然，情绪好（默认）
    fishaudio/fish-speech-1.5

音色（系统预置，CosyVoice2）：
    anna / bella / claire / diana / eileen / alex / benjamin / charles / david
    也可以用自己上传的语音：speech:你的音色名:xxxx
"""
import os
import sys
import json
import urllib.request

API = os.environ.get("FISH_SF_URL", "https://api.siliconflow.cn/v1/audio/speech")
KEY = os.environ.get("FISH_SF_KEY", "")


def shift_pitch(path, ratio):
    """变调但不变速：asetrate + 反向 atempo"""
    if abs(ratio - 1.0) < 0.01:
        return
    import subprocess
    tmp = path + ".p.mp3"
    subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-y", "-i", path,
         "-af", "asetrate=24000*%.3f,aresample=24000,atempo=%.4f" % (ratio, 1.0 / ratio),
         "-ar", "24000", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "96k", tmp],
        check=True)
    os.replace(tmp, path)


def pad_tail(path, seconds=0.45):
    """末尾补静音：QQ 转码会切掉最后一点，补上就不会「尾音没说完」"""
    import subprocess
    tmp = path + ".t.mp3"
    try:
        subprocess.run(
            ["ffmpeg", "-loglevel", "error", "-y", "-i", path,
             "-af", "apad=pad_dur=%.2f" % seconds,
             "-ar", "24000", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "96k", tmp],
            check=True, timeout=30)
        if os.path.exists(tmp) and os.path.getsize(tmp) > 1000:
            os.replace(tmp, path)
    except Exception:
        pass


def synth(text, out_path, voice="anna", model="FunAudioLLM/CosyVoice2-0.5B", speed=1.0, pitch=1.0):
    if not KEY:
        raise RuntimeError("没设置 FISH_SF_KEY")
    payload = {
        "model": model,
        "input": text,
        "voice": voice,
        "response_format": "mp3",
        "speed": speed,
    }
    req = urllib.request.Request(
        API,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={
            "Authorization": "Bearer " + KEY,
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = resp.read()
    if len(data) < 500:
        raise RuntimeError("返回内容太少：%r" % data[:200])
    with open(out_path, "wb") as fp:
        fp.write(data)
    shift_pitch(out_path, pitch)
    pad_tail(out_path, 0.45)
    return len(data)


def main():
    if len(sys.argv) < 3:
        print("用法: sf_tts.py <out.mp3> <text> [voice] [model] [speed]")
        return 2
    out = sys.argv[1]
    text = sys.argv[2]
    voice = sys.argv[3] if len(sys.argv) > 3 else "anna"
    model = sys.argv[4] if len(sys.argv) > 4 else "FunAudioLLM/CosyVoice2-0.5B"
    speed = float(sys.argv[5]) if len(sys.argv) > 5 else 1.0
    pitch = float(sys.argv[6]) if len(sys.argv) > 6 else 1.0
    n = synth(text, out, voice, model, speed, pitch)
    print("OK %d bytes -> %s" % (n, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
