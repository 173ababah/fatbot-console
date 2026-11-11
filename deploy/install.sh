#!/usr/bin/env bash
# fatbot 一键安装脚本（**在云服务器上跑**，Ubuntu 20.04+ / Debian 11+）
#
# 目标：一台干净的云服务器，只要给了「QQ 号 + SSH」，跑完这个脚本就能：
#   1. 建好 venv 并装齐 nonebot2 + 各插件
#   2. 生成 .env / bot.py / pyproject.toml / 人设正文
#   3. 建两个 systemd 服务：<name>（nonebot）+ <name>web（控制台后端）
#   4. 启动并自检，最后打印控制台地址
#
# 用法（root 或 sudo）：
#   sudo bash install.sh --qq 123456789 --token 面板口令 [选项]
#
# 选项：
#   --qq <QQ号>            机器人自己的 QQ（必填）
#   --admin <QQ号>         超管 QQ（可多个，逗号分隔；默认同 --qq）
#   --token <口令>         控制台口令（默认随机生成）
#   --name <名字>          服务/目录名（默认 fishbot）
#   --prefix <目录>        安装目录（默认 /opt/<name>）
#   --ai-base/--ai-model/--ai-key   文字模型渠道（可留空，之后在控制台里配）
#   --ws <url>             协议端 WS 地址（默认 ws://127.0.0.1:3000/onebot/v11/ws）
#   --port <端口>          控制台后端端口（默认 8090）
#   --plugins "<列表>"     额外 pip 安装的插件（空格分隔，默认见 DEFAULT_PLUGINS）
#   --with-napcat          装 NapCat（Docker）作为协议端
#   --no-systemd           不写/不启 systemd（测试用）
#   --dry-run              只打印将要做的事，不改系统
#
# 幂等：重复跑只会更新代码和配置，不会重建 venv、不会覆盖已存在的 .env（除非 --force-env）。
set -euo pipefail

QQ=""
ADMIN=""
TOKEN=""
NAME="fishbot"
PREFIX=""
AI_BASE=""
AI_MODEL=""
AI_KEY=""
WS="ws://127.0.0.1:3000/onebot/v11/ws"
PORT="8090"
PLUGINS=""
WITH_NAPCAT=0
NO_SYSTEMD=0
DRY=0
FORCE_ENV=0

DEFAULT_PLUGINS="nonebot-plugin-fishpersona nonebot-plugin-fishmemory nonebot-plugin-fishslang \
nonebot-plugin-fishwallet nonebot-plugin-fishmenu nonebot-plugin-fishparser \
nonebot-plugin-fishmusic nonebot-plugin-fishmeme nonebot-plugin-fishqzone"

while [ $# -gt 0 ]; do
  case "$1" in
    --qq) QQ="$2"; shift 2 ;;
    --admin) ADMIN="$2"; shift 2 ;;
    --token) TOKEN="$2"; shift 2 ;;
    --name) NAME="$2"; shift 2 ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    --ai-base) AI_BASE="$2"; shift 2 ;;
    --ai-model) AI_MODEL="$2"; shift 2 ;;
    --ai-key) AI_KEY="$2"; shift 2 ;;
    --ws) WS="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --plugins) PLUGINS="$2"; shift 2 ;;
    --with-napcat) WITH_NAPCAT=1; shift ;;
    --no-systemd) NO_SYSTEMD=1; shift ;;
    --dry-run) DRY=1; shift ;;
    --force-env) FORCE_ENV=1; shift ;;
    -h|--help) sed -n '2,40p' "$0"; exit 0 ;;
    *) echo "未知参数：$1（--help 看用法）" >&2; exit 2 ;;
  esac
done

[ -n "$PREFIX" ] || PREFIX="/opt/$NAME"
[ -n "$ADMIN" ] || ADMIN="$QQ"
[ -n "$PLUGINS" ] || PLUGINS="$DEFAULT_PLUGINS"
VENV="$PREFIX/.venv"
RUN_USER="${SUDO_USER:-$(id -un)}"

say()  { printf '\033[36m▸ %s\033[0m\n' "$*"; }
ok()   { printf '\033[32m✓ %s\033[0m\n' "$*"; }
warn() { printf '\033[33m! %s\033[0m\n' "$*"; }
die()  { printf '\033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }
run()  { if [ "$DRY" = 1 ]; then echo "  [dry] $*"; else "$@"; fi; }

[ -n "$QQ" ] || die "缺少 --qq <机器人QQ号>"
echo "$QQ" | grep -qE '^[0-9]{5,12}$' || die "--qq 看起来不是 QQ 号：$QQ"

if [ "$(id -u)" != 0 ] && [ "$DRY" = 0 ]; then
  die "请用 root 或 sudo 跑（要装包、写 systemd）"
fi

if [ -z "$TOKEN" ]; then
  TOKEN="$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 16)"
  GENERATED_TOKEN=1
fi

cat <<EOF

  fatbot 安装
  ────────────────────────────────────────
  机器人 QQ   : $QQ
  超管        : $ADMIN
  安装目录    : $PREFIX
  运行用户    : $RUN_USER
  控制台端口  : $PORT
  协议端 WS   : $WS
  NapCat      : $([ "$WITH_NAPCAT" = 1 ] && echo 装 || echo 跳过)
  systemd     : $([ "$NO_SYSTEMD" = 1 ] && echo 跳过 || echo 启用)
  ────────────────────────────────────────

EOF

# ---------------------------------------------------------------- 1. 系统依赖
say "1/7 系统依赖"
if [ "$DRY" = 0 ]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq python3 python3-venv python3-pip git curl ca-certificates ffmpeg >/dev/null
fi
ok "python3 $(python3 -V 2>/dev/null | awk '{print $2}')"

# ---------------------------------------------------------------- 2. 目录 + venv
say "2/7 目录与虚拟环境"
run mkdir -p "$PREFIX/src" "$PREFIX/data" "$PREFIX/logs"
if [ ! -x "$VENV/bin/python" ]; then
  run python3 -m venv "$VENV"
else
  ok "venv 已存在，复用"
fi
run "$VENV/bin/pip" install -q -U pip wheel

# ---------------------------------------------------------------- 3. 依赖 + 插件
say "3/7 装 nonebot2 与插件"
run "$VENV/bin/pip" install -q "nonebot2>=2.3" "nonebot-adapter-onebot>=2.4"
# ⚠️ 别用 `pip install git+https://…`：腾讯云/部分机房到 github.com 的 **git 端点**会被卡到超时，
# 但 codeload（zip 归档）是通的。所以统一走 archive zip。
for p in $PLUGINS; do
  case "$p" in
    git+*|http*|./*|/*|*.whl|*=*) spec="$p" ;;
    *) spec="https://github.com/173ababah/$p/archive/refs/heads/main.zip" ;;
  esac
  run "$VENV/bin/pip" install -q --no-cache-dir "$spec" || warn "装 $p 失败（其他插件不受影响，日志见上）"
done
ok "插件安装完成"

# ---------------------------------------------------------------- 4. 配置
say "4/7 写配置"
ENV_FILE="$PREFIX/.env"
if [ -f "$ENV_FILE" ] && [ "$FORCE_ENV" = 0 ]; then
  ok ".env 已存在，保留（要重写加 --force-env）"
else
  if [ "$DRY" = 1 ]; then
    echo "  [dry] 写 $ENV_FILE"
  else
    umask 027
    cat > "$ENV_FILE" <<ENVEOF
# ---- NoneBot 基础 ----
HOST=127.0.0.1
PORT=8080
DRIVER=~fastapi+~httpx+~websockets
LOG_LEVEL=INFO
SUPERUSERS=["$ADMIN"]
NICKNAME=["小鱼"]
COMMAND_START=["/"]
COMMAND_SEP=["."]
ONEBOT_WS_URLS=["$WS"]
SESSION_EXPIRE_TIMEOUT=86400

# ---- 文字模型（留空就先用词库，之后在控制台里配）----
FISH_AI_ENABLED=$([ -n "$AI_KEY" ] && echo 1 || echo 0)
FISH_AI_BASE_URL=${AI_BASE}
FISH_AI_MODEL=${AI_MODEL}
FISH_AI_KEY=${AI_KEY}
FISH_AI_TIMEOUT=60
FISH_AI_MAX_TOKENS=300
FISH_AI_HISTORY=16
FISH_AI_TEMPERATURE=0.75
FISH_AI_REASONING=none

# ---- 各插件（默认全开，缺依赖的会在日志里说）----
FISHMEM_ENABLED=true
FISHSLANG_ENABLED=true
FISHWALLET_ENABLED=true
FISHMENU_ENABLED=true
FISHVOICE_ENABLED=true
FISHLIB_ENABLED=true
FISHDRAW_ENABLED=false
FISHPARSER_ENABLED=true
FISHMUSIC_ENABLED=true

# ---- 控制台后端 ----
FISHWEB_PORT=$PORT
FISHWEB_TOKEN=$TOKEN
ENVEOF
    chown "$RUN_USER":"$RUN_USER" "$ENV_FILE" 2>/dev/null || true
    chmod 640 "$ENV_FILE"
  fi
fi

# bot.py
if [ "$DRY" = 1 ]; then
  echo "  [dry] 写 $PREFIX/bot.py"
else
  cat > "$PREFIX/bot.py" <<'PYEOF'
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""fatbot 入口：NapCat(OneBot v11) <-> NoneBot2。由 install.sh 生成。"""
import os

import nonebot
from nonebot.adapters.onebot.v11 import Adapter as ONEBOT_V11

os.chdir(os.path.dirname(os.path.abspath(__file__)))
nonebot.init()
nonebot.get_driver().register_adapter(ONEBOT_V11)
nonebot.load_from_toml("pyproject.toml")

if __name__ == "__main__":
    nonebot.run()
PYEOF
fi

# pyproject.toml：plugin_dirs，同时给 nonebot 和 setuptools 用
if [ "$DRY" = 1 ]; then
  echo "  [dry] 写 $PREFIX/pyproject.toml"
else
  cat > "$PREFIX/pyproject.toml" <<'TOMLEOF'
[tool.nonebot]
plugin_dirs = ["src"]

[project]
name = "fatbot-site"
version = "0.1.0"
requires-python = ">=3.9"
TOMLEOF
fi

# 人设正文：默认给一段能跑的，之后在控制台里改
if [ ! -f "$PREFIX/persona_prompt.txt" ]; then
  if [ "$DRY" = 1 ]; then echo "  [dry] 写默认人设"; else
    cat > "$PREFIX/persona_prompt.txt" <<'PROMPTEOF'
你叫小鱼，是住在这个 QQ 里的一只小女生。

说话方式：
- 短句为主，一次一两句话，别长篇大论
- 用「我」自称，偶尔用「人家」，但别每句都用
- 不用 emoji 表情符号堆砌，不用书面语，别像客服
- 别人问你问题就正常回答；不想答就转移话题，别硬答

边界：
- 不自称 AI、助手、模型；别人问你是不是机器人，就含糊带过
- 不主动要钱、不要个人信息
- 群聊里不要抢话，被 @ 或叫到名字才说
PROMPTEOF
  fi
fi

# 控制台后端：从仓库里的一份 app.py 拷过来（没有就跳过，只装机器人本体）
WEB_SRC="$(dirname "$0")/app.py"
if [ -f "$WEB_SRC" ]; then
  run mkdir -p "$PREFIX/webui"
  run cp "$WEB_SRC" "$PREFIX/webui/app.py"
  [ -d "$(dirname "$0")/static" ] && run cp -r "$(dirname "$0")/static" "$PREFIX/webui/" || true
  if [ "$DRY" = 1 ]; then echo "  [dry] 写 $PREFIX/webui/config.json"; else
    cat > "$PREFIX/webui/config.json" <<JSONEOF
{"port": $PORT, "token": "$TOKEN", "bot_dir": "$PREFIX", "service": "$NAME"}
JSONEOF
  fi
else
  warn "没带 app.py，跳过控制台后端（只装机器人）"
fi
# ⚠️ 脚本是 root 跑的（要写 systemd），但服务是以 $RUN_USER 运行的 ——
#    这里必须把整个安装目录交还给运行用户，否则 webui/ 这类目录会 root:750，服务读不了。
if [ "$DRY" = 0 ] && [ "$RUN_USER" != "root" ]; then
  chown -R "$RUN_USER":"$RUN_USER" "$PREFIX" 2>/dev/null || true
  chmod 640 "$ENV_FILE" 2>/dev/null || true
  chmod 755 "$PREFIX" "$PREFIX/webui" 2>/dev/null || true
fi
ok "配置写好：$ENV_FILE"

# ---------------------------------------------------------------- 5. systemd
say "5/7 systemd 服务"
if [ "$NO_SYSTEMD" = 1 ]; then
  warn "跳过（--no-systemd）"
elif [ "$DRY" = 1 ]; then
  echo "  [dry] 写 /etc/systemd/system/$NAME.service 与 ${NAME}web.service，并 enable --now"
else
  cat > "/etc/systemd/system/$NAME.service" <<UNIT
[Unit]
Description=fatbot ($NAME, NoneBot2 + OneBot v11)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$PREFIX
ExecStart=$VENV/bin/python $PREFIX/bot.py
Restart=on-failure
RestartSec=5
Environment=PYTHONUNBUFFERED=1

[Install]
WantedBy=multi-user.target
UNIT
  if [ -f "$PREFIX/webui/app.py" ]; then
    cat > "/etc/systemd/system/${NAME}web.service" <<UNIT
[Unit]
Description=fatbot console web ($PORT)
After=network.target

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$PREFIX/webui
Environment=FISHWEB_PORT=$PORT
Environment=FISHWEB_TOKEN=$TOKEN
ExecStart=/usr/bin/python3 $PREFIX/webui/app.py
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
UNIT
  fi
  systemctl daemon-reload
  systemctl enable --now "$NAME" >/dev/null 2>&1 || warn "$NAME 启动失败，看 journalctl -u $NAME"
  [ -f "/etc/systemd/system/${NAME}web.service" ] && \
    systemctl enable --now "${NAME}web" >/dev/null 2>&1 || true
fi

# ---------------------------------------------------------------- 6. NapCat
say "6/7 协议端（NapCat）"
if [ "$WITH_NAPCAT" = 0 ]; then
  cat <<'TIP'
  跳过。要让机器人真连上 QQ，还需要一个 OneBot v11 协议端，二选一：
    · 本机装 NapCat：bash install.sh ... --with-napcat   （需要 Docker）
    · 已有协议端：把 --ws 指过去，例如 --ws ws://1.2.3.4:3000/onebot/v11/ws
TIP
elif ! command -v docker >/dev/null 2>&1; then
  warn "没有 Docker。先装 Docker 再来：curl -fsSL https://get.docker.com | sh"
else
  run docker pull -q mlikiowa/napcat-docker:latest || warn "拉不动镜像（网络/代理？）"
  run docker rm -f napcat >/dev/null 2>&1 || true
  run docker run -d --name napcat --restart always \
      -e NAPCAT_ACCOUNT="$QQ" \
      -e WEBUI_TOKEN="$TOKEN" \
      -v "$PREFIX/napcat:/app/napcat/config" \
      -p 127.0.0.1:3000:3000 -p 6099:6099 \
      mlikiowa/napcat-docker:latest
  ok "NapCat 起来了：WebUI http://<服务器>:6099（token 同控制台）"
fi

# ---------------------------------------------------------------- 7. 自检
say "7/7 自检"
if [ "$DRY" = 1 ]; then
  echo "  [dry] 自检略"
  # 干跑也要给机器可读标记：setup.ps1 靠它们判断成败
  _ip="$(curl -s -m 5 ifconfig.me || hostname -I 2>/dev/null | awk '{print $1}')"
  echo
  echo "FATBOT_INSTALL_OK"
  echo "FATBOT_WEB=http://${_ip:-127.0.0.1}:$PORT/?token=$TOKEN"
  echo "FATBOT_DIR=$PREFIX"
  echo "FATBOT_SERVICE=$NAME"
  echo "FATBOT_DRY=1"
  exit 0
fi
"$VENV/bin/python" - <<'PYEOF' || die "nonebot 导入失败，看上面报错"
import nonebot
from nonebot.adapters.onebot.v11 import Adapter
print("nonebot", nonebot.__version__ if hasattr(nonebot, "__version__") else "ok", "adapter ok")
PYEOF
sleep 2
if [ "$NO_SYSTEMD" = 1 ]; then
  ok "跳过服务检查（--no-systemd）"
  BOT_ACTIVE="skipped"; WEB_ACTIVE="skipped"
else
  BOT_ACTIVE="$(systemctl is-active "$NAME" 2>/dev/null || true)"
  WEB_ACTIVE="$(systemctl is-active "${NAME}web" 2>/dev/null || true)"
  [ "$BOT_ACTIVE" = "active" ] && ok "$NAME: active" || warn "$NAME: $BOT_ACTIVE（journalctl -u $NAME -n 50 看原因）"
fi

IP="$(curl -s -m 5 ifconfig.me || hostname -I | awk '{print $1}')"
cat <<EOF

  ────────────────────────────────────────
  ✓ 装完了

  控制台（浏览器/手机都能开）：
      http://$IP:$PORT/?token=$TOKEN

  下一步：
    · 打开上面地址 → 「扫码」页 → 用机器人自己的 QQ 扫一下登录
    · 想换人设：改 $PREFIX/persona_prompt.txt 后 systemctl restart $NAME
    · 想配 AI：控制台「API 渠道」填 base/model/key（没填就用词库聊）
    · 日志：journalctl -u $NAME -f
  ────────────────────────────────────────
FATBOT_INSTALL_OK
FATBOT_WEB=http://$IP:$PORT/?token=$TOKEN
FATBOT_DIR=$PREFIX
FATBOT_SERVICE=$NAME
EOF

if [ "${GENERATED_TOKEN:-0}" = "1" ]; then
  warn "控制台口令是随机生成的，记下来：$TOKEN"
fi
if [ "$WEB_ACTIVE" != "active" ] && [ "$WEB_ACTIVE" != "skipped" ] && [ -f "$PREFIX/webui/app.py" ]; then
  warn "控制台后端没起来（${NAME}web: $WEB_ACTIVE），但机器人本体不受影响"
fi


