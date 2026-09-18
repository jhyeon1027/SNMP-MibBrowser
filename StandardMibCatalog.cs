namespace SnmpMibBrowser;

public static class StandardMibCatalog
{
    public static IReadOnlyList<MibNode> Load() => Data.Select(x => new MibNode
    {
        Module=x.Module, Name=x.Name, Oid=x.Oid, Syntax=x.Syntax, Access=x.Access, Description=x.De, DescriptionEnglish=x.En
    }).ToList();

    private static readonly (string Module,string Name,string Oid,string Syntax,string Access,string De,string En)[] Data =
    [
        ("SNMPv2-MIB","system","1.3.6.1.2.1.1","OBJECT IDENTIFIER","not-accessible","Systemgruppe","System group"),
        ("SNMPv2-MIB","sysDescr","1.3.6.1.2.1.1.1","DisplayString","read-only","Beschreibung des Geräts","Device description"),
        ("SNMPv2-MIB","sysObjectID","1.3.6.1.2.1.1.2","OBJECT IDENTIFIER","read-only","Herstellerspezifische Gerätekennung","Vendor specific device identifier"),
        ("SNMPv2-MIB","sysUpTime","1.3.6.1.2.1.1.3","TimeTicks","read-only","Zeit seit Initialisierung","Time since initialization"),
        ("SNMPv2-MIB","sysContact","1.3.6.1.2.1.1.4","DisplayString","read-write","Kontaktperson","Contact person"),
        ("SNMPv2-MIB","sysName","1.3.6.1.2.1.1.5","DisplayString","read-write","Administrativer Gerätename","Administrative device name"),
        ("SNMPv2-MIB","sysLocation","1.3.6.1.2.1.1.6","DisplayString","read-write","Physischer Standort","Physical location"),
        ("SNMPv2-MIB","sysServices","1.3.6.1.2.1.1.7","Integer32","read-only","Angebotene OSI-Dienste","OSI services offered"),
        ("IF-MIB","interfaces","1.3.6.1.2.1.2","OBJECT IDENTIFIER","not-accessible","Netzwerkschnittstellen","Network interfaces"),
        ("IF-MIB","ifNumber","1.3.6.1.2.1.2.1","Integer32","read-only","Anzahl der Schnittstellen","Number of interfaces"),
        ("IF-MIB","ifTable","1.3.6.1.2.1.2.2","SEQUENCE OF IfEntry","not-accessible","Schnittstellentabelle","Interface table"),
        ("IF-MIB","ifIndex","1.3.6.1.2.1.2.2.1.1","InterfaceIndex","read-only","Eindeutiger Schnittstellenindex","Unique interface index"),
        ("IF-MIB","ifDescr","1.3.6.1.2.1.2.2.1.2","DisplayString","read-only","Schnittstellenbeschreibung","Interface description"),
        ("IF-MIB","ifType","1.3.6.1.2.1.2.2.1.3","IANAifType","read-only","Schnittstellentyp","Interface type"),
        ("IF-MIB","ifMtu","1.3.6.1.2.1.2.2.1.4","Integer32","read-only","Maximale Paketgröße","Maximum packet size"),
        ("IF-MIB","ifSpeed","1.3.6.1.2.1.2.2.1.5","Gauge32","read-only","Geschätzte Bandbreite","Estimated bandwidth"),
        ("IF-MIB","ifPhysAddress","1.3.6.1.2.1.2.2.1.6","PhysAddress","read-only","Physische Adresse","Physical address"),
        ("IF-MIB","ifAdminStatus","1.3.6.1.2.1.2.2.1.7","INTEGER","read-write","Administrativer Status","Administrative status"),
        ("IF-MIB","ifOperStatus","1.3.6.1.2.1.2.2.1.8","INTEGER","read-only","Betriebsstatus","Operational status"),
        ("IF-MIB","ifInOctets","1.3.6.1.2.1.2.2.1.10","Counter32","read-only","Empfangene Oktette","Received octets"),
        ("IF-MIB","ifOutOctets","1.3.6.1.2.1.2.2.1.16","Counter32","read-only","Gesendete Oktette","Transmitted octets"),
        ("IP-MIB","ipForwarding","1.3.6.1.2.1.4.1","INTEGER","read-write","IP-Weiterleitung aktiv","IP forwarding state"),
        ("IP-MIB","ipDefaultTTL","1.3.6.1.2.1.4.2","Integer32","read-write","Standard-IP-TTL","Default IP TTL"),
        ("IP-MIB","ipInReceives","1.3.6.1.2.1.4.3","Counter32","read-only","Empfangene IP-Datagramme","Received IP datagrams"),
        ("TCP-MIB","tcpActiveOpens","1.3.6.1.2.1.6.5","Counter32","read-only","Aktiv geöffnete TCP-Verbindungen","Active TCP opens"),
        ("TCP-MIB","tcpCurrEstab","1.3.6.1.2.1.6.9","Gauge32","read-only","Aktuell etablierte TCP-Verbindungen","Currently established TCP connections"),
        ("UDP-MIB","udpInDatagrams","1.3.6.1.2.1.7.1","Counter32","read-only","Empfangene UDP-Datagramme","Received UDP datagrams"),
        ("UDP-MIB","udpOutDatagrams","1.3.6.1.2.1.7.4","Counter32","read-only","Gesendete UDP-Datagramme","Sent UDP datagrams"),
        ("HOST-RESOURCES-MIB","hrSystemUptime","1.3.6.1.2.1.25.1.1","TimeTicks","read-only","Systemlaufzeit","System uptime"),
        ("HOST-RESOURCES-MIB","hrSystemDate","1.3.6.1.2.1.25.1.2","DateAndTime","read-write","Systemdatum und -zeit","System date and time"),
        ("HOST-RESOURCES-MIB","hrSystemNumUsers","1.3.6.1.2.1.25.1.5","Gauge32","read-only","Angemeldete Benutzer","Logged-in users"),
        ("ENTITY-MIB","entPhysicalTable","1.3.6.1.2.1.47.1.1.1","SEQUENCE OF EntPhysicalEntry","not-accessible","Physische Komponenten","Physical components"),
        ("ENTITY-MIB","entPhysicalDescr","1.3.6.1.2.1.47.1.1.1.1.2","SnmpAdminString","read-only","Komponentenbeschreibung","Component description"),
        ("ENTITY-MIB","entPhysicalName","1.3.6.1.2.1.47.1.1.1.1.7","SnmpAdminString","read-only","Komponentenname","Component name"),
        ("BRIDGE-MIB","dot1dBaseBridgeAddress","1.3.6.1.2.1.17.1.1","MacAddress","read-only","MAC-Adresse der Bridge","Bridge MAC address"),
        ("BRIDGE-MIB","dot1dBaseNumPorts","1.3.6.1.2.1.17.1.2","Integer32","read-only","Anzahl Bridge-Ports","Number of bridge ports"),
        ("LLDP-MIB","lldpMessageTxInterval","1.0.8802.1.1.2.1.1.1","Integer32","read-write","LLDP-Sendeintervall","LLDP transmit interval"),
        ("LLDP-MIB","lldpLocChassisId","1.0.8802.1.1.2.1.3.2","LldpChassisId","read-only","Lokale Chassis-ID","Local chassis ID"),
        ("LLDP-MIB","lldpLocSysName","1.0.8802.1.1.2.1.3.3","SnmpAdminString","read-only","Lokaler Systemname","Local system name")
    ];
}
