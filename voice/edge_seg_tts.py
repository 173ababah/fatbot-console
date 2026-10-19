#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Edge TTS 分段合成：支持 *重音* 标记、段间停顿、整体变速

用法：
    edge_seg_tts.py <输出mp3> <文本> <voice> <rate> <pitch> [重音加强比例]

文本里用 *词* 包住要重读的部分（星号不会念出来）。
实现：按标点和重音切段 → 每段用不同 rate/pitch/volume 合成 → 段间插静音 → ffmpeg 拼接。
"""
import asyncio
import os
import re
import subprocess
import sys
import tempfile

import edge_tts

# 停顿和重音都可以从命令行调（第 6、7 个参数）
PAUSE_SCALE = 1.0     # 0 = 不加任何人工静音（全靠 TTS 自己的语气）
EMPH_RATE = "-12%"    # 重音段：只放慢，不改音调（改音调会"变声"）
EMPH_PITCH = "+0Hz"
EMPH_VOL = "+14%"

# 断句：保留标点在段尾
SPLIT_RE = re.compile(r"([^，。！？；、,.!?;…\n]*[，。！？；、,.!?;…\n]?)")
# 强调标记
EMPH_RE = re.compile(r"\*([^*]{1,12})\*")


def split_segments(text: str):
    """切成 [(片段文本, 段后停顿秒数, 是否重音)]"""
    out = []
    for chunk in re.findall(r"[^，。！？；、,.!?;…\n]+[，。！？；、,.!?;…\n]?", text):
        chunk = chunk.strip()
        if not chunk:
            continue
        tail = chunk[-1]
        if PAUSE_SCALE <= 0:
            pause = 0.0
        else:
            base = 0.22 if tail in "。！？.!?" else (0.09 if tail in "，、,;；…" else 0.06)
            pause = base * PAUSE_SCALE
        # 段内可能带 *重音*，再拆
        pos = 0
        for m in EMPH_RE.finditer(chunk):
            pre = chunk[pos:m.start()]
            if pre.strip():
                out.append((pre, 0.05, False))
            out.append((m.group(1), 0.05, True))
            pos = m.end()
        rest = chunk[pos:]
        if rest.strip():
            out.append((rest, pause, False))
        elif out:
            t, _, e = out[-1]
            out[-1] = (t, pause, e)
    if not out:
        out = [(EMPH_RE.sub(r"\1", text), 0.0, False)]
    return out


async def synth_one(text, out_path, voice, rate, pitch, volume="+0%"):
    communicate = edge_tts.Communicate(text, voice, rate=rate, pitch=pitch, volume=volume)
    with open(out_path, "wb") as fp:
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                fp.write(chunk["data"])
    return os.path.getsize(out_path)


def make_silence(path, seconds):
    subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i",
         "anullsrc=r=24000:cl=mono", "-t", "%.3f" % seconds, "-q:a", "9", "-y", path],
        check=True)


def concat(parts, out_path):
    lst = out_path + ".txt"
    with open(lst, "w", encoding="utf-8") as fp:
        for p in parts:
            fp.write("file '%s'\n" % p.replace("'", "'\\''"))
    # 输出统一做响度标准化，手机外放才听得清
    subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-f", "concat", "-safe", "0",
         "-i", lst, "-af", "loudnorm=I=-15:TP=-1.2:LRA=11",
         "-ar", "24000", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "64k",
         "-y", out_path],
        check=True)
    os.remove(lst)


async def main():
    if len(sys.argv) < 6:
        print("用法: edge_seg_tts.py <out.mp3> <text> <voice> <rate> <pitch>")
        return 2
    out_path = sys.argv[1]
    text = sys.argv[2]
    voice = sys.argv[3]
    rate = sys.argv[4]
    pitch = sys.argv[5]
    global PAUSE_SCALE, EMPH_PITCH
    if len(sys.argv) > 6:
        PAUSE_SCALE = float(sys.argv[6])
    if len(sys.argv) > 7:
        EMPH_PITCH = sys.argv[7]

    segs = split_segments(text)
    tmpdir = tempfile.mkdtemp(prefix="edge_seg_")
    parts = []
    idx = 0
    for seg_text, pause, emph in segs:
        clean = EMPH_RE.sub(r"\1", seg_text).strip()
        if not clean:
            continue
        idx += 1
        p = os.path.join(tmpdir, "s%03d.mp3" % idx)
        r = EMPH_RATE if emph else rate
        pi = EMPH_PITCH if emph else pitch
        vol = EMPH_VOL if emph else "+0%"
        try:
            size = await synth_one(clean, p, voice, r, pi, vol)
        except Exception as e:
            print("段合成失败 %r: %r" % (clean, e))
            continue
        if size < 200:
            continue
        parts.append(p)
        if pause > 0:
            sp = os.path.join(tmpdir, "g%03d.mp3" % idx)
            make_silence(sp, pause)
            parts.append(sp)

    if not parts:
        print("没有任何音频生成")
        return 1
    concat(parts, out_path)
    total = os.path.getsize(out_path)
    print("OK %d bytes, %d 段 -> %s" % (total, idx, out_path))
    for p in parts:
        try:
            os.remove(p)
        except Exception:
            pass
    try:
        os.rmdir(tmpdir)
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
