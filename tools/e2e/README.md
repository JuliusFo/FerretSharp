# E2E helper scripts

Scripts for checking the UI end to end in an isolated test instance (`docs/e2e-testing.md` has the rules and pitfalls).
Use a scratch folder as `-Root`; nothing touches the user's data, WebView2 folder or a running instance.

```powershell
# 1. Does injected input get through on this machine? (exit code 0 = yes; otherwise use CDP input)
powershell -ExecutionPolicy Bypass -File tools/e2e/Test-InjectedInput.ps1

# 2. Build and start a test instance (prints ProcessId, Port, DataDir, Log)
powershell -ExecutionPolicy Bypass -File tools/e2e/Start-TestInstance.ps1 -Root <scratch>\e2e -Lang de

# 3. Read the page and positions via CDP (expressions in a file)
node tools/e2e/cdp.mjs 9333 eval <scratch>\find-target.js

# 4. Real Windows click at a CSS position (also -Button Right|Middle, -Clicks 2, -ToX/-ToY to drag, -MoveOnly)
powershell -ExecutionPolicy Bypass -File tools/e2e/Invoke-RealClick.ps1 -ProcessId <id> -X 188 -Y 22.5

# 5. Screenshot from the surface (also when the window is covered), optional clip in CSS pixels
node tools/e2e/cdp.mjs 9333 screenshot <scratch>\shot.png 0 0 800 600

# 6. Close the instance and count warnings/errors in its log
powershell -ExecutionPolicy Bypass -File tools/e2e/Stop-TestInstance.ps1 -Root <scratch>\e2e -ProcessId <id>
```

| Script | Purpose |
|---|---|
| `Test-InjectedInput.ps1` | Probe: injected click and key into an own window that never takes the focus |
| `Start-TestInstance.ps1` | Build to `<Root>\app`, start with own data/WebView2 folders and debug port, fixed window position |
| `Invoke-RealClick.ps1` | `SendInput` mouse through the WPF host; stops if the window is not in front or not under the point |
| `cdp.mjs` | `eval`, `calls` (several CDP calls in one connection), `screenshot` |
| `Stop-TestInstance.ps1` | Close only that instance, report `[ERR]`/`[WRN]` |
