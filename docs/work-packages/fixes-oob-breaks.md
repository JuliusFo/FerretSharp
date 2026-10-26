# Fixes: Abbrechen über VPN – Breaks in-band zum Ausprobieren → Release 3.50.2

Backlog-Eintrag „Abbrechen über VPN“ (gemeldet 2026-10-08, am 2026-10-26 zum zweiten Mal beobachtet): Ein Grid-Laden wurde 0,8 s nach dem Start abgebrochen, 0,2 s nach dem Break endete der Befehl mit ORA-12570 statt ORA-01013, der nächste Befehl fand die Verbindung tot. Vorgehen mit dem Nutzer abgestimmt: erst belegen, dann ein vorläufiger Schalter für die echte Strecke; die endgültige Lösung entscheidet der Nutzer nach der Auswertung. Branch `fix/oob-breaks`.

## Was ODP.NET Managed (23.26.301) bietet
- `OracleConfiguration.DisableOOB` – `public static bool`, Standard `false` ([Oracle-Doku](https://docs.oracle.com/en/database/oracle/oracle-database/26/odpnt/ConfigurationDisableOOB.html)). Gilt für den ganzen Prozess und lässt sich nur setzen, solange noch keine Verbindung geöffnet wurde (gemessen: danach `InvalidOperationException` ORA-50099).
- `DISABLE_OOB=on` in der sqlnet.ora ([Net Services Reference](https://docs.oracle.com/en/database/oracle/oracle-database/26/netrf/parameters-for-the-sqlnet.ora.html); ODP.NET Core liest den Parameter laut [Konfigurationsdoku](https://docs.oracle.com/en/database/oracle/oracle-data-access-components/19.3/odpnt/InstallCoreConfiguration.html)). Wirkt **pro Verbindung**, wenn das Attribut `Tns_Admin` im Connection-String auf den Ordner zeigt (gemessen, siehe unten). Haken: Von den sqlnet.ora-Dateien liest ODP.NET nur die erste gefundene – bei TNS-Alias-Profilen ersetzte ein eigener Ordner die sqlnet.ora des Nutzers (und `tnsnames.ora` müsste mit).
- `(DISABLE_OOB=ON)` im Connect-Descriptor: nicht dokumentiert, gemessen wirkungslos (der Client bietet weiter OOB an).
- `DISABLE_OOB_AUTO` (Prüfung des Pfads beim Verbinden): kennt der Managed-Treiber nicht – der Name steht nicht in der DLL.

Belegt am Connect-Paket im ODP.NET-Trace (Service Options): mit OOB `0x0C01`, mit `DisableOOB` oder `DISABLE_OOB=on` `0x0801`; der Server antwortet im Accept mit `0x0C01`, wenn er OOB annimmt, sonst `0x0801`. Erst dann schickt ODP.NET einen Break als TCP-Urgent-Daten, sonst nur in-band.

## Nachbau ohne VPN
- **Die Test-Container können kein OOB:** Die gvenzl-Images ab 21c setzen auf dem Server `DISABLE_OOB=ON` ([gvenzl/oci-oracle-xe#43](https://github.com/gvenzl/oci-oracle-xe/issues/43): Verbindungen mit OOB scheiterten über Docker-Proxys). Jeder bisherige Abbruch in den Tests lief deshalb in-band, und der erste Proxy-Versuch zeigte nichts. Dazu verwirft der Port-Forwarder von Docker Desktop Urgent-Daten selbst. Die Tests schalten OOB auf dem Server für ihre Dauer ein (`OracleContainerFixture.AllowOutOfBandBreaksAsync`, sed auf die sqlnet.ora; der Server liest sie je neuer Session) und laufen allein (`SerialTests`, `DisableParallelization`).
- `BreakProxy` vor dem Container mit zwei Modellen einer VPN-Strecke. Ergebnis (Oracle 23, Abfrage mit 4 s Rechenzeit, Abbruch nach 1 s; gleich unter Windows durch den Forwarder und aus einem Linux-Container im selben Docker-Netz):

| Strecke | OOB (Standard) | in-band (`DISABLE_OOB`) |
|---|---|---|
| direkt (Server mit OOB) | ORA-01013 nach 1 s, Session lebt | ORA-01013 nach 1 s, Session lebt |
| verwirft Urgent-Daten | Abbruch greift erst, wenn der Server fertig ist (4,2 s), dann ORA-01013, Session lebt; bei einer endlosen Abfrage hängt der Abbruch | ORA-01013 nach 1 s, Session lebt |
| löscht das URG-Flag (Byte geht als Daten weiter) | Session weg: ORA-12537, sobald der Server wieder liest; der nächste Befehl ebenso | ORA-01013 nach 1 s, Session lebt |

- Die beiden Fälle des Nutzers passen auf je ein Modell: 2026-10-08 (lange Abfrage ließ sich nicht abbrechen, danach ORA-03135) auf „verwirft“, 2026-10-26 (Grid-Laden, das der Server schnell beendet, ORA-12570 0,2 s nach dem Break) auf „löscht das URG-Flag“ – manche Firewalls tun das standardmäßig. Welches das Kunden-VPN tut, zeigt erst der Versuch auf der echten Strecke. Die Hypothese „VPN schließt still rechnende Verbindungen“ passt zum zweiten Fall nicht (idle 0,0 s).
- Den globalen Schalter prüft kein Test im Testprozess (dort ist längst eine Verbindung offen); er setzt dieselbe Einstellung wie die sqlnet.ora (`DisableOOB : True` im Trace, gleiches Connect-Paket), die `In_band_…` pro Verbindung prüft.

## Gebaut (vorläufig)
- Startargument `--disable-oob` (`CommandLine.DisableOob`, neu `CommandLine.Has` für Schalter ohne Wert): `Program.Main` ruft vor allem anderen `OracleSession.SendBreaksInBand()` (setzt `OracleConfiguration.DisableOOB`), der App-Start loggt „Breaks are sent in band (--disable-oob)“. Ein Neustart (Update, Grafikfehler) behält das Argument.
- Diagnose: Die Session-Historie einer verlorenen Verbindung endet mit „; breaks in band“, wenn der Schalter an ist – so zeigt das Log bei der Auswertung, auf welchem Weg der Break ging.
- Kein ADR: Ohne Schalter ändert sich nichts. Der kommt mit der endgültigen Lösung.

## E2E
- App mit `--data-dir=<scratch>` und `--disable-oob` gestartet: Das Log zeigt „Breaks are sent in band (--disable-oob)“ direkt nach dem Start, kein `[ERR]`. Ohne Verbindung geprüft; den Abbruch selbst prüfen die Integrationstests.
- Für die Untersuchung eigene Container `ferret-oob-test` (Oracle 23), `ferret-oob-test21` (XE 21c), `ferret-oob-pub` und das Netz `ferret-oob-net`, danach entfernt.

## Offen – zu testen auf der echten Strecke
- Ein paar Tage mit `--disable-oob` über das VPN abbrechen: lange Abfrage im SQL-Editor, Grid-Laden, F5 beim Laden. Im Log nach „Breaks are sent in band“ beim Start und nach Verbindungsverlusten mit „breaks in band“ schauen.
- Danach entscheidet der Nutzer: Pro Verbindung ginge nur über eine eigene sqlnet.ora per `Tns_Admin` (mit dem Haken bei TNS-Alias-Profilen oben). Sonst OOB ohne Einstellung generell abschalten – Preis: Der Server bemerkt einen Break nur, wenn er die Verbindung prüft, ein Abbruch greift bei langen Rechnungen später (in den Tests nicht messbar: 1 s wie mit OOB). Im gvenzl-Issue wird außerdem etwas Overhead durch das Prüfen auf Breaks erwähnt (`BREAK_POLL_SKIP`); im Alltag des Nutzers beim Ausprobieren mit beobachten.
