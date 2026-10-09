# ADR 0017: Oberfläche auf Englisch und Deutsch über typisierte resx-Ressourcen

- Status: akzeptiert
- Datum: 2026-10-09
- Paket: WP-29

## Kontext

Die Oberfläche war nur deutsch, README, CHANGELOG und Screenshots auf GitHub dagegen englisch. Der Nutzer hat entschieden (Roadmap WP-29): Englisch und Deutsch, **Standard Englisch** (auch für bestehende Installationen), Deutsch in den Einstellungen wählbar; Enum-Anzeigenamen aus dem C#-Projekt (`[Display]`) bleiben in der Windows-Sprache, weil das Projekt des Nutzers nur deutsche Ressourcen hat; die Formatkultur (Zahlen, Daten, auch beim Parsen von Eingaben) bleibt vorerst unverändert.

Die Texte stecken nicht nur in Razor-Komponenten, sondern auch in State-Klassen der UI (ohne DI erzeugt, z. B. `TableTab`), im Core (Ablehnungen, Validierung, Erklärungen zu Abweichungen) und im ModelHost (Fehler und Ladeschritte). Grob 1.300–1.500 Texte.

Erwogen:

1. **`IStringLocalizer<T>`** (Microsoft.Extensions.Localization), wie in der Roadmap vorgeschlagen. Braucht DI: Jede State-Klasse und jede Core-Klasse mit Meldungen bekäme einen Localizer injiziert, auch Klassen, die heute ohne DI entstehen. Schlüssel sind Strings – ein Tippfehler fällt erst zur Laufzeit auf (der Localizer gibt den Schlüssel zurück). Neue Paketabhängigkeit in UI und Core.
2. **Typisierte resx-Ressourcen, vom MSBuild-SDK erzeugt** (`GenerateResource` mit `StronglyTypedLanguage`): statischer Zugriff `ConnectionText.NameMissing` überall gleich (Core, UI-State, Razor, ModelHost), ohne DI. Ein fehlender Schlüssel ist ein Compilerfehler. Keine neue Abhängigkeit; die Klasse entsteht beim Build in `obj` (auch unter Linux), nichts Generiertes im Repo.
3. **Source-Generator-Paket** für resx (z. B. `Microsoft.CodeAnalysis.ResxSourceGenerator`): wie 2, aber mit neuer Abhängigkeit und ohne Vorteil für dieses Projekt.
4. **Eigenes JSON-Format** für Texte: eigener Lader, keine Werkzeugunterstützung (resx-Editor in Visual Studio, Übersetzungsplattformen wie Weblate lesen resx). Verworfen.

## Entscheidung

Weg 2.

- `Directory.Build.targets` erzeugt für jede `Resources/*Text.resx` (nur die neutrale Datei; `FooText.de.resx` passt nicht auf das Muster) eine öffentliche Klasse `<RootNamespace>.Resources.<Name>`. Neutrale Sprache ist Englisch (`NeutralLanguage` in `Directory.Build.props`), Deutsch kommt als Satelliten-Assembly (`de\*.resources.dll`).
- Eine resx je Bereich statt einer großen Datei (Liste in `docs/localization.md`): kleine Dateien, keine Konflikte bei paralleler Arbeit, Klassennamen projektübergreifend eindeutig.
- **Nur die UI-Kultur wechselt.** Die App setzt vor dem Host `CultureInfo.CurrentUICulture` und `DefaultThreadCurrentUICulture` auf `en` bzw. `de` (`UiLanguages`, Einstellung `AppSettings.Language`, für eine Sitzung `--lang=en|de`). `CurrentCulture` bleibt die von Windows, damit Formatierung und Parsen unverändert bleiben (eigene Formatkultur: Backlog).
- **Umschalten wirkt nach einem Neustart.** Ein Wechsel zur Laufzeit müsste jede Komponente neu rendern und Texte in bestehenden Zuständen (Meldungen, Tab-Titel) neu erzeugen; der Nutzen ist gering.
- **ModelHost:** Er bekommt wie bisher die Windows-Kultur (`--culture`) für `[Display]`-Namen; die App liest sie, bevor sie die UI-Kultur umstellt, und gibt sie `ModelHostRunner` und `ModelCache` (Cache-Schlüssel) ausdrücklich mit. Seine eigenen Meldungen folgen der UI-Sprache.
- Texte mit Markup über die Komponente `Fmt` (Platzhalter `{0}`–`{3}` als `RenderFragment`), Plural über `TextFormat.Plural` (Englisch und Deutsch kennen nur eins/mehrere).
- Tests: Alle Testprojekte laufen mit deutscher UI-Kultur (Module-Initializer in `tests/Shared/UiCulture.cs`), damit die vorhandenen Tests ihre Texte weiter prüfen. `ResourceTests` prüfen jede Textklasse: gleiche Schlüssel in beiden Sprachen, keine leeren Texte, gleiche Platzhalter.

## Konsequenzen

- Jedes neue Feature pflegt seine Texte in beiden Sprachen (Regel in der `CLAUDE.md`, „Fertig heißt“).
- Texte dürfen nicht in statischen Feldern zwischengespeichert werden (sie behielten die Sprache des ersten Zugriffs).
- Visual Studio zeigt die erzeugten Klassen erst nach einem Build; der resx-Editor von VS funktioniert mit den Dateien.
- Eine dritte Sprache wäre eine weitere `.<lang>.resx` je Bereich plus ein Eintrag in `UiLanguage`; dann müssten die Tests fehlende Übersetzungen erlauben (Rückfall auf Englisch) – Backlog „Weitere Sprachen von außen“.
