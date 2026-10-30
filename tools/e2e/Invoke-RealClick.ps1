<#
.SYNOPSIS
    Real Windows mouse input (SendInput) at a CSS position in a test instance: click, double click, right or middle
    click, drag or only hover. The input goes through the WPF host like the user's mouse (docs/e2e-testing.md, level 3).

.DESCRIPTION
    Take the position from getBoundingClientRect via CDP (cdp.mjs); screen = ClientToScreen(window) + CSS * DPR.
    Before any input the script brings the window to the front and stops if Windows refuses (the input would land in
    the window on top) or if another window lies under the point (a new instance on another screen). Run
    Test-InjectedInput.ps1 first: with an anti-cheat filter active only moves arrive.

    Holding the window in front gives the focus back to the last focused field; blur it via CDP first if a column
    picker could open and cover the target.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Invoke-RealClick.ps1 -ProcessId 12345 -X 188 -Y 22.5

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Invoke-RealClick.ps1 -ProcessId 12345 -X 40 -Y 300 -ToX 40 -ToY 420
#>
param(
    # Process id of the test instance (Start-TestInstance.ps1).
    [Parameter(Mandatory)][int]$ProcessId,
    # Position in CSS pixels of the page.
    [Parameter(Mandatory)][double]$X,
    [Parameter(Mandatory)][double]$Y,
    # window.devicePixelRatio of the page.
    [double]$Dpr = 1,
    [ValidateSet('Left', 'Right', 'Middle')][string]$Button = 'Left',
    # 2 = double click, 3 = triple click.
    [ValidateRange(1, 3)][int]$Clicks = 1,
    # Drags with the button held from X,Y to ToX,ToY (CSS pixels) in small steps.
    [double]$ToX = [double]::NaN,
    [double]$ToY = [double]::NaN,
    # Only moves the mouse there (hover).
    [switch]$MoveOnly
)
$ErrorActionPreference = 'Stop'
$src = @'
using System;
using System.Runtime.InteropServices;
public static class RealInput {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int size);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  // x64: type + MOUSEINPUT = 40 bytes; another size makes SendInput fail with error 87 and no click arrives.
  [StructLayout(LayoutKind.Explicit, Size=40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }
  public static void Mouse(uint flags, int dx, int dy) {
    var i = new INPUT[1]; i[0].type = 0; i[0].mi.flags = flags; i[0].mi.dx = dx; i[0].mi.dy = dy;
    if (SendInput(1, i, 40) != 1) throw new InvalidOperationException("SendInput failed, error " + Marshal.GetLastWin32Error());
  }
  // Absolute move over the whole virtual desktop (screens left of or above the primary one have negative coordinates).
  public static void MoveTo(POINT p) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    Mouse(0x8000 | 0x4000 | 0x0001, (int)((p.X - vx) * 65535L / (vw - 1)), (int)((p.Y - vy) * 65535L / (vh - 1)));
  }
}
'@
Add-Type $src
[RealInput]::SetProcessDPIAware() | Out-Null

$window = (Get-Process -Id $ProcessId).MainWindowHandle
if ($window -eq [IntPtr]::Zero) { throw "Process $ProcessId has no window." }

function ScreenPoint([double]$cssX, [double]$cssY) {
    $p = New-Object RealInput+POINT
    $p.X = [int][Math]::Round($cssX * $Dpr); $p.Y = [int][Math]::Round($cssY * $Dpr)
    [RealInput]::ClientToScreen($window, [ref]$p) | Out-Null
    if ([RealInput]::GetAncestor([RealInput]::WindowFromPoint($p), 2) -ne $window) {
        throw "Another window lies under ($($p.X), $($p.Y)) - stopped before any input."
    }
    $p
}

[RealInput]::SetForegroundWindow($window) | Out-Null
Start-Sleep -Milliseconds 300
if ([RealInput]::GetForegroundWindow() -ne $window) { throw 'Windows refused to bring the test instance to the front - stopped before any input.' }

$start = ScreenPoint $X $Y
$drag = -not [double]::IsNaN($ToX)
$end = if ($drag) { ScreenPoint $ToX $ToY }
$flags = @{ Left = 0x0002, 0x0004; Right = 0x0008, 0x0010; Middle = 0x0020, 0x0040 }[$Button]

[RealInput]::MoveTo($start)
Start-Sleep -Milliseconds 80
if (-not $MoveOnly) {
    if ($drag) {
        [RealInput]::Mouse($flags[0], 0, 0)
        for ($i = 1; $i -le 10; $i++) {
            $p = New-Object RealInput+POINT
            $p.X = $start.X + [int](($end.X - $start.X) * $i / 10); $p.Y = $start.Y + [int](($end.Y - $start.Y) * $i / 10)
            [RealInput]::MoveTo($p)
            Start-Sleep -Milliseconds 30
        }
        [RealInput]::Mouse($flags[1], 0, 0)
    } else {
        for ($i = 0; $i -lt $Clicks; $i++) {
            [RealInput]::Mouse($flags[0], 0, 0); Start-Sleep -Milliseconds 40
            [RealInput]::Mouse($flags[1], 0, 0); Start-Sleep -Milliseconds 60
        }
    }
}
Start-Sleep -Milliseconds 150
$what = if ($MoveOnly) { 'Moved' } elseif ($drag) { "Dragged ($Button) to ($($end.X), $($end.Y)) from" } else { "$Button x$Clicks at" }
"$what ($($start.X), $($start.Y)); test instance still in front: $([RealInput]::GetForegroundWindow() -eq $window)"
