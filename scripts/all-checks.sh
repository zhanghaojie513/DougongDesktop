#!/usr/bin/env bash
# 全部确定性自检。不联网、不需要 provider。
#
# 为什么要有这个脚本：CI 与本地必须跑同一套。分两套必然漂移，
# 而漂移的方向总是"本地全绿、CI 红"。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
cd "$ROOT"

echo "== 构建 =="
"$DOTNET" build DougongDesktop.slnx -c Release

echo
echo "== 架构守卫（正向）=="
"$DOTNET" run --project tests/DougongDesktop.Architecture.Checks -c Release --no-build

echo
echo "== 架构守卫（反证：每条都必须变红）=="
# 从没红过的检查等于没有检查。这一节的价值不是"通过"，
# 而是证明"破坏架构时它真的会失败"。
failed=0
for check in app-does-not-reference-sdk sdk-side-has-no-ui-dependencies ipc-depends-on-nothing-but-bcl; do
  printf '%-36s ' "$check"
  if "$DOTNET" run --project tests/DougongDesktop.Architecture.Checks \
        -c Release --no-build -- --break="$check" > /dev/null 2>&1; then
    echo "FAIL（反证未生效：检查没有失败，说明它抓不到真违规）"
    failed=$((failed + 1))
  else
    echo "OK（正确变红）"
  fi
done

echo
if [ "$failed" -eq 0 ]; then
  echo "ALL CHECKS PASS"
else
  echo "$failed 项反证未生效"
  exit 1
fi
