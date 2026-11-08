# Publishing on GitHub

## One-time setup

1. Use the public repository `https://github.com/jhyeon1027/SNMP-MibBrowser`.
2. Use this project directory as the repository root and push it to the `main` branch.
3. The update URL and `RepositoryUrl` are already configured for this repository.

## Create a release

A stable tag automatically creates a regular release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

A tag with a suffix is automatically published as a pre-release:

```powershell
git tag v1.1.0-beta.1
git push origin v1.1.0-beta.1
```

The workflow builds the portable Windows x64 executable (`SNMP MibBrowser.exe`) and the Linux terminal app (`snmpmb-linux-x64.tar.gz`, with a smoke test), creates a source archive and `SHA256SUMS.txt` (which also lists the unpacked `snmpmb`), and uploads everything to the GitHub release. The integrated Windows updater recognizes assets named `SNMP MibBrowser.exe` or `SNMP-MibBrowser.exe`.

## Checklist

- Update the version in `Directory.Build.props` and the build string in `MainWindow.xaml`, `README.md` and `CHANGELOG.md`.
- Run `dotnet build SnmpMibBrowser.csproj -c Release` and `dotnet publish Tui/SnmpMibBrowser.Tui.csproj -c Release -r linux-x64` locally.
- Verify light and dark themes in both English and German.
- Test SNMP GET, WALK, tables, and update checking.
- Run `snmpmb` in an SSH terminal (Termius or MobaXterm): GET/WALK, F12 copy, Ctrl+Q.
- Create the tag and ensure the GitHub Actions workflow completes successfully.
