# ADR 0003: WPF-UI als Theme, AvalonDock-Theme synchronisiert

- Status: **ersetzt** durch [0004 Blazor Hybrid](0004-blazor-hybrid-ui.md)
- Datum: 2026-10-01

## Kontext

Zur Wahl standen WPF-UI (lepoco), ModernWpf und das in .NET 9 eingeführte Fluent-`ThemeMode` von WPF. ModernWpf wird nicht mehr gepflegt, das eingebaute Fluent-Theme ist noch als experimentell markiert.

## Entscheidung

- WPF-UI (`FluentWindow`, `ThemesDictionary`, `ControlsDictionary`).
- Die App folgt der Windows-Einstellung hell/dunkel (`SystemThemeWatcher`).
- `ThemeService` setzt bei jedem Theme-Wechsel das passende AvalonDock-Theme (`Vs2013LightTheme` / `Vs2013DarkTheme`).

## Konsequenzen

- Moderne Optik ohne eigene Styles für Standard-Controls.
- AvalonDock-Themes passen farblich nur annähernd zu WPF-UI; Feinabstimmung bei Bedarf über eigene Ressourcen.
