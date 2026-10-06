# ADR 0015: Autovervollständigung in der LINQ-Konsole – Semantic Model im Hilfsprozess

- Status: akzeptiert
- Datum: 2026-10-06

## Kontext

WP-19: Die LINQ-Konsole (WP-13, ADR 0011) hatte Syntax-Hervorhebung über Monacos C#-Grammatik, aber keine Vorschläge außer Monacos Wörtern aus dem Text. Wer Queries schreibt statt sie zu kopieren, braucht die DbSets, Properties, Enum-Member und die LINQ-/EF-Methoden des eigenen Projekts. Diese Typen kennt nur der Hilfsprozess: Er lädt das Projekt und kompiliert die Scripts mit Roslyn gegen dessen Assemblies. Dort ist Roslyn auf 4.11 festgelegt (ab 4.12 nicht neben der .NET-8-Runtime ladbar, ADR 0011).

## Entscheidung

- **Vorschläge berechnet der Hilfsprozess** über eine weitere Anfrage im Pipe-Protokoll (`LinqProtocol.Complete`: Code, Variablen, Abschnitt, Cursor-Offset → `LinqCompletionItem`s). Das Script wird gebaut wie beim Ausführen; Kontext und Token werden automatisch deklariert, wenn der Code sie unbekannt benutzt (`_context.` in kopiertem Code bietet die DbSets an). Geprüft wird nur das Binden dieser Namen, keine vollständige Diagnose.
- **Semantic Model statt Completion-Service der IDE:** `Microsoft.CodeAnalysis.Features` würde weitere Assemblies (MEF, Workspaces) in den Prozess des Projekts bringen, in dem die Versionen schon eng sind. `LinqCompletion` fragt `LookupSymbols` / `LookupStaticMembers` / `LookupNamespacesAndTypes` an der Cursorposition:
  - nach `x.` die Member des Typs von x samt Extension-Methoden (`includeReducedExtensionMethods`), bei unfertigen Lambdas über den Typ des Parameters;
  - nach `Typ.` die statischen Member, bei Enums nur die Enum-Member (wie Visual Studio);
  - in `new T { … }` die setzbaren, noch nicht gesetzten Properties;
  - sonst Variablen und Parameter, die Typen aus den Namespaces des Modells, einige .NET-Typen (`DateTime`, `Guid`, `List<T>`, `EF` …) und C#-Schlüsselwörter. Bewusst nicht alle Typen der importierten Namespaces (Tausende Einträge bei jedem neuen Wort).
  - Nichts in Strings und Kommentaren und beim Benennen neuer Variablen. `[EditorBrowsable(Never)]` bleibt verborgen, Überladungen sind ein Eintrag („(+2)“).
- **Reihenfolge:** eigene Member des Typs, geerbte, Extension-Methoden, Member von `object`.
- **Vorschläge warten nie** auf einen Start oder eine laufende Ausführung (`LinqConsoleService.CompleteAsync` liefert dann eine leere Liste). Ist das Projekt seit dem Start neu gebaut, startet der Hilfsprozess im Hintergrund neu (wie sonst beim nächsten Ausführen); einen gestoppten oder gescheiterten startet erst das Ausführen bzw. das Öffnen eines LINQ-Tabs. Wie beim Ausführen gilt ein Timeout (10 s), danach startet der Prozess neu.
- Monaco: derselbe Completion-Provider wie im SQL-Editor (`MonacoEditor.CompletionItem` mit der Art als Text), für beide Editoren der Konsole (Variablen und Query).

## Konsequenzen

- Jede Anfrage kompiliert das Script ein- bis zweimal (beim Beispielprojekt um 100 ms). Bei sehr großen Projekten kann das spürbar werden; ein Cache der Compilation ist möglich, wenn nötig.
- Keine Hover-Infos, keine Parameterhilfe, keine Snippets – mit demselben Semantic Model machbar (Backlog).
- Während des Neustarts nach einem neuen Build (beim Nutzer ~12 s, ohne Modell-Cache) gibt es keine Vorschläge.
