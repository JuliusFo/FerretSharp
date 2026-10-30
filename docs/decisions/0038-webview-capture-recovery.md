# ADR 0038: Bild der WebView nach dem Ruhezustand neu aufnehmen

- Status: akzeptiert
- Datum: 2026-10-30
- Paket: Fix `fix/webview-frozen-after-sleep`

## Kontext

Nach dem Aufwachen aus dem Ruhezustand ließ sich FerretSharp nicht mehr bedienen: Der Mauszeiger wechselte über Knöpfen noch, Klicks schienen nichts zu tun, Minimieren und Maximieren gingen; das Log zeigte nur den Verbindungsverlust des Keep-alive, das Schließen per ✕ lief normal (der UI-Thread hing also nicht).

BlazorWebView (WPF, 10.0.110) zeigt die Seite fest über das `WebView2CompositionControl` (ADR 0004; ein Schalter für das HWND-basierte `WebView2` existiert nicht). Dieses Control nimmt die Ausgabe des Browsers per `GraphicsCaptureSession` auf und kopiert jedes Bild in ein `D3DImage` (`GraphicsItemD3DImage`). In der mitgebrachten WebView2-Version 1.0.3179.45 entstehen die Direct3D-11- und -9-Geräte nur einmal im Konstruktor, der Back Buffer wird nur einmal gesetzt (`_backBufferSet`). Geht das Gerät verloren oder kommt WPFs Front Buffer zurück, bleibt das letzte Bild stehen, während die Seite weiterläuft – den Zeiger liefert der Browser über `CursorChanged` unabhängig vom Bild. Nachgelesen per `ilspycmd` im Paket.

Seit WebView2 1.0.4258 baut das Control Geräte, Aufnahme und Back Buffer nach einer Änderung der Bildschirmeinstellungen selbst neu auf (`SystemEvents.DisplaySettingsChanging/Changed` → `RecoverAfterDisplayChange`, mit Wiederholungen bei `COMException`). Ein Aufwachen ändert die Einstellungen nicht immer, und ein zurückkehrender Front Buffer setzt den Back Buffer weiterhin nicht neu.

## Entscheidung

- **WebView2 auf 1.0.4258.31 heben** (transitiv über `Directory.Packages.props`, `CentralPackageTransitivePinningEnabled`). 1.0.4314.35 war erst eine Woche alt.
- **Den Wiederaufbau des Controls selbst anstoßen** (`App/Services/WebViewCapture`): nach `PowerModes.Resume` (3 s später), wenn der Front Buffer wieder verfügbar ist, und wenn das Fenster aus dem minimierten Zustand zurückkommt – Letzteres ist der Ausweg des Nutzers, falls das Bild doch steht. Angestoßen wird es wie bei einer echten Änderung: `OnDisplaySettingsChanging` (Back Buffer freigeben), dann `OnDisplaySettingsChanged` (neu aufbauen, auf dem Dispatcher, mit den Wiederholungen des Controls).
- **Per Reflection auf private Member** (`WebView2CompositionControl._d3dImage`, `GraphicsItemD3DImage.OnDisplaySettingsChanging/Changed`). Sie werden beim Start der WebView gesucht; fehlen sie nach einem Update, steht eine Warnung im Log und es passiert sonst nichts (kein Absturz, nur kein Neustart).
- **Geprüft und verworfen:** das HWND-basierte `WebView2` (BlazorWebView bietet es nicht an; eigener Host wäre ein Nachbau von `BlazorWebView`); die ganze `BlazorWebView` neu erzeugen (neuer Scope, Komponentenzustand weg); die Größe um ein Pixel ändern, damit `_backBufferSet` zurückfällt (hilft nicht bei verlorenem Gerät); nur das Paket heben (deckt den Ruhezustand ohne Änderung der Bildschirmeinstellungen nicht).

## Folgen

- Beim Heben von `Microsoft.Web.WebView2` oder `Microsoft.AspNetCore.Components.WebView.Wpf` prüfen, dass das Log nach dem Start keine Warnung „capture cannot be restarted“ zeigt, und einmal minimieren/wiederherstellen („Restarting the WebView's capture“ im Log, Inhalt aktualisiert sich). Bringt BlazorWebView selbst eine neuere WebView2-Version mit, kann der Pin entfallen.
- Jedes Wiederherstellen aus dem minimierten Zustand nimmt das Bild neu auf (Geräte, Aufnahme, Back Buffer) – im E2E fiel dabei nichts auf.
- Den Ruhezustand selbst kann der E2E nicht auslösen; ob Resume und Front Buffer reichen, zeigt erst der Alltag (das Log nennt jeden Neustart mit Grund).
