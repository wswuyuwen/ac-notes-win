# AcNotes.Windows hover 交互链路验证
# 时序：启动（等编辑器就绪缩紧凑）→ 鼠标进激活区(顶部中央) → 展开 → 截图(展开态) → 移出 → 防抖收起 → 截图(紧凑态)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 正式工程（run-local.sh 部署产物）；旧验证工程 NotchWindowsPoc 已废弃
$exe = 'C:\temp\ac-notes\app\AcNotes.Windows.exe'
$shots = 'C:\temp\ac-notes\shots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null

# 屏幕物理 2240x1400，DPI=1.25。紧凑态窗口 DIP Left=(1792-210)/2=791 宽 210 → 物理中心 x = (791+105)*1.25 = 1120
$activateX = 1120; $activateY = 5
$awayX = 2000; $awayY = 1300

function Move-Mouse($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
}

function Take-Shot($name) {
    $b = New-Object System.Drawing.Bitmap(400, 260)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(880, 0, 0, 0, $b.Size)  # 截顶部中央区域(含窗口)
    $b.Save("$shots\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
    Write-Host "saved $shots\$name.png"
}

# 1. 启动应用（必须带 DOTNET_ROOT，SDK 未注册系统路径）
#    注意：启动后窗口先以展开尺寸初始化 WebView2（页面脚本需大/可见窗口），就绪后才缩紧凑态
$env:DOTNET_ROOT = 'C:\dotnet'
$env:PATH = "C:\dotnet;$env:PATH"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 6000
if ($p.HasExited) { Write-Host "FATAL: app exited early code=$($p.ExitCode)"; exit 1 }

# 2. 鼠标进入激活区 → hover 展开
Move-Mouse $activateX $activateY
Start-Sleep -Milliseconds 700   # 0.28s 展开动画 + 余量
Take-Shot '01-expanded'

# 3. 鼠标移出停留区 → 0.22s 防抖 + 0.16s 收起
Move-Mouse $awayX $awayY
Start-Sleep -Milliseconds 900
Take-Shot '02-collapsed'

# 4. 再次进入 → 再次展开（验证可重复触发）
Move-Mouse $activateX $activateY
Start-Sleep -Milliseconds 700
Take-Shot '03-re-expanded'

# 5. 退出
Move-Mouse $awayX $awayY
Start-Sleep -Milliseconds 800
Stop-Process -Id $p.Id -Force
Write-Host "hover test done"
