# ADR 0004: Blazor Hybrid statt WPF-Controls für die Oberfläche

- Status: akzeptiert (ersetzt [0003](0003-wpf-ui-theme.md))
- Datum: 2026-10-01

## Kontext

Die erste App-Hülle (WPF + WPF-UI + AvalonDock) wirkte uneinheitlich: AvalonDock-Themes passen nicht zum Fluent-Look, und das eingebaute WPF-`DataGrid` ist für ein datenlastiges Tool optisch und funktional das schwächste Glied. Eine DevExpress-Lizenz ist nicht vorhanden. Ein Prototyp (Branch `spike/blazor-hybrid`) mit Blazor Hybrid und AG Grid hat überzeugt: moderner Look mit wenig Aufwand, Grid mit Infinite Row Model passt genau zum serverseitigen Paging/Sortieren. Der Nutzer bringt Blazor-Know-how mit.

## Entscheidung

- **`FerretSharp.App`** ist ein schlanker WPF-Host: Generic Host, Serilog, globale Exception-Handler, Fenster-Chrome (dunkle Titelleiste per DWM), `BlazorWebView`. TFM `net10.0-windows10.0.19041.0` – das `WebView2CompositionControl` von BlazorWebView braucht die WinRT-Projektion, mit `net10.0-windows` stürzt die App beim Start ab.
- **`FerretSharp.UI`** ist eine plattformneutrale Razor Class Library (`net10.0`) mit allen Komponenten, CSS (Design-Tokens, hell/dunkel über `prefers-color-scheme`) und JS-Interop. Keine WPF-/Windows-Referenzen.
- **Grid:** AG Grid Community (MIT) über JS-Interop, Infinite Row Model; Datenblöcke und Sortierung kommen aus .NET. AG Grid wird lokal im Repo mitgeliefert (kein CDN) – Einführung in WP-04.
- **SQL-Anzeige:** eigener schlanker Highlighter in Razor; ein vollwertiger Editor (Monaco) erst mit dem freien SQL-Editor (Backlog).
- **Entfernt:** WPF-UI, AvalonDock, CommunityToolkit.Mvvm, AvalonEdit (nie eingeführt).
- JS bleibt dünn: nur Grid-Brücke, Zwischenablage, Scrollen, Fokus. Geschäftslogik ausschließlich in C#.
- `--theme=dark|light` übersteuert die Windows-Einstellung (WebView2 `PreferredColorScheme`), praktisch für Tests und Screenshots.

## Konsequenzen

- Einheitlicher, moderner Look; freie Gestaltung per CSS.
- Abhängigkeit von der WebView2-Runtime (auf Windows 10/11 vorinstalliert) und spürbar höherer Speicherbedarf als reines WPF.
- Zwei Welten (C# und JS) an der Grid-Grenze; Fokus- und Shortcut-Behandlung über die WPF/WebView-Grenze muss bewusst gelöst werden.
- UI-Komponenten lassen sich später mit bUnit testen und in einem Web-Host (Blazor Server) für die UI-Entwicklung im Browser betreiben.
