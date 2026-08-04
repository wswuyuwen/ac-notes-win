# ac-notes-for-windows（动森变体）

macOS 动森笔记应用 [ac-notes](https://github.com/wswuyuwen) 的 Windows 移植版（基于 NotchNotes for Windows 底座换肤）：一个住在屏幕顶部的动森风格快捷笔记应用——鼠标移到顶部中央，胶囊展开成 **WYSIWYG 富文本编辑器**；移走自动收起。

## 文档

- [产品规划](docs/PRODUCT-PLAN.md)：NotchNotes 功能逻辑反推 + 动森改造规格（macOS 蓝本源码全量走读）
- [技术验证报告](docs/TECH-VALIDATION.md)：技术支柱验证结果、方案演进、实现坑清单
- [开发进度](docs/PROGRESS.md)：动森改造里程碑完成状态与验证记录

## 技术栈

.NET 8 + WPF ｜ **WebView2 + Tiptap**（WYSIWYG 编辑器，存储 HTML）｜ 全局鼠标钩子（WH_MOUSE_LL）｜ **双窗口**：主窗口 520×480 非分层 + DWM 抗锯齿圆角 ｜ 胶囊 210×36 独立分层窗口 ｜ 形态切换 = 显隐互斥 + 内容淡入（尺寸永不变化）

## 动森视觉（与 NotchNotes 的区别）

- 浅色奶油系（macOS 蓝本 Theme.swift 色板）：cream #f8f8f0 / seaBlue #98D2E3 / leaf #62bd69 / brown #725d42
- panel-bg.jpg 平铺背景 + 奶油渐变遮罩、footer-sea 页脚、标题"随手记" ZCOOL KuaiLe 字体
- 标签页 = 小胶囊（选中 sea 蓝 + 叶子徽标），编辑区大卡片圆角 24 带硬边阴影

## 开发环境

- WSL 内编译：`/mnt/c/dotnet/dotnet.exe build -c Release`（SDK 位于 C:\dotnet）
- 部署运行：`scripts/run-local.sh`（拷贝到 C:\temp\ac-notes\app 再运行，规避 UNC 崩溃）
- 运行 GUI 需 `DOTNET_ROOT=C:\dotnet`
- 详见 CLAUDE.md

## 里程碑

- [x] 底座（继承 NotchNotes）：双窗口骨架 + WebView2+Tiptap 编辑器 + 标签页 + 双通道存储 + 托盘
- [x] 动森视觉改造（2026-08-03/04）：背景/页脚/标题栏/大卡片/浅色化 + 七轮视觉精修
- [ ] 待办：sea ↔ tree 主题切换（footer-tree.png 已就位）、文件收纳 M3、打磨 M4
