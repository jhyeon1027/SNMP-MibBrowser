# Changelog

## 1.0.1 build11

- Expand the complete MIB tree path when searching for a numeric OID.
- Resolve indexed instance OIDs to the most specific known MIB object.
- Preserve symbolic MIB names for all known nodes along filtered search paths.
- Use the official SNMP MibBrowser GitHub repository automatically for update checks.
- Remove the repository input from the updater while retaining optional pre-release support.
- Publish all GitHub-facing project documentation in English.

## 1.0.0 build10

- Initial public release.
- SNMP v1, v2c, and v3 support with GET, GET NEXT, WALK, and SET operations.
- Hierarchical MIB tree with built-in standard MIBs, search, and MIB import.
- Dedicated device views for physical interfaces, IP addresses, routing, ARP, and LLDP neighbors.
- German and English user interface with light and dark themes.
- Persistent SNMP profiles and configurable default MIB folder.
- Integrated GitHub update checker with optional pre-release support, integrity verification, and automatic restart.
- Portable Windows x64 single-file executable with no separate .NET installation required.
