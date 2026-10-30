#!/usr/bin/env bash
# 杀掉本项目的运行进程，然后构建。
#
# 为什么需要这个脚本：Windows 上 exe/dll 被运行中的进程锁住时，
# dotnet build 报 MSB3027/MSB3021。而如果构建失败后脚本继续跑，
# 看到的是**旧产物**——本项目已因此误判过多次（"改了没生效"其实是没编译）。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
TARGET="${1:-DougongDesktop.slnx}"
CONFIG="${2:-Release}"

echo "== 停止运行中的进程 =="
# 只杀本项目的进程，不碰其他 dotnet 进程
for name in DougongDesktop.App DougongDesktop.AgentHost; do
  if tasklist 2>/dev/null | grep -qi "^$name"; then
    tasklist 2>/dev/null | grep -i "^$name" | awk '{print $2}' \
      | while read -r pid; do taskkill //F //PID "$pid" >/dev/null 2>&1 || true; done
    echo "  已停止 $name"
  fi
done

# 文件系统释放句柄需要一点时间
sleep 2

echo
echo "== 构建 $TARGET ($CONFIG) =="
cd "$ROOT"
"$DOTNET" build "$TARGET" -c "$CONFIG"
