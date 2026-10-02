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

The workflow builds the portable Windows x64 single-file executable, creates a source archive, and uploads both files to the GitHub release. The integrated updater recognizes assets named `SNMP MibBrowser.exe` or `SNMP-MibBrowser.exe`.

## Checklist

- Update the version and build string.
- Run `dotnet build -c Release` locally.
- Verify light and dark themes in both English and German.
- Test SNMP GET, WALK, tables, and update checking.
- Create the tag and ensure the GitHub Actions workflow completes successfully.
