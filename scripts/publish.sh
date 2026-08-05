#!/usr/bin/env bash
# ac-notes-for-windows 发布脚本：双包发布
#   A 框架依赖版（~8MB）：需目标机装 .NET 8 Desktop Runtime —— 技术用户/自己用
#   B 自包含单文件压缩版（~35MB）：双击即用免装 .NET —— 普通用户
set -e
DISTRO=$(/mnt/c/Windows/System32/wsl.exe -l -q 2>/dev/null | tr -d '\0\r ' | head -1)
PROJ="\\\\wsl.localhost\\$DISTRO\\home\\lenovo\\WorkSpace\\AiProject\\ac-notes-for-windows\\src\\AcNotes.Windows"
DOTNET=/mnt/c/dotnet/dotnet.exe
PUB=/home/lenovo/WorkSpace/AiProject/ac-notes-for-windows/publish
VERSION=$(date +%Y%m%d)

echo "=== A: 框架依赖版 ==="
rm -rf "$PUB/framework-dependent"
"$DOTNET" publish -c Release -r win-x64 --self-contained false -p:DebugType=None -o "$PUB/framework-dependent" "$PROJ" 2>&1 | tail -2
cd "$PUB/framework-dependent" && zip -r -q "../ac-notes-framework-dependent-$VERSION.zip" . -x "*.pdb"
echo "A 完成: $(du -h "$PUB/ac-notes-framework-dependent-$VERSION.zip" | cut -f1)（展开 $(du -sh "$PUB/framework-dependent" | cut -f1)）"

echo "=== B: 自包含单文件压缩版 ==="
rm -rf "$PUB/singlefile"
"$DOTNET" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "$PUB/singlefile" "$PROJ" 2>&1 | tail -2
cd "$PUB/singlefile" && zip -r -q "../ac-notes-singlefile-$VERSION.zip" . -x "*.pdb"
echo "B 完成: $(du -h "$PUB/ac-notes-singlefile-$VERSION.zip" | cut -f1)（展开 $(du -sh "$PUB/singlefile" | cut -f1)）"

echo "=== 全部产物 ==="
ls -lh "$PUB"/*.zip | awk '{print $5, $9}'
