# SNMP MibBrowser

Aktueller Stand: **1.0.0 build10**

Open-Source MIB-Browser und SNMP-Diagnosewerkzeug für Windows (WPF/.NET 9). Die Oberfläche orientiert sich am ACLI Session Manager und unterstützt Deutsch/Englisch sowie ein persistentes helles und dunkles Design.

## Funktionen

- SNMP v1, v2c und v3 (noAuthNoPriv, authNoPriv, authPriv)
- GET, GET NEXT, WALK und SET
- SNMPv3: MD5, SHA-1/256/384/512 sowie DES und AES-128/192/256
- beliebig viele SMIv1/SMIv2-MIB-Dateien oder ganze Ordner laden
- MIB-Baum, Volltextsuche, Objektinformationen und symbolische Ergebnisnamen
- alphabetische A–Z-Sortierung der Knoten auf jeder Ebene des MIB-Baums
- Antwortliste mit Datentyp, Wert und Laufzeit; Ereignisprotokoll
- eingebauter Standardkatalog für SNMPv2-MIB, IF-MIB, IP-MIB, TCP-MIB, UDP-MIB, HOST-RESOURCES-MIB, ENTITY-MIB, BRIDGE-MIB und LLDP-MIB
- portable x64-Single-File-EXE ohne erforderliche .NET-Installation
- hierarchischer OID-Baum mit automatisch erzeugten Zwischenknoten
- Gerätetabellen für Interfaces, IP-Adressen, Routing, ARP und LLDP-Nachbarn
- Interfaces-Tabelle mit Filter auf physische Ports, binär korrekter MAC-Darstellung und IF-MIB-Hochgeschwindigkeitswerten
- typgerechte Darstellung von Broadcast-Adressen, Routing-/ARP-Enums sowie LLDP Chassis- und Port-IDs
- LLDP-Zuordnung des lokalen Portnamens aus der Local-Port-Tabelle und eindeutig bezeichnete Remote-Port-Spalte
- einheitliche dünne, themegerechte Tabellenlinien ohne schwarze oder doppelte Zellrahmen
- einklappbare SNMP-Konfiguration mit Zielgerät, Profil, Port, Version sowie v1/v2c- und v3-Zugangsdaten
- automatische `.0`-Instanz für skalare OIDs sowie GET/WALK per Doppelklick im OID-Baum
- WALK-Ergebnisse zusätzlich als pivotierte Tabelle mit Indexzeilen und Objektspalten
- Kontextmenü am MIB-Baum für GET, GET NEXT, WALK, Kopieren und Details
- persistente SNMP-Profile und auswählbarer Standard-MIB-Ordner im Optionen-Dialog
- SNMP-Profile enthalten ausschließlich Protokolleinstellungen; das Zielgerät bleibt unabhängig im Hauptfenster
- die Profilauswahl befüllt die sichtbaren SNMP-Einstellungen; Ziel und Werte können für die aktuelle Sitzung direkt angepasst werden
- synchronisierte Ziel- und Profilauswahl in der SNMP-Konfiguration sowie direkt vor den Abfrageaktionen
- Enter im Target-Feld führt unmittelbar einen GET-Test auf `sysDescr.0` aus
- konsistente deutsche und englische Texte für Statusmeldungen, Dialoge, Protokollmeldungen, Tooltips und Tabellenspalten
- durchgehend linksbündige ComboBox-Werte und Auswahllisten
- MIB-Datei- und MIB-Ordner-Import in der obersten Kopfleiste; GET, GET NEXT und WALK arbeiten mit dem ausgewählten Baumknoten
- kompakter Sprachwechsel per Globus-Symbol
- integrierte GitHub-Updateprüfung mit gespeicherter Repository-URL, optionaler Pre-Release-Auswahl, SHA-256-Prüfung und automatischem Neustart
- eigenständiger SNMP-SET-Tab mit OID, Datentyp und Schreibwert
- unveränderlicher Public-MIB-Baum; zusätzliche Dateien erweitern ausschließlich `private.enterprises` und `experimental`
- deduplizierter MIB-Import, der eingebaute Public-MIB-Definitionen beibehält

## Start

```powershell
dotnet run --project SnmpMibBrowser.csproj
```

Oder die veröffentlichte `SNMP MibBrowser.exe` direkt starten.

## Updates und GitHub

Im Hauptfenster öffnet **Updates** die GitHub-Updateprüfung. Das Repository [`marlon82/SNMP-MibBrowser`](https://github.com/marlon82/SNMP-MibBrowser) ist fest voreingestellt. Vorabversionen lassen sich separat ein- oder ausschließen. Details zur Veröffentlichung und zum automatischen Tag-Workflow stehen in [`RELEASE.md`](RELEASE.md).

Hinweis: UDP/161 muss zwischen Rechner und Zielgerät erreichbar sein. SNMP SET verändert das Zielgerät und erfordert eine schreibberechtigte Community bzw. einen passenden v3-Benutzer.

## Lizenz und Herkunft

Der Anwendungscode steht unter MIT. Er wurde als Clean-Room-Neuimplementierung erstellt und enthält keinen Code und keine Binärdateien des referenzierten XIO-Soft-Programms. SharpSnmpLib wird unter MIT verwendet. MIB-Dateien von Geräteherstellern werden aus Lizenzgründen nicht mitgeliefert und können über die Oberfläche importiert werden.
