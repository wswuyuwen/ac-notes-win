#!/usr/bin/env bash
# 部署到 Windows 本地路径后运行（开发期）
# 原因：exe 直接在 \\wsl.localhost UNC 上运行会因 WSL 文件系统抖动触发
# STATUS_IN_PAGE_ERROR(0xc0000006) 崩溃，必须拷贝到 Windows 本地盘运行。
set -euo pipefail
DISTRO=$(/mnt/c/Windows/System32/wsl.exe -l -q 2>/dev/null | tr -d '\0\r ' | head -1)
PROJ="\\\\wsl.localhost\\$DISTRO\\home\\lenovo\\WorkSpace\\AiProject\\ac-notes-for-windows\\src\\AcNotes.Windows"
SRC="/home/lenovo/WorkSpace/AiProject/ac-notes-for-windows/src/AcNotes.Windows/bin/Release/net8.0-windows"
APP_DIR="/mnt/c/temp/ac-notes/app"
ARGS="${1:-}"

# 1. 编译
/mnt/c/dotnet/dotnet.exe build -c Release "$PROJ" >/dev/null 2>&1

# 2. 同步到 Windows 本地目录
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"
cp -r "$SRC"/. "$APP_DIR"/
echo "deployed to C:\temp\ac-notes\app"

# 3. 运行
/mnt/c/Windows/System32/cmd.exe /c "set DOTNET_ROOT=C:\dotnet&& set PATH=C:\dotnet;%PATH%&& cd /d C:\temp\ac-notes\app&& AcNotes.Windows.exe $ARGS"
