# Changelog

## 1.0.6 build16

- WALK compare tab: walk the same OID 2-10 times at a fixed interval; rows whose value changed are highlighted, with a "Changed only" filter and a Change column.
- Multi-target window: GET system or custom OIDs from many targets (ranges like 10.1.1.10-20), 10 in parallel, with its own version and community list.
- Multi-target failure reasons (No SNMP / host down / port closed / SNMPv3 errors), Re-run failed, numeric IP sorting and remembered inputs.
- SNMPv3 rejections (unknown user, wrong password or algorithm, decryption error) are reported as errors instead of being shown as values.

## 1.0.5 build15

- Remember the last 20 targets that answered; open the list with the button next to Target or the Down key.
- Results: right-click to copy value, OID, "OID = value", or rows for Excel; Ctrl+C copies rows with headers; double-click copies the value.
- Clipboard copy uses the Win32 clipboard with retries, avoiding false "clipboard busy" errors when a clipboard watcher is running.
- Show non-text OctetString values (MAC addresses, binary IDs) as hex in Results and the WALK table.
- Save to profile button in the SNMP configuration panel.
- English is the default language for new installations; "None" replaces "Keine" in the Auth/Privacy lists.
- Results columns keep a minimum width and scroll horizontally on narrow screens.

## 1.0.4 build14

- Support an ordered list of v1/v2c communities (List button next to Community, also in the profile editor). Communities are tried top to bottom; the accepted one is logged and remembered per device for the session.
- SET tries the community list in order, skipping communities that time out or are refused.
- WALK now shows the Results tab (scrolled to the new rows) instead of switching to the WALK table tab.
- Toolbar wraps on narrow or square screens so Options, Updates, and About stay reachable; the window opens within the desktop work area.

## 1.0.3 build13

- Allow only one query at a time; starting another query while one is running is rejected with a status message.
- Log unhandled errors to `%LOCALAPPDATA%\SNMP-MibBrowser\error.log` and keep the application running after UI errors.
- Add per-profile timeout (ms) and retry settings; GET, GET NEXT, and WALK requests are retried on timeout (SET is not).
- Warn in the log when a WALK or device table stops at the 10,000-request limit and the result is incomplete.

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
