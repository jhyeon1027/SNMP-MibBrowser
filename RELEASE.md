# Veröffentlichung auf GitHub

## Einmalige Einrichtung

1. Das öffentliche Repository `https://github.com/marlon82/SNMP-MibBrowser` verwenden.
2. Diesen Projektordner als Repository-Root verwenden und auf den Branch `main` pushen.
3. Die Update-URL und `RepositoryUrl` sind bereits fest auf dieses Repository eingestellt.

## Release erstellen

Ein stabiler Tag erzeugt automatisch ein reguläres Release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

Ein Tag mit Suffix wird automatisch als Vorabversion veröffentlicht:

```powershell
git tag v1.1.0-beta.1
git push origin v1.1.0-beta.1
```

Der Workflow baut die portable Windows-x64-Single-File-EXE, erstellt ein Quellarchiv und lädt beide Dateien in das GitHub Release. Der Assetname `SNMP MibBrowser.exe` beziehungsweise `SNMP-MibBrowser.exe` wird vom integrierten Updater erkannt.

## Checkliste

- Versions- und Build-String aktualisieren.
- `dotnet build -c Release` lokal ausführen.
- Light/Dark sowie Deutsch/Englisch prüfen.
- SNMP GET, WALK, Tabellen und Updateprüfung testen.
- Tag erstellen und GitHub Actions vollständig durchlaufen lassen.
