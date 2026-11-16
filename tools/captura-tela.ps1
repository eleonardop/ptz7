# Abre o PTZ Studio no computador de compilacao do GitHub e tira capturas de tela em varios tamanhos de janela.
# Serve para conferir a aparencia sem depender de um PC com Windows. Nao ha camera nem controle ligados aqui.
param([string]$Exe = "publish/PTZStudio.exe", [string]$Out = "capturas")
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
try { Set-DisplayResolution -Width 1920 -Height 1080 -Force } catch { Write-Host "Nao foi possivel trocar a resolucao: $_" }
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W]::SetProcessDPIAware() | Out-Null
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "Tela: $($scr.Width)x$($scr.Height)"

function Shot([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $scr.Width, $scr.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $g.Dispose()
  $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  return $bmp
}
function FindKnob($bmp) {
  # centro do botao azul do joystick virtual (cor de destaque do tema: 3B82F6)
  $sx = 0; $sy = 0; $n = 0
  for ($y = 0; $y -lt $bmp.Height; $y += 2) { for ($x = 0; $x -lt $bmp.Width; $x += 2) {
    $c = $bmp.GetPixel($x, $y)
    if ($c.R -eq 0x3B -and $c.G -eq 0x82 -and $c.B -eq 0xF6) { $sx += $x; $sy += $y; $n++ } } }
  if ($n -lt 200) { return $null }
  return @([int]($sx / $n), [int]($sy / $n), $n)
}

$p = Start-Process $Exe -PassThru
Start-Sleep 8
$p.Refresh(); $h = $p.MainWindowHandle
Write-Host "Janela: $h"
[W]::SetForegroundWindow($h) | Out-Null

# 1) janela padrao (1280x800), antes do alerta de camera
(Shot "01-janela-padrao-1280x800").Dispose()
# 2) notebook Full HD com escala de 125% (area util ~1536x824)
[W]::MoveWindow($h, 0, 0, 1536, 824, $true) | Out-Null; Start-Sleep 3
(Shot "02-notebook-125pct-1536x824").Dispose()
Start-Sleep 10   # tempo para a faixa "camera sem resposta" aparecer
$b = Shot "03-notebook-125pct-com-alerta"

# 3) setas de direcao e arraste (acha o botao azul na captura)
$k = FindKnob $b; $b.Dispose()
if ($k) {
  Write-Host "Botao azul em $($k[0]),$($k[1]) ($($k[2]) pontos)"
  $cx = $k[0]; $cy = $k[1]
  [W]::SetCursorPos($cx + 97, $cy) | Out-Null; Start-Sleep -Milliseconds 400
  (Shot "04-seta-direita-mouse-em-cima").Dispose()
  [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 900
  (Shot "05-seta-direita-apertada").Dispose()
  [W]::SetCursorPos($cx + 97, $cy + 70) | Out-Null; Start-Sleep -Milliseconds 700      # sai de cima da seta com o botao apertado
  (Shot "06-saiu-da-seta-ainda-apertado").Dispose()
  [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400
  [W]::SetCursorPos($cx - 69, $cy - 69) | Out-Null; Start-Sleep -Milliseconds 300
  [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 900
  (Shot "07-seta-cima-esquerda-apertada").Dispose()
  [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400
  [W]::SetCursorPos($cx, $cy) | Out-Null; Start-Sleep -Milliseconds 300
  [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
  [W]::SetCursorPos($cx + 30, $cy - 20) | Out-Null; Start-Sleep -Milliseconds 200
  [W]::SetCursorPos($cx + 70, $cy - 50) | Out-Null; Start-Sleep -Milliseconds 900
  (Shot "08-arrastando-o-centro").Dispose()
  [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 600
  (Shot "09-depois-de-soltar").Dispose()
} else { Write-Host "Botao azul do joystick nao encontrado na captura." }
[W]::SetCursorPos(5, 5) | Out-Null

# 4) Full HD maximizado, janela estreita (minimo) e janela baixa
[W]::ShowWindow($h, 3) | Out-Null; Start-Sleep 3
(Shot "10-fullhd-maximizado").Dispose()
[W]::ShowWindow($h, 9) | Out-Null; Start-Sleep 1
[W]::MoveWindow($h, 0, 0, 1366, 728, $true) | Out-Null; Start-Sleep 3
(Shot "11-notebook-1366x728").Dispose()
[W]::MoveWindow($h, 0, 0, 1000, 700, $true) | Out-Null; Start-Sleep 3
(Shot "12-janela-estreita-1000x700").Dispose()

Stop-Process -Id $p.Id -Force
Get-ChildItem $Out | ForEach-Object { Write-Host $_.Name $_.Length }
