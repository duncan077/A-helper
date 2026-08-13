<#
.SYNOPSIS
    Read-only probe of the Acer BatteryControl WMI interface.

.DESCRIPTION
    Class BatteryControl, GUID 79772EC5-04B1-4BFD-843C-61E7F77B6CC9.

    The Linux driver (frederik-h/acer-wmi-battery) passes a single packed
    struct to each method. Windows does NOT work that way: the ACPI-WMI mapper
    decomposes the struct into individually named parameters, so each field
    must be set by name. Passing a packed byte[] fails with
    "Cannot convert System.Byte[] to System.IConvertible" - that was the bug in
    the previous dump script.

    Calls ONLY getters: 19 (GetBattInfoInterface), 20 (GetBatteryHealthControl-
    Status) and 22 (GetBatteryFunctionData). The setters 21 and 23 are never
    invoked, so charging behaviour is not modified.

    Methods 22/23 are absent from the Linux driver entirely. Their parameter
    names (uBACStartTime / uBACStopTime / uBACSwitch) point at a scheduled
    charging window, which this probe tries to read.

.NOTES
    Run elevated, on the Acer.
#>

[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\acer-battery-report.txt"
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
    Emit ('=' * 72) 'DarkCyan'
    Emit "  $Text" 'Cyan'
    Emit ('=' * 72) 'DarkCyan'
}
function Hex { param($b) if ($null -eq $b) { '<null>' } else { ($b | ForEach-Object { '{0:X2}' -f $_ }) -join ' ' } }

$BAT_GUID = '79772EC5-04B1-4BFD-843C-61E7F77B6CC9'

Emit-Header 'BATTERY CONTROL INTERFACE'

$cls = $null
foreach ($c in (Get-CimClass -Namespace 'root\WMI')) {
    $q = $c.CimClassQualifiers['guid']
    if ($q -and ($q.Value -replace '[{}]','').ToUpperInvariant() -eq $BAT_GUID) { $cls = $c; break }
}
if (-not $cls) {
    Emit 'BatteryControl class not present.' 'Red'
    $report | Set-Content $OutFile -Encoding utf8; return
}

Emit "Class: $($cls.CimClassName)" 'Green'

$inst = Get-CimInstance -Namespace 'root\WMI' -ClassName $cls.CimClassName -ErrorAction SilentlyContinue |
        Select-Object -First 1
if (-not $inst) { Emit 'No instance of the class.' 'Red'; $report | Set-Content $OutFile -Encoding utf8; return }

$byId = @{}
foreach ($m in $cls.CimClassMethods) {
    $q = $m.Qualifiers['WmiMethodId']
    if ($q) { $byId[[int]$q.Value] = $m.Name }
}
Emit ("Methods: " + (($byId.Keys | Sort-Object | ForEach-Object { "$_=$($byId[$_])" }) -join ', '))

function Invoke-Bat {
    param([int]$Id, [hashtable]$Arguments)
    if (-not $byId.ContainsKey($Id)) { return $null }
    try {
        return Invoke-CimMethod -InputObject $inst -MethodName $byId[$Id] `
               -Arguments $Arguments -ErrorAction Stop
    } catch {
        Emit "  call to id $Id failed: $($_.Exception.Message)" 'Red'
        return $null
    }
}

# ------------------------------------------- 20: health control status query
Emit-Header 'HEALTH CONTROL STATUS (method 20, read-only)'

# Each struct field set individually - this is the fix.
$r = Invoke-Bat -Id 20 -Arguments @{
    uBatteryNo     = [byte]1
    uFunctionQuery = [byte]1
    uReserved      = [byte[]]@(0, 0)
}

if ($r) {
    $fnList = [byte]$r.uFunctionList
    $status = $r.uFunctionStatus
    Emit ("uFunctionList   = 0x{0:X2}  (binary {1})" -f $fnList,
          [Convert]::ToString($fnList, 2).PadLeft(8, '0')) 'White'
    Emit ("uFunctionStatus = " + (Hex $status)) 'White'
    Emit ("uReturn         = " + (Hex $r.uReturn)) 'DarkGray'
    Emit ''

    $names = @{
        0 = 'HEALTH_MODE (80% charge limit)'
        1 = 'CALIBRATION_MODE'
    }
    $extraFound = $false
    for ($b = 0; $b -lt 8; $b++) {
        $supported = (($fnList -shr $b) -band 1) -eq 1
        $label = if ($names.ContainsKey($b)) { $names[$b] } else { 'UNDOCUMENTED FUNCTION' }
        if (-not $supported) {
            Emit ("  bit$b  [ - ]  $label") 'DarkGray'
            continue
        }
        $st = if ($status -and $b -lt $status.Count) { $status[$b] } else { $null }
        $state = if ($null -eq $st) { 'status byte missing' } elseif ($st -gt 0) { "ON  (0x{0:X2})" -f $st } else { 'OFF' }
        $col = if ($names.ContainsKey($b)) { 'Green' } else { 'Magenta' }
        if (-not $names.ContainsKey($b)) { $extraFound = $true }
        Emit ("  bit$b  [YES]  $label - $state") $col
    }

    Emit ''
    if ($extraFound) {
        Emit 'UNDOCUMENTED FUNCTIONS PRESENT - worth investigating for bypass.' 'Magenta'
    } else {
        Emit 'Only health/calibration are offered. No bypass function in this interface.' 'Yellow'
    }
}

# ----------------------------------- 22: battery function data (BAC) query
Emit-Header 'BATTERY FUNCTION DATA / BAC (method 22, read-only)'
Emit 'Parameter names suggest a scheduled charging window.' 'White'
Emit 'Not implemented by the Linux driver - undocumented.' 'White'
Emit ''

foreach ($mask in 0..7) {
    $r22 = Invoke-Bat -Id 22 -Arguments @{
        uFunctionMask = [byte]$mask
        uReservedIn   = [byte[]]@(0, 0, 0, 0, 0, 0)
    }
    if (-not $r22) { continue }

    $rc = $r22.uReturnCode
    $rcFirst = if ($rc -and $rc.Count -gt 0) { $rc[0] } else { 255 }

    # Only report masks the firmware accepts.
    if ($rcFirst -ne 0) {
        Emit ("  mask $mask : rejected (returnCode " + (Hex $rc) + ")") 'DarkGray'
        continue
    }

    Emit ("  mask $mask : ACCEPTED") 'Green'
    Emit ("      uBACStatus    = $($r22.uBACStatus)") 'Green'
    Emit ("      uBACStartTime = " + (Hex $r22.uBACStartTime)) 'Green'
    Emit ("      uBACStopTime  = " + (Hex $r22.uBACStopTime)) 'Green'
    Emit ("      uReservedOut  = " + (Hex $r22.uReservedOut)) 'DarkGray'
}

# ------------------------------------------ 19: battery information sweep
Emit-Header 'BATTERY INFORMATION (method 19, read-only index sweep)'

foreach ($idx in 0..15) {
    $r19 = Invoke-Bat -Id 19 -Arguments @{
        uBatteryInfoIndex = [uint32]$idx
        uBatteryNo        = [uint32]1
    }
    if (-not $r19) { continue }
    $v = $r19.uReturn
    if ($null -eq $v -or $v -eq 0) { continue }
    Emit ("  index {0,2} = {1,-12} (0x{1:X8})" -f $idx, $v) 'Green'
}

# ------------------------------------------------------- OS-level baseline
Emit-Header 'OS BATTERY BASELINE'
try {
    $b = Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1
    Emit "  Name            : $($b.Name)"
    Emit "  EstimatedCharge : $($b.EstimatedChargeRemaining) %"
    Emit "  BatteryStatus   : $($b.BatteryStatus)  (2 = on AC)"
} catch { Emit '  Win32_Battery unavailable' 'DarkGray' }

try {
    $s = Get-CimInstance -Namespace 'root\WMI' -ClassName BatteryStatus -ErrorAction Stop |
         Select-Object -First 1
    Emit "  Charging / Discharging / PowerOnline : $($s.Charging) / $($s.Discharging) / $($s.PowerOnline)"
    Emit "  ChargeRate / DischargeRate           : $($s.ChargeRate) / $($s.DischargeRate)"
    Emit "  RemainingCapacity                    : $($s.RemainingCapacity)"
} catch { Emit '  root\WMI BatteryStatus unavailable' 'DarkGray' }

Emit ''
Emit 'Only getters were called. Charging behaviour was not modified.' 'Cyan'
$report | Set-Content $OutFile -Encoding utf8
Emit "Report written to: $OutFile" 'Cyan'
