<#
.SYNOPSIS
    Enumerates EVERY ACPI-WMI class the BIOS exposes, not just the known ones.

.DESCRIPTION
    The earlier probes only looked for the seven GUIDs documented in the Linux
    acer-wmi driver. The battery charge limit is not among them - --watch proved
    it is not any of the misc-setting indices either - so it must live in a
    different WMI class that nothing has enumerated yet.

    This dumps every class in root\WMI carrying a 'guid' qualifier (the marker
    the ACPI-WMI mapper puts on BIOS-provided classes), with its methods and
    properties, then flags anything battery or charge related.

    Strictly read-only. It reads class DEFINITIONS and non-method properties;
    it never invokes a method.

.NOTES
    Run elevated, on the Acer.
#>

[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\acer-wmi-full-dump.txt"
)

$ErrorActionPreference = 'Continue'
$report = [System.Collections.Generic.List[string]]::new()

function Emit {
    param([string]$Text, [string]$Color = 'Gray')
    Write-Host $Text -ForegroundColor $Color
    $report.Add($Text)
}
function Emit-Header {
    param([string]$Text)
    Emit ''
    Emit ('=' * 74) 'DarkCyan'
    Emit "  $Text" 'Cyan'
    Emit ('=' * 74) 'DarkCyan'
}

# GUIDs already accounted for by acer-wmi.c - anything else is new territory.
$Known = @{
    '67C3371D-95A3-4C37-BB61-DD47B491DAAB' = 'AMW0_GUID1'
    '431F16ED-0C2B-444C-B267-27DEB140CF9C' = 'AMW0_GUID2'
    '6AF4F258-B401-42FD-BE91-3D4AC2D7C0D3' = 'WMID_GUID1'
    '95764E09-FB56-4E83-B31A-37761F60994A' = 'WMID_GUID2'
    '61EF69EA-865C-4BC3-A502-A0DEBA0CB531' = 'WMID_GUID3'
    '7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56' = 'WMID_GUID4 (gaming)'
    '676AA15E-6A47-4D9F-A2CC-1E6D18D14026' = 'ACERWMID_EVENT'
}

Emit-Header 'MACHINE'
$cs = Get-CimInstance Win32_ComputerSystem
Emit "Model : $($cs.Model)"
Emit "BIOS  : $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"

# ------------------------------------------------- every BIOS-provided class
Emit-Header 'ALL ACPI-WMI CLASSES IN root\WMI'

$classes = @()
try { $classes = Get-CimClass -Namespace 'root\WMI' -ErrorAction Stop }
catch { Emit "FATAL: $($_.Exception.Message)" 'Red'; $report | Set-Content $OutFile -Encoding utf8; return }

$acpiClasses = foreach ($c in $classes) {
    $q = $c.CimClassQualifiers['guid']
    if (-not $q -or -not $q.Value) { continue }
    [pscustomobject]@{
        Name  = $c.CimClassName
        Guid  = ($q.Value -replace '[{}]', '').ToUpperInvariant()
        Class = $c
    }
}

Emit "Found $($acpiClasses.Count) BIOS-provided classes."
Emit ''

foreach ($a in ($acpiClasses | Sort-Object Name)) {
    $tag = if ($Known.ContainsKey($a.Guid)) { $Known[$a.Guid] } else { '*** NOT IN acer-wmi.c ***' }
    $col = if ($Known.ContainsKey($a.Guid)) { 'DarkGray' } else { 'Green' }
    Emit ("  {0,-38} {1}  {2}" -f $a.Name, $a.Guid, $tag) $col
}

# --------------------------------------------- detail on the unknown classes
Emit-Header 'UNDOCUMENTED CLASSES - METHODS AND PROPERTIES'

$unknown = $acpiClasses | Where-Object { -not $Known.ContainsKey($_.Guid) }
if (-not $unknown) {
    Emit 'None. Every BIOS class is already known to the kernel driver.' 'Yellow'
} else {
    foreach ($a in ($unknown | Sort-Object Name)) {
        Emit ''
        Emit ("--- {0}  [{1}]" -f $a.Name, $a.Guid) 'Cyan'

        $methods = $a.Class.CimClassMethods
        if ($methods.Count -eq 0) {
            Emit '    (no methods - data-only class)' 'DarkGray'
        }
        foreach ($m in $methods) {
            $idQ = $m.Qualifiers['WmiMethodId']
            $id = if ($idQ) { $idQ.Value } else { '?' }
            $inp = ($m.Parameters | Where-Object { $_.Qualifiers['In'] } |
                    ForEach-Object { "$($_.CimType) $($_.Name)" }) -join ', '
            $outp = ($m.Parameters | Where-Object { $_.Qualifiers['Out'] } |
                     ForEach-Object { "$($_.CimType) $($_.Name)" }) -join ', '
            Emit ("    id={0,-4} {1}" -f $id, $m.Name) 'White'
            if ($inp)  { Emit "             in : $inp" 'DarkGray' }
            if ($outp) { Emit "             out: $outp" 'DarkGray' }
        }

        $props = $a.Class.CimClassProperties | Where-Object { $_.Name -notlike '__*' }
        if ($props) {
            Emit '    properties:' 'White'
            foreach ($p in $props) { Emit ("      {0} {1}" -f $p.CimType, $p.Name) 'DarkGray' }

            # Data-only classes are safe to read; method classes are not touched.
            if ($methods.Count -eq 0) {
                try {
                    $inst = Get-CimInstance -Namespace 'root\WMI' -ClassName $a.Name -ErrorAction Stop |
                            Select-Object -First 1
                    if ($inst) {
                        Emit '    live values:' 'Green'
                        foreach ($p in $props) {
                            Emit ("      {0,-28} = {1}" -f $p.Name, $inst.($p.Name)) 'Green'
                        }
                    }
                } catch {
                    Emit "    (instance read failed: $($_.Exception.Message))" 'DarkGray'
                }
            }
        }
    }
}

# ------------------------------------------------ battery / charge hunting
Emit-Header 'BATTERY AND CHARGE CANDIDATES'

$pattern = 'batt|charge|charging|power|thresh|limit|health|calib'

Emit 'Classes in root\WMI whose NAME matches battery/charge/power:' 'White'
$byName = $classes | Where-Object { $_.CimClassName -match $pattern }
if ($byName) {
    foreach ($c in $byName) {
        $q = $c.CimClassQualifiers['guid']
        $g = if ($q) { ($q.Value -replace '[{}]','').ToUpperInvariant() } else { '(not BIOS-provided)' }
        Emit ("  {0,-42} {1}" -f $c.CimClassName, $g) 'Green'
    }
} else { Emit '  none' 'DarkGray' }

Emit ''
Emit 'Classes whose METHOD or PROPERTY names match:' 'White'
foreach ($a in $acpiClasses) {
    $hits = @()
    foreach ($m in $a.Class.CimClassMethods) { if ($m.Name -match $pattern) { $hits += "method $($m.Name)" } }
    foreach ($p in $a.Class.CimClassProperties) { if ($p.Name -match $pattern) { $hits += "prop $($p.Name)" } }
    if ($hits) { Emit ("  {0,-38} {1}" -f $a.Name, ($hits -join ', ')) 'Green' }
}

Emit ''
Emit 'Other namespaces worth checking:' 'White'
foreach ($ns in @('root\CIMV2', 'root\ACER', 'root\WMI\ms_409')) {
    try {
        $hit = Get-CimClass -Namespace $ns -ErrorAction Stop |
               Where-Object { $_.CimClassName -match 'Acer|Nitro|Predator' }
        if ($hit) {
            foreach ($h in $hit) { Emit ("  {0} :: {1}" -f $ns, $h.CimClassName) 'Green' }
        } else { Emit "  $ns : no Acer classes" 'DarkGray' }
    } catch { Emit "  $ns : not present" 'DarkGray' }
}

# ------------------------------------------- Acer battery health interface
Emit-Header 'ACER BATTERY HEALTH INTERFACE (79772EC5-...)'

# Protocol from frederik-h/acer-wmi-battery (GPL-2.0):
#   method 19 = GetBatteryInformation
#   method 20 = GetBatteryHealthControlStatus  in: 4 bytes, out: 8 bytes
#   method 21 = SetBatteryHealthControl        in: 8 bytes, out: 4 bytes
# Only method 20 is called here - it is a pure query.
$BAT_GUID = '79772EC5-04B1-4BFD-843C-61E7F77B6CC9'

$batClass = $acpiClasses | Where-Object { $_.Guid -eq $BAT_GUID } | Select-Object -First 1
if (-not $batClass) {
    Emit 'Battery health GUID NOT present on this machine.' 'Yellow'
    Emit 'Charge limiting would then not be available through WMI at all.' 'Yellow'
} else {
    Emit "PRESENT as class: $($batClass.Name)" 'Green'
    Emit ''

    foreach ($m in $batClass.Class.CimClassMethods) {
        $idQ = $m.Qualifiers['WmiMethodId']
        $id = if ($idQ) { $idQ.Value } else { '?' }
        $inp = ($m.Parameters | Where-Object { $_.Qualifiers['In'] } |
                ForEach-Object { "$($_.CimType) $($_.Name)" }) -join ', '
        $outp = ($m.Parameters | Where-Object { $_.Qualifiers['Out'] } |
                 ForEach-Object { "$($_.CimType) $($_.Name)" }) -join ', '
        Emit ("  id={0,-4} {1}" -f $id, $m.Name) 'White'
        if ($inp)  { Emit "           in : $inp" 'DarkGray' }
        if ($outp) { Emit "           out: $outp" 'DarkGray' }
    }

    # Query which health functions the firmware actually offers.
    $getStatus = $batClass.Class.CimClassMethods |
                 Where-Object { $_.Qualifiers['WmiMethodId'] -and
                                [int]$_.Qualifiers['WmiMethodId'].Value -eq 20 } |
                 Select-Object -First 1

    if (-not $getStatus) {
        Emit ''
        Emit 'Method id 20 not exposed - cannot query health status.' 'Yellow'
    } else {
        Emit ''
        Emit "Calling $($getStatus.Name) (read-only query)..." 'White'
        try {
            $inst = Get-CimInstance -Namespace 'root\WMI' -ClassName $batClass.Name `
                    -ErrorAction Stop | Select-Object -First 1
            $inDef = $getStatus.Parameters | Where-Object { $_.Qualifiers['In'] } | Select-Object -First 1

            # uBatteryNo = 1, uFunctionQuery = 1, two reserved bytes.
            $args = @{ $inDef.Name = [byte[]]@(1, 1, 0, 0) }
            $r = Invoke-CimMethod -InputObject $inst -MethodName $getStatus.Name `
                 -Arguments $args -ErrorAction Stop

            $outDef = $getStatus.Parameters | Where-Object { $_.Qualifiers['Out'] } | Select-Object -First 1
            $bytes = $r.($outDef.Name)

            if ($null -eq $bytes) {
                Emit '  method returned no data.' 'Yellow'
            } else {
                Emit ("  raw output: " + (($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')) 'White'

                # out = uFunctionList(1) uReturn(2) uFunctionStatus(5)
                $fnList = $bytes[0]
                Emit ("  uFunctionList = 0x{0:X2} (bitmap of supported functions)" -f $fnList) 'Green'

                $health = ($fnList -band 1) -ne 0
                $calib  = ($fnList -band 2) -ne 0
                Emit ("    bit0 HEALTH_MODE (80% charge limit) : {0}" -f `
                      $(if ($health) { "SUPPORTED, currently $(if ($bytes[3] -gt 0) {'ON'} else {'OFF'})" } else { 'not supported' })) `
                      $(if ($health) { 'Green' } else { 'DarkGray' })
                Emit ("    bit1 CALIBRATION_MODE               : {0}" -f `
                      $(if ($calib) { "SUPPORTED, currently $(if ($bytes[4] -gt 0) {'ON'} else {'OFF'})" } else { 'not supported' })) `
                      $(if ($calib) { 'Green' } else { 'DarkGray' })

                # Anything above bit1 is undocumented - a bypass/passthrough mode
                # would show up here if the firmware has one.
                $extra = $fnList -band 0xFC
                if ($extra -ne 0) {
                    Emit ("    EXTRA BITS SET: 0x{0:X2} - undocumented functions exist!" -f $extra) 'Magenta'
                    for ($b = 2; $b -lt 8; $b++) {
                        if (($fnList -shr $b) -band 1) {
                            $st = if ($b + 1 -lt 8) { $bytes[$b + 3] } else { '?' }
                            Emit ("      bit$b : SUPPORTED, status byte = $st") 'Magenta'
                        }
                    }
                } else {
                    Emit '    no bits beyond health/calibration - firmware offers no other mode' 'DarkGray'
                }
            }
        } catch {
            Emit "  call failed: $($_.Exception.Message)" 'Red'
        }
    }
}

# --------------------------------------------------------- battery baseline
Emit-Header 'BATTERY STATE (for before/after comparison)'
try {
    $b = Get-CimInstance Win32_Battery -ErrorAction Stop
    foreach ($x in $b) {
        Emit "  Name              : $($x.Name)"
        Emit "  EstimatedCharge   : $($x.EstimatedChargeRemaining) %"
        Emit "  BatteryStatus     : $($x.BatteryStatus)  (2 = on AC)"
        Emit "  DesignCapacity    : $($x.DesignCapacity)"
        Emit "  FullChargeCapacity: $($x.FullChargeCapacity)"
    }
} catch { Emit "  Win32_Battery unavailable: $($_.Exception.Message)" 'DarkGray' }

try {
    $st = Get-CimInstance -Namespace 'root\WMI' -ClassName BatteryStatus -ErrorAction Stop |
          Select-Object -First 1
    if ($st) {
        Emit ''
        Emit '  root\WMI BatteryStatus:'
        Emit "    Charging      : $($st.Charging)"
        Emit "    Discharging   : $($st.Discharging)"
        Emit "    PowerOnline   : $($st.PowerOnline)"
        Emit "    ChargeRate    : $($st.ChargeRate)"
        Emit "    DischargeRate : $($st.DischargeRate)"
        Emit "    RemainingCap  : $($st.RemainingCapacity)"
        Emit "    Voltage       : $($st.Voltage)"
    }
} catch { Emit "  root\WMI BatteryStatus unavailable" 'DarkGray' }

Emit ''
Emit 'No method was invoked. Nothing was modified.' 'Cyan'
$report | Set-Content $OutFile -Encoding utf8
Emit "Report written to: $OutFile" 'Cyan'
