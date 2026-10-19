#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""mp3/wav → 16k/24k PCM → SILK（QQ 原生语音编码）

用法：
    to_silk.py <输入音频> <输出.silk> [采样率，默认 24000]
"""
import os
import subprocess
import sys
import tempfile

import pilk


def to_silk(src, dst, rate=24000):
    tmp = tempfile.mktemp(suffix=".pcm")
    subprocess.run(
        ["ffmpeg", "-loglevel", "error", "-y", "-i", src,
         "-ar", str(rate), "-ac", "1", "-f", "s16le", "-acodec", "pcm_s16le", tmp],
        check=True, timeout=60)
    try:
        pilk.encode(tmp, dst, pcm_rate=rate, tencent=True)
    finally:
        try:
            os.remove(tmp)
        except Exception:
            pass
    return os.path.getsize(dst)


def main():
    if len(sys.argv) < 3:
        print("用法: to_silk.py <输入> <输出.silk> [采样率]")
        return 2
    rate = int(sys.argv[3]) if len(sys.argv) > 3 else 24000
    n = to_silk(sys.argv[1], sys.argv[2], rate)
    print("OK %d bytes -> %s (%d Hz)" % (n, sys.argv[2], rate))
    return 0


if __name__ == "__main__":
    sys.exit(main())
