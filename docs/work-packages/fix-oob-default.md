# Fixes: Breaks standardmäßig in-band → Release (nächste)

Auswertung zu `fixes-oob-breaks.md` (3.50.2): Der Nutzer meldet am 2026-11-03, dass `--disable-oob` die ständigen Verbindungsabbrüche über das VPN behoben hat, bei einem Kollegen ebenso. Auftrag: in-band zum Standard machen. Branch `fix/oob-default`, ADR 0041.

## Gebaut
- `Program.Main` setzt `OracleSession.SendBreaksInBand()` ohne Schalter (`CommandLine.BreaksOutOfBand`), erst in der Instanz, die die App startet – nach der Instanzsperre, vor `new App` und damit vor der ersten Verbindung.
- `CommandLine.DisableOob` → `CommandLine.EnableOob` (`--enable-oob`): Notausgang zurück zum ODP.NET-Standard. Das Start-Log nennt nur noch die Abweichung („Out-of-band breaks allowed (--enable-oob)“), die Historie einer verlorenen Session endet mit „; out-of-band breaks allowed“ statt „; breaks in band“ (`DescribeHistory(…, outOfBandAllowed)`).
- `--disable-oob` wird ignoriert wie jedes unbekannte Argument; Verknüpfungen mit dem alten Schalter starten weiter.
- Keine Einstellung in der Oberfläche (wirkte erst nach Neustart, kein bekannter Bedarf) – offene Frage an den Nutzer, falls doch.
- Backlog-Eintrag „Abbrechen über VPN“ entfernt.

## Nicht geprüft
- Der globale Schalter selbst läuft weiter in keinem Test (im Testprozess ist längst eine Verbindung offen); die Integrationstests prüfen dieselbe Einstellung per sqlnet.ora je Verbindung.

## E2E
- App zweimal mit `--data-dir=<scratch>` gestartet: ohne Schalter keine Zeile zu Breaks und kein `[ERR]`, mit `--enable-oob` „Out-of-band breaks allowed (--enable-oob)“. Ohne Verbindung; den Abbruch selbst prüfen die Integrationstests.

## Nachtrag: Code-Review (2026-11-03)
Entscheidung des Nutzers: „Alle beheben“.
1. M – `DatabaseErrorTests.Keep_alive_ping_…` prüfte `EndsWith` auf die Historie; im Testprozess (kein in-band) endet sie jetzt mit dem Modus, CI rot → `Contains`. Behoben, Klasse gegen den Testcontainer grün.
2. K – „breaks out of band“ behauptete mehr, als der Client weiß (Server oder sqlnet.ora mit `DISABLE_OOB` halten Breaks trotzdem in-band) → „out-of-band breaks allowed“ in Log, Historie und Doku. Behoben.
3. K – Die Entscheidung in `Program.Main` war ungetestet → `CommandLine.BreaksOutOfBand` mit Test (Standard, `--disable-oob` wirkungslos, Link kann `--enable-oob` nicht anhängen). Behoben; das `!` in `Program.Main` selbst deckt nur der E2E-Start ab.
4. K (Verdacht) – ODP.NET wurde bei jedem Start geladen, auch bei der Übergabe eines Links, vor Serilog → erst nach der Instanzsperre. Behoben; eine Ausnahme aus `OracleConfiguration` beendet die startende Instanz weiter ohne Log (ohne Treiber ginge ohnehin keine Verbindung).
- Zweiter Durchgang: ADR 0041 nannte noch „vor allem anderen“ → korrigiert; dieser Nachtrag ergänzt.
