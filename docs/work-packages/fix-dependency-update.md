# Fix: Abhängigkeiten aktualisieren, .NET 10.0.14 (Brotli-CVE) → Release 3.66.0

Branch `fix/dependency-update` (von `main`), 2026-11-09. Anstoß: Frage des Nutzers nach relevanten Updates der eingesetzten Pakete.

## Bewertung (2026-11-09)
- `dotnet list package --vulnerable --include-transitive`: keine bekannte Schwachstelle in den Paketen.
- **.NET 10.0.14** (2026-10-25, außer der Reihe): CVE-2026-111497 (Out-of-bounds-Write im Brotli-Decoder), Nachbesserung von CVE-2026-70197 (`SocketAsyncEventArgs`) und CVE-2026-102870 (nur Blazor WebAssembly, betrifft uns nicht). Das Release ist self-contained, die Runtime steckt also in der App und erreicht die Nutzer nur mit einem neuen Release.
  - CI: `actions/setup-dotnet` v6 macht aus `global.json` (`10.0.400`, `rollForward: latestFeature`) die Anforderung `10.0` mit Mindestversion 10.0.400 und installiert damit das neueste 10.0-SDK (10.0.403 → Runtime-Pack 10.0.14). `global.json` muss dafür nicht geändert werden.
  - Lokal: SDK 10.0.401, Runtime 10.0.12 – ein Build nach `artifacts/` von diesem Rechner enthielte noch 10.0.12. Vor einem lokalen Release-Build SDK 10.0.403 installieren.
- **Angehoben:** ASP.NET Core Components.Web und Extensions.Hosting 10.0.13 → 10.0.14, Components.WebView.Wpf 10.0.110 → 10.0.120, CredentialManager 3.0.5 → 3.0.10, `Oracle.ManagedDataAccess.Core` 23.26.301 → 23.26.400, xunit.v3 4.0.2 → 4.0.3. ODP.NET 23.26.400 bringt weiter `System.Security.Cryptography.Pkcs` 8.0.1 – die Festlegung auf 8.0.2 bleibt (aufgelöst überall 8.0.2).
- **Bewusst nicht:**
  - `Microsoft.Web.WebView2` bleibt 1.0.4258.31 (ADR 0038). Die Release-Notes von SDK 1.0.4314.35 nennen keine neuen APIs (`CrashReport` an `ProcessFailedEventArgs` ist erst im Prerelease-SDK 1.0.4312). Paketvergleich: `Microsoft.Web.WebView2.Wpf.dll` gleich groß, gleiche Typ- und Membernamen, alle Ziele der Reflection in `WebViewCapture` vorhanden – nur Signatur und Versionsstempel unterscheiden sich, `WebView2Loader.dll` ist neu gebaut. Die Bugfixes von Runtime 156 kommen über die Evergreen-Runtime ohnehin. Die ab v159 angekündigte ACL-Pflicht gilt nur für App-eigene Fixed-Version-Runtimes, nicht für uns. Erst heben, wenn ein Release-SDK etwas bringt, das wir brauchen; dann den Ruhezustand testen.
  - `Microsoft.Diagnostics.Tracing.TraceEvent` 3.2.8 → 4.0.0 (Major, nur `FerretSharp.AppTrace`) und AG Grid 36.2.0 → 36.3.0 (erschienen 2026-11-04, noch keine zwei Wochen alt): jeweils eigenes Thema.
  - Wie in WP-36: Roslyn-Scripting 4.11 und EF Core Relational 8.0.0 bzw. `Caching.Memory` 8.0.1 im ModelHost (ADR 0009, 0011), EF Core 10 und `Oracle.EntityFrameworkCore` 10 in den Stand-ins.
  - Monaco 0.57.0 und Velopack/vpk 1.2.161 sind aktuell (1.3 nur Prerelease); Lucide 1.70 erst, wenn neue Icons gebraucht werden.

## Offen
- E2E-Lauf nicht gemacht: die angehobenen Pakete sind Servicing-Releases ohne API-Änderung.
- Im Release-Log prüfen, dass die CI SDK 10.0.403 und Runtime-Pack 10.0.14 gezogen hat.
