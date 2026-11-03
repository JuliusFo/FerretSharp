# ADR 0041: Breaks standardmäßig in-band

- Status: akzeptiert
- Datum: 2026-11-03
- Paket: Fix `fix/oob-default` (Nachfolger von `fixes-oob-breaks.md`, 3.50.2)

## Kontext

Über das Kunden-VPN riss die Session an einem Abbruch ab (ORA-12570/12537) oder der Abbruch griff erst, wenn der Server fertig war. Nachgestellt mit einem TCP-Proxy (`fixes-oob-breaks.md`): ODP.NET schickt einen Break standardmäßig als TCP-Urgent-Daten (out-of-band), wenn der Server zustimmt; eine Strecke, die Urgent-Daten verwirft, verzögert den Abbruch, eine, die das URG-Flag löscht, zerstört die Session. In-band blieb die Session in beiden Fällen heil. Seit 3.50.2 gab es dafür den vorläufigen Startschalter `--disable-oob`.

Ergebnis auf der echten Strecke (Rückmeldung 2026-11-03): Beim Nutzer hörten die Verbindungsabbrüche mit `--disable-oob` auf, bei einem Kollegen mit demselben Problem ebenso. Entscheidung des Nutzers: in-band als Standard.

## Entscheidung

- **Breaks gehen immer in-band**: `Program.Main` setzt vor allem anderen `OracleConfiguration.DisableOOB` (`OracleSession.SendBreaksInBand`) – prozessweit, ODP.NET nimmt es nur vor der ersten Verbindung (ORA-50099).
- **Notausgang `--enable-oob`**: lässt die Einstellung weg (ODP.NET-Standard). Das Log nennt es beim Start („Out-of-band breaks allowed (--enable-oob)“), die Historie einer verlorenen Session endet dann mit „; out-of-band breaks allowed“ – nur „erlaubt“: Ob ein Break wirklich out-of-band geht, entscheiden Server und sqlnet.ora. Die Entscheidung trifft `CommandLine.BreaksOutOfBand`; gesetzt wird erst in der Instanz, die die App startet (die Übergabe eines Links braucht keinen Treiber). Keine Einstellung in der Oberfläche: Sie wirkte erst nach einem Neustart, und es gibt keinen bekannten Fall, der sie braucht.
- `--disable-oob` ist überflüssig; die App ignoriert es wie jedes unbekannte Argument, vorhandene Verknüpfungen stören also nicht.
- **Verworfen: pro Verbindung** über eine eigene sqlnet.ora per `Tns_Admin` – ersetzt bei TNS-Alias-Profilen die sqlnet.ora des Nutzers (ODP.NET liest nur die erste gefundene), und ein Vorteil von OOB war bei keiner Verbindung zu sehen.

## Folgen

- In-band bemerkt der Server einen Break erst, wenn er die Verbindung prüft; ein Abbruch kann bei langen Rechnungen später greifen (in den Tests nicht messbar: ORA-01013 nach 1 s wie mit OOB, auch direkt gegen einen Server mit OOB). Beim Nutzer über Wochen im Alltag nicht aufgefallen.
- Greift ein Abbruch auf einer Verbindung spürbar spät, zuerst mit `--enable-oob` vergleichen.
- Die gvenzl-Test-Container haben OOB auf dem Server ohnehin aus; die Tests zu Breaks (`OutOfBandBreakTests`) bleiben, sie belegen beide Wege.
