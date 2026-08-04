# AcNotes.Windows hover 稳定性验证（修复后回归）
# 验证点：
#   A. hover 展开正常
#   B. 移出防抖收起正常
#   C. 可重复触发
#   D. 关键：鼠标静止停留在胶囊上 3s，不得出现 expand/collapse 交替（死循环检测）
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$exe = 'C:\temp\ac-notes\app\AcNotes.Windows.exe'
$shots = 'C:\temp\ac-notes\shots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null

# 屏幕物理 2240x1400，DPI=1.25。紧凑态窗口 DIP Left=791 宽210 → 物理中心 x = (791+105)*1.25 = 1120
$activateX = 1120; $activateY = 5
$awayX = 2000; $awayY = 1300

function Move-Mouse($x, $y) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
}
function Take-Shot($name) {
    $b = New-Object System.Drawing.Bitmap(400, 260)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(880, 0, 0, 0, $b.Size)
    $b.Save("$shots\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
    Write-Host "saved $shots\$name.png"
}

$env:DOTNET_ROOT = 'C:\dotnet'
$env:PATH = "C:\dotnet;$env:PATH"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Milliseconds 6000
if ($p.HasExited) { Write-Host "FATAL: app exited early code=$($p.ExitCode)"; exit 1 }

# A. 进入激活区 → 展开
Move-Mouse $activateX $activateY
Start-Sleep -Milliseconds 700
Take-Shot 'fix-01-expanded'
Write-Host "A. expanded"

# D. 关键死循环检测：鼠标静止停留在胶囊 3 秒（期间不得有 collapse 再 expand）
Start-Sleep -Seconds 3
Take-Shot 'fix-02-stay3s'
Write-Host "D. stayed 3s - if panel jittered, log will show expand/collapse alternation"

# B. 移出 → 收起
Move-Mouse $awayX $awayY
Start-Sleep -Milliseconds 900
Take-Shot 'fix-03-collapsed'
Write-Host "B. collapsed"

# C. 再进入 → 再展开
Move-Mouse $activateX $activateY
Start-Sleep -Milliseconds 700
Take-Shot 'fix-04-re-expanded'
Write-Host "C. re-expanded"

# 收尾
Move-Mouse $awayX $awayY
Start-Sleep -Milliseconds 800
Stop-Process -Id $p.Id -Force
Write-Host "hover stability test done"
