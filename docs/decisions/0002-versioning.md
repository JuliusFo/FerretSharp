# ADR 0002: Auslieferung in Versionen – read-only zuerst, .NET-Integration in v3

- Status: akzeptiert
- Datum: 2026-10-01

## Kontext

Schreibender Zugriff (Transaktionen, Locks, Typ-Parsing bei Eingaben, Verbindungsabbrüche mit offenen Änderungen) ist der riskanteste und aufwendigste Teil. Die Kernfeatures FK-Navigation, Filter und Workspaces brauchen ihn nicht. Das eigentliche Ziel des Tools ist die Brücke zur C#-/EF-Core-Welt.

## Entscheidung

- **v1 – Read-only Browser:** kein Codepfad, der DML/DDL erzeugt oder ausführt (INSERT-Export nur als Text).
- **v2 – Sandbox-Editing:** Transaktionsmodell (Pending → Flushed → Committed), Editieren, Prod-Freischaltung.
- **v3 – .NET-Integration:** DbContext-Modell, Schema-Anreicherung, LINQ-Konsole, Code-Generierung.
- v1 und v2 bauen v3 vor (Annotation-Schicht, austauschbare Spaltenpräsentation, FK-Quellen als Enum).

## Konsequenzen

- Früh benutzbares Werkzeug ohne Risiko für Prod-Daten.
- Der Mehrwert der Workspaces (getrennte Transaktionen) zeigt sich erst ab v2.
- Features späterer Versionen werden nicht vorgezogen, sondern in `docs/backlog.md` notiert.
