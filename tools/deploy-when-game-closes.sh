#!/usr/bin/env bash
# 等 SlayTheSpire2.exe 完全退出后，把最新编译的 CombatSolver.dll 复制到 mods 目录。
# 用于游戏运行期间无法覆盖 DLL 的场景（自动部署，无需人工批准）。
SRC="C:/Users/19148/Desktop/AIWithCombatSolver/CombatSolver/.godot/mono/temp/bin/Release/CombatSolver.dll"
DST="D:/steam/steamapps/common/Slay the Spire 2/mods/CombatSolver/CombatSolver.dll"
SRC_MTIME="$(stat -c %y "$SRC" 2>/dev/null)"

echo "[deploy-watcher] watching for game exit; source mtime=$SRC_MTIME"
for i in $(seq 1 200); do
    if ! tasklist //FI "IMAGENAME eq SlayTheSpire2.exe" 2>/dev/null | grep -qi "SlayTheSpire2"; then
        echo "[deploy-watcher] game process exited (poll $i). deploying..."
        # 进程可能仍在退场，稍等片刻再复制。
        sleep 3
        for attempt in 1 2 3 4 5; do
            if cp -f "$SRC" "$DST" 2>/dev/null; then
                echo "[deploy-watcher] DEPLOYED attempt=$attempt $(stat -c %y "$DST")"
                exit 0
            fi
            echo "[deploy-watcher] copy attempt $attempt failed (locked?), retrying in 5s"
            sleep 5
        done
        echo "[deploy-watcher] FAILED to copy after 5 attempts"
        exit 1
    fi
    sleep 15
done
echo "[deploy-watcher] gave up after ~50 minutes"
exit 0
