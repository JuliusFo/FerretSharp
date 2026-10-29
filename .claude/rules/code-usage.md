---
paths:
  - "src/FerretSharp.Core/ClrModel/Usage/**"
  - "src/FerretSharp.UI/Components/Schema/UsageView*"
  - "src/FerretSharp.UI/Components/Schema/UsageBuildNote*"
  - "src/FerretSharp.UI/State/**/CodeUsage*"
  - "src/FerretSharp.App/Services/CodeEditorLauncher*"
---

# Verwendung im Code (WP-40, ADR 0030)

Aus `clr-model.md` herausgelöst (Review 3.54.0, D4): Die Fallen lud jede Datei des ModelHost, die der Verwendung erreichten sie nicht. Das C#-Modell selbst und der ModelHost: `clr-model.md`.

- Der IL-Scanner (`IlUsageScanner`) lädt nichts und braucht keine Schattenkopie: Bild komplett in den Speicher, Dateien mit `FileShare.ReadWrite | Delete`. Behalten werden nur Getter/Setter/`ldtoken` von Typen der Solution (Assembly-Namen) und `DbContext.Set<T>()` – sonst wird der Cache je MVID riesig.
- Namen wie `Type.FullName`: verschachtelt mit `+`, generisch mit `` `1 `` (Instanziierungen auf die Definition), dateilokale Typen (`<Datei>F…__Name`) mit ihrem geschriebenen Namen. Aufrufer aus Lambdas, lokalen Funktionen und Zustandsautomaten (`<Methode>d__5`) werden auf die geschriebene Methode aufgelöst.
- Mapping-Code (`IEntityTypeConfiguration<T>`, `OnModelCreating`, DbSet-Properties des Kontexts) ist kein Gebrauch: `builder.Property(x => x.Email)` ist auch ein `ldtoken`. Schlüssel und Schatten-Properties sind nie „ungenutzt“, Fremdschlüssel zählen über ihre Navigations. Was eine Spalte zählt, zeigt sie auch: Markierung, Liste und Designer gehen alle über `CodeUsageIndex.ForColumn` (3.44.0: die Liste kannte nur `ForProperty` und widersprach der Markierung).
- `ExecuteUpdate(s => s.SetProperty(x => x.Email, …))` (EF 7–9) ist ein Expression Tree: `ldtoken SetProperty` kommt vor den Argumenten, der nächste Getter-`ldtoken` ist die geschriebene Property (`Write`), danach liest der Wert-Ausdruck. Eine Änderung der Einordnung erhöht `AssemblyUses.CurrentFormatVersion`, sonst bleiben alte Cache-Einträge (je MVID) stehen.
- Filter (`CodeUseKind.Filter`): Getter aus einem Expression Tree warten auf den ersten echten Aufruf mit `Expression<T>`-Parameter, erst der entscheidet (`Where` → Filter, `Select` → LINQ). Der Parameter wird aus der Signatur gelesen; ohne diese Prüfung entschied schon `CancellationToken.None` vor `FirstOrDefaultAsync`. Hilfsaufrufe, mit denen der Compiler den Baum baut, sind zusätzlich ausgenommen (`IlUsageScanner.BuildsTrees`). Nimmt ein neuer Hilfsaufruf selbst ein `Expression<T>`, gehört er dorthin. Ein Getter auf einem Wert (`kunde.AdresseId`: der Baum setzt auf `Expression.Constant` der Closure auf, nicht auf `ldloc` des Parameters) filtert nicht, ein Baum, der direkt nach `Expression.Lambda` in eine Variable, ein Feld oder ein `ret` geht oder an einen Aufruf, der ihn als letztes Argument oder als `this` nimmt, aber ohne `Expression<T>`-Parameter (Dictionary, `List.Add`; nicht `CancellationToken.None`, nicht ein `newobj` ohne Argumente), bleibt „LINQ“ (sonst nahm das nächste `Where` die Sortierschlüssel eines `switch` mit). Ein Wert über Aufruf, Indexer oder `??` zählt noch als Filter (Grenze, Backlog).
- Visual Studio per COM: Aufrufe direkt nach `OpenFile` scheitern mit `RPC_E_SERVERCALL_RETRYLATER`, der `dynamic`-Binder verpackt das HRESULT nur im Text einer `MissingMemberException` – wiederholen. Ein als Administrator laufendes VS steht nicht in der Running Object Table eines normalen Prozesses. Nur im App-Projekt.
- Die gewählte Solution steht in `ConnectionProfile.CodeSolution`, nicht im `ClrProjectLink`: Modell, LINQ-Konsole und Watcher vergleichen den Link, eine Änderung dort lädt alles neu.
- Ob ein nicht gebautes Projekt „ungenutzt“ unsicher macht (`CodeUsageResult.Missing`), kommt aus der Projektdatei ohne MSBuild: Was sie nicht sicher verrät (Eigenschaft im Referenzpfad, `Directory.Build.props/targets`, Projekt außerhalb der Solution, `IsTestProject` unter Bedingung), zählt in die vorsichtige Richtung – lieber ein Hinweis zu viel als ein falsches „ungenutzt“. Test-Frameworks nur in `TestFrameworks` (Assembly- und Paketnamen), sonst sind gebaut und nicht gebaut verschiedener Meinung.
