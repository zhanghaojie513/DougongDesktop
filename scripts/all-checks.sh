#!/usr/bin/env bash
# 全部确定性自检。不联网、不需要 provider。
#
# 为什么要有这个脚本：CI 与本地必须跑同一套。分两套必然漂移，
# 而漂移的方向总是"本地全绿、CI 红"。
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
cd "$ROOT"

# 先杀掉运行中的本项目进程：它们会锁住 bin/ 里的 DLL，
# 导致构建报 MSB3027。而构建失败后脚本继续跑，看到的是**旧产物**——
# 那会让人误以为检查通过了。
for name in DougongDesktop.App DougongDesktop.AgentHost; do
  tasklist 2>/dev/null | grep -qi "^$name" && taskkill //F //IM "$name.exe" >/dev/null 2>&1 || true
done

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
# 架构守卫三条 + slot 框架九条。每一条都必须能被破坏后变红。
checks="app-does-not-reference-sdk sdk-side-has-no-ui-dependencies ipc-depends-on-nothing-but-bcl"
checks="$checks slot-priority-overrides-default slot-priority-tie-breaks-by-registration"
checks="$checks slot-list-preserves-register-order slot-list-observable-updates"
checks="$checks slot-keyed-dispatches-by-key slot-conversation-key-is-reserved"
checks="$checks slot-unload-leaves-no-residue slot-select-panel-rejects-missing-key"
checks="$checks slot-occupant-failure-is-isolated slot-any-name-works-for-list"
checks="$checks slot-undeclared-register-throws slot-origin-defaults-to-installed"
checks="$checks ext-origin-always-installed ext-rejects-reserved-key"
checks="$checks ext-skips-non-plugin-dll ext-missing-dir-throws"
checks="$checks ext-unload-leaves-no-residue"
checks="$checks modules-list-is-explicit-and-ordered modules-list-has-no-duplicate-slot"
checks="$checks placeholder-yields-to-plugin"
checks="$checks proj-text-delta proj-tool-requested-new-item proj-tool-id-is-deterministic"
checks="$checks proj-unknown-outcome-preserved proj-unknown-event-ignored proj-outcome-mapping"
checks="$checks resync-command-roundtrips resync-request-carries-session diag-uiplugin-has-phase"
checks="$checks tool-durations-passed-through tool-null-duration-stays-null"
checks="$checks reasoning-separate-from-text plan-replaces-wholesale plan-tool-comes-from-sdk"
checks="$checks caps-table-beats-guess max-output-never-eats-context budget-check-comes-from-sdk"
checks="$checks approval-duplicate-answer-denied approval-session-scope-is-narrow"
checks="$checks approval-silence-is-denial approval-channel-failure-denies"
checks="$checks approval-deny-and-stop-really-stops"
checks="$checks fs-list-rejects-escape dock-unimplemented-tabs-are-labeled fs-list-roundtrips"
checks="$checks ipc-every-message-is-registered"
# 行为等价：迁移后线上格式必须没变（不是"编译通过"就算完成）
checks="$checks wire-enum-format wire-null-omitted wire-cross-version-compat"
checks="$checks diff-detects-add-modify-delete diff-ignores-same-content"
checks="$checks cols-match-dsh-constants cols-sidebar-default-280 cols-rightbar-yields-first"
checks="$checks cols-rightbar-drops-before-center cols-rightbar-default-45pct"
checks="$checks cols-responsive-breakpoints color-mix-matches-css"
checks="$checks dsh-tokens-complete dsh-tokens-themes-differ titlebar-tokens-match-dsh"
checks="$checks sdk-files-stay-contained"

for check in $checks; do
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
