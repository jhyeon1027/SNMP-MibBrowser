# Changelog

## 1.0.2 build12

- Add a toolbar OID field so GET, GET NEXT, and WALK can query OIDs that are not in the MIB tree; Enter runs GET.
- Selecting a MIB tree node fills the OID field, and invalid OIDs are reported without sending a request.
- Add a WALK depth limit (default 4, 0 = unlimited); deeper subtrees show only their first object.
- GET NEXT writes the returned OID back into the OID field so it can be pressed repeatedly.
- Show SNMPv3 auth and privacy keys as a tooltip when hovering the password fields.
- Check this fork (jhyeon1027/SNMP-MibBrowser) for updates.

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
