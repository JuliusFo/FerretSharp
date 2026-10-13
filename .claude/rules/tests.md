---
paths:
  - "tests/**"
---

# Tests

- **xUnit v3** auf der **Microsoft Testing Platform** + NSubstitute. Kein VSTest: `Microsoft.NET.Test.Sdk` und `xunit.runner.visualstudio` nicht verwenden.
- Unit-Tests laufen ohne DB (Mocks/Fakes). Alles, was eine echte DB braucht, gehört in `FerretSharp.Integration.Tests` (Testcontainers.Oracle, `gvenzl/oracle-free:23-slim-faststart`) und muss sich ohne Docker sauber überspringen. Teile, die die .NET-8-Runtime brauchen (ModelHost, `FakeEfApp`), ebenso: Pfade dorthin über `Net8.Require`, der Container-Start fängt jeden Fehler (`OracleContainerFixture`) – Skip mit Grund, Tests ohne DB laufen weiter. In der CI (`CI=true`) überspringt nur ein fehlendes Docker, alles andere scheitert. Eine Testklasse, die in `InitializeAsync` überspringt, prüft im `DisposeAsync` auf null.
- Ordner nur über `TestFolder` (`tests/Shared`) anlegen und löschen, nie selbst rekursiv löschen (`SafeDeleteTests` durchsucht auch die Tests).
- Die Tests laufen mit deutscher UI-Kultur (`tests/Shared/UiCulture.cs`), damit ältere Tests ihre deutschen Texte prüfen; englische Texte mit `using (UiCulture.Use("en")) { … }`. `ResourceTests` (Core, UI) prüfen beide Sprachen je Schlüssel.
- Stand-ins: `FakeModelHost` (Prozessweg des ModelHost, Szenario per `--scenario`), `FakeEfApp` (App mit EF Core 8, an die sich die Integration.Tests für „Statements der App“ hängen), `FakeEfModel` (Contexts mit eigenem Connection-String für die LINQ-Konsole).

## UI.Tests
- Prüfen die State-Klassen ohne Rendern: `TestApp` verdrahtet Hub, Scopes und Fakes; `FakeDataEditor` spielt Schreiben, Undo/Redo, Commit, DDL und auf Wunsch Konflikte und gescheiterte Rollbacks, `TestApp.ReadSql` den Leseweg.
- Es gibt keinen Dispatcher wie Blazors UI-Thread: Die Session eines Workspaces öffnet beim ersten Zugriff per `Task.Run`, alles danach läuft auf dem Threadpool weiter. Ein Test, der eine Aktion startet, ohne sie abzuwarten, und dann einen Fake umstellt (`OnCommit`, `ReadSql`), wartet vorher auf ein Signal aus dem Fake („angekommen“) – sonst greift der Halt nicht, und zwei Aktionen laufen gleichzeitig über Zustand, den die App nur vom UI-Thread anfasst (so flackerte `A_running_commit_blocks_only_its_own_workspace`).
- `ConnectionServiceInjectionTests` schlägt bei `[Inject]` eines verbindungsgebundenen Dienstes an, `SettingsCatalogTests` bei einem `SettingKey` ohne Eintrag.
