<#
.SYNOPSIS
    Checks whether injected input (SendInput) gets through on this machine - run it before clicking in a test instance
    with real Windows input.

.DESCRIPTION
    Shows a small topmost probe window that never takes the focus, moves the cursor onto it, injects one left click
    and one F24 key, and reports what arrived: raw input (after all low-level hooks) and the window message.
    The cursor goes back to where it was; the foreground window does not change.

    Anti-cheat drivers (seen with FACEIT after a game) drop injected clicks and keys while letting moves through: the
    page then sees pointermove but no mousedown. If this probe fails, the machine filters - use CDP input instead.
    If it passes and the app still gets no mousedown, look at the app.

    Exit code 0 when click and key arrived, 1 otherwise.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/e2e/Test-InjectedInput.ps1
#>
$ErrorActionPreference = 'Stop'
$src = @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class InjectionProbe : Form {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
  [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort page, usage; public uint flags; public IntPtr target; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  // x64: type + union = 40 bytes; any other size makes SendInput fail with error 87.
  [StructLayout(LayoutKind.Explicit, Size=40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }

  const ushort VkF24 = 0x87;
  bool rawDown, rawUp, msgDown, msgUp, rawKey;

  public static void DpiAware() { SetProcessDPIAware(); }

  public InjectionProbe() {
    FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
    StartPosition = FormStartPosition.Manual; BackColor = Color.OrangeRed;
    var wa = Screen.PrimaryScreen.WorkingArea;
    Bounds = new Rectangle(wa.Right - 260, wa.Bottom - 160, 220, 120);
  }
  protected override bool ShowWithoutActivation { get { return true; } }
  // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
  protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x8; return p; } }
  protected override void WndProc(ref Message m) {
    switch (m.Msg) {
      case 0x21: m.Result = (IntPtr)3; return; // WM_MOUSEACTIVATE -> MA_NOACTIVATE
      case 0x201: msgDown = true; break;
      case 0x202: msgUp = true; break;
      case 0xFF: ReadRaw(m.LParam); break;
    }
    base.WndProc(ref m);
  }
  void ReadRaw(IntPtr handle) {
    uint size = 0; GetRawInputData(handle, 0x10000003, IntPtr.Zero, ref size, 24);
    IntPtr buf = Marshal.AllocHGlobal((int)size);
    try {
      GetRawInputData(handle, 0x10000003, buf, ref size, 24);
      int type = Marshal.ReadInt32(buf);
      if (type == 0) { ushort buttons = (ushort)Marshal.ReadInt16(buf, 24 + 4); if ((buttons & 1) != 0) rawDown = true; if ((buttons & 2) != 0) rawUp = true; }
      else if (type == 1 && (ushort)Marshal.ReadInt16(buf, 24 + 6) == VkF24) rawKey = true;
    } finally { Marshal.FreeHGlobal(buf); }
  }
  void Pump(int ms) { var end = DateTime.Now.AddMilliseconds(ms); while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }

  public string Run(out bool ok) {
    ok = false;
    Show(); Pump(200);
    var devices = new RAWINPUTDEVICE[2];
    devices[0].page = 1; devices[0].usage = 2; devices[0].flags = 0x100; devices[0].target = Handle; // mouse, RIDEV_INPUTSINK
    devices[1].page = 1; devices[1].usage = 6; devices[1].flags = 0x100; devices[1].target = Handle; // keyboard
    RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));

    POINT old; GetCursorPos(out old);
    var target = new POINT { X = Left + Width / 2, Y = Top + Height / 2 };
    try {
      SetCursorPos(target.X, target.Y);
      if (GetAncestor(WindowFromPoint(target), 2) != Handle)
        return "Not tested: another window covers the probe window.";
      var input = new INPUT[4];
      input[0].type = 0; input[0].mi.flags = 0x0002; // left down
      input[1].type = 0; input[1].mi.flags = 0x0004; // left up
      input[2].type = 1; input[2].ki.vk = VkF24;
      input[3].type = 1; input[3].ki.vk = VkF24; input[3].ki.flags = 2;
      uint sent = SendInput(4, input, 40);
      int error = Marshal.GetLastWin32Error();
      Pump(800);
      ok = msgDown && msgUp && rawKey;
      return string.Format("SendInput {0}/4 (error {1}) | click: raw input {2}, window {3} | key: raw input {4}",
        sent, error, rawDown && rawUp ? "yes" : "no", msgDown && msgUp ? "yes" : "no", rawKey ? "yes" : "no");
    } finally {
      SetCursorPos(old.X, old.Y);
      Close();
    }
  }
}
'@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Windows.Forms, System.Drawing
[InjectionProbe]::DpiAware()
$ok = $false
$report = (New-Object InjectionProbe).Run([ref]$ok)
$report
if ($ok) { 'Injected input gets through.' } else { 'Injected input is filtered or untested - use CDP input (docs/e2e-testing.md).' }
exit ([int](-not $ok))
