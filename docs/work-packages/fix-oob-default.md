# Fixes: Breaks standardmäßig in-band → Release (nächste)

Auswertung zu `fixes-oob-breaks.md` (3.50.2): Der Nutzer meldet am 2026-11-03, dass `--disable-oob` die ständigen Verbindungsabbrüche über das VPN behoben hat, bei einem Kollegen ebenso. Auftrag: in-band zum Standard machen. Branch `fix/oob-default`, ADR 0041.

## Gebaut
- `Program.Main` setzt `OracleSession.SendBreaksInBand()` ohne Schalter, vor der ersten Verbindung wie bisher.
- `CommandLine.DisableOob` → `CommandLine.EnableOob` (`--enable-oob`): Notausgang zurück zum ODP.NET-Standard. Das Start-Log nennt nur noch die Abweichung („Breaks are sent out of band (--enable-oob)“), die Historie einer verlorenen Session endet mit „; breaks out of band“ statt „; breaks in band“ (`DescribeHistory(…, outOfBand)`).
- `--disable-oob` wird ignoriert wie jedes unbekannte Argument; Verknüpfungen mit dem alten Schalter starten weiter.
- Keine Einstellung in der Oberfläche (wirkte erst nach Neustart, kein bekannter Bedarf) – offene Frage an den Nutzer, falls doch.
- Backlog-Eintrag „Abbrechen über VPN“ entfernt.

## Nicht geprüft
- Der globale Schalter selbst läuft weiter in keinem Test (im Testprozess ist längst eine Verbindung offen); die Integrationstests prüfen dieselbe Einstellung per sqlnet.ora je Verbindung.
