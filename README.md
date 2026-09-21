# SNMP MibBrowser

Current version: **1.0.0 build10**

Open-source MIB browser and SNMP diagnostics tool for Windows, built with WPF and .NET 9. The interface is inspired by ACLI Session Manager and supports English and German as well as persistent light and dark themes.

## Features

- SNMP v1, v2c, and v3 (`noAuthNoPriv`, `authNoPriv`, and `authPriv`)
- GET, GET NEXT, WALK, and SET operations
- SNMPv3 support for MD5, SHA-1/256/384/512, DES, and AES-128/192/256
- Import individual SMIv1/SMIv2 MIB files or entire folders
- Hierarchical MIB tree, full-text search, object details, and symbolic result names
- Alphabetical A-Z sorting of nodes at every level of the MIB tree
- Result list with data type, value, response time, and event log
- Built-in standard catalog for SNMPv2-MIB, IF-MIB, IP-MIB, TCP-MIB, UDP-MIB, HOST-RESOURCES-MIB, ENTITY-MIB, BRIDGE-MIB, and LLDP-MIB
- Portable Windows x64 single-file executable with no separate .NET installation required
- Hierarchical OID tree with automatically generated intermediate nodes
- Dedicated device tables for interfaces, IP addresses, routes, ARP entries, and LLDP neighbors
- Physical-interface filtering, correct binary MAC address formatting, and IF-MIB high-speed values
- Type-aware formatting for broadcast addresses, routing and ARP enumerations, and LLDP chassis and port IDs
- LLDP local-port mapping and clearly labeled remote-port information
- Consistent thin, theme-aware table lines without black or doubled cell borders
- Collapsible SNMP configuration with target, profile, port, version, and v1/v2c or v3 credentials
- Automatic `.0` instances for scalar OIDs and GET/WALK by double-clicking an OID tree node
- Optional pivoted WALK table with index rows and object columns
- MIB-tree context menu for GET, GET NEXT, WALK, copy, and object details
- Persistent SNMP profiles and a configurable default MIB folder
- SNMP profiles contain protocol settings only; the target remains independent in the main window
- Profile selection populates the visible SNMP settings while allowing per-session overrides
- Synchronized target and profile controls in the configuration area and query toolbar
- Pressing Enter in the Target field immediately tests the device with a GET request for `sysDescr.0`
- Consistent English and German localization for status messages, dialogs, logs, tooltips, and table columns
- Left-aligned ComboBox values and selection lists
- MIB file and folder import in the top toolbar
- Compact language switching through a globe icon
- Integrated GitHub update checker with optional pre-release support, SHA-256 verification, and automatic restart
- Dedicated SNMP SET tab with OID, data type, and value fields
- Protected built-in public MIB tree; imported files extend only `private.enterprises` and `experimental`
- Deduplicated MIB imports that preserve built-in public MIB definitions

## Run from source

```powershell
dotnet run --project SnmpMibBrowser.csproj
```

Alternatively, download and run the published `SNMP MibBrowser.exe`.

## Updates and GitHub releases

Select **Updates** in the main window to open the GitHub update checker. The repository [`marlon82/SNMP-MibBrowser`](https://github.com/marlon82/SNMP-MibBrowser) is configured by default. Pre-release versions can be included or excluded separately. See [`RELEASE.md`](RELEASE.md) for details about publishing and the automated tag workflow.

> [!NOTE]
> UDP port 161 must be reachable between the computer and the target device. SNMP SET modifies the target device and requires a write-enabled community or an appropriately configured SNMPv3 user.

## License and provenance

The application is licensed under the MIT License. It is a clean-room reimplementation and contains no source code or binaries from the referenced XIO-Soft application. SharpSnmpLib is used under the MIT License. Vendor-specific MIB files are not bundled for licensing reasons and can be imported through the application.
