<#
.SYNOPSIS
    Read-only probe v2 of Acer's AcerGamingFunction WMI interface.

.DESCRIPTION
    v1 confirmed the interface exists but every call failed, because it passed
    UInt64 values into parameters the BIOS declares as UInt32. CIM does not
    coerce silently - it throws, and v1 swallowed the exception.

    v2 fixes that:
      * casts each input to the exact CimType the method declares
      * reports the real exception instead of hiding it
      * probes the extra methods v1 did not know about (fan table, gaming
        profile, profile settings, CPU OC) that this BIOS exposes beyond
        what the Linux acer-wmi driver implements

    Still strictly READ-ONLY. Only Get* method ids are called: 3, 5, 7, 9, 11,
    13, 15, 17, 19, 21, 23, 25. Nothing is written to the EC.

.NOTES
    Run elevated, on the Acer.
#>

[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\acer-probe-report-v2.txt"
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

$GAMING_GUID = '7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56'

# NOTE: plain hashtable, NOT [ordered]. Indexing an OrderedDictionary with an
# integer is POSITIONAL, which is why v1 printed method labels off by position.
$MethodNames = @{
     1 = 'SetGamingProfile';          3 = 'GetGamingProfile'
     2 = 'SetGamingLED';              4 = 'GetGamingLED'
     5 = 'GetGamingSysInfo'
     6 = 'SetGamingRgbKb';            7 = 'GetGamingRgbKb'
     8 = 'SetGamingProfileSetting';   9 = 'GetGamingProfileSetting'
    10 = 'SetGamingLEDBehavior';     11 = 'GetGamingLEDBehavior'
    12 = 'SetGamingLEDColor';        13 = 'GetGamingLEDColor'
    14 = 'SetGamingFanBehavior';     15 = 'GetGamingFanBehavior'
    16 = 'SetGamingFanSpeed';        17 = 'GetGamingFanSpeed'
    18 = 'SetGamingFanTable';        19 = 'GetGamingFanTable'
    20 = 'SetGamingKBBacklight';     21 = 'GetGamingKBBacklight'
    22 = 'SetGamingMiscSetting';     23 = 'GetGamingMiscSetting'
    24 = 'SetCPUOverclockingProfile';25 = 'GetCPUOverclockingProfile'
}

$ThermalProfiles = @{ 0='Quiet'; 1='Balanced'; 4='Performance'; 5='Turbo'; 6='Eco' }
$FanModes        = @{ 1='Auto'; 2='Turbo'; 3='Custom' }
$Sensors         = @{ 1='CPU temperature'; 2='CPU fan speed'; 3='External temp 2'
                      6='GPU fan speed'; 10='GPU temperature' }

# ------------------------------------------------------------------ identity
Emit-Header 'MACHINE IDENTITY'
$cs = Get-CimInstance Win32_ComputerSystem
Emit "Model    : $($cs.Model)"
Emit "BIOS     : $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Emit "Elevated : $isAdmin" $(if ($isAdmin) { 'Green' } else { 'Red' })
if (-not $isAdmin) { Emit '!! Re-run elevated or every call will fail.' 'Red' }

# --------------------------------------------------------------- class lookup
$gamingClass = $null
foreach ($c in (Get-CimClass -Namespace 'root\WMI')) {
    $q = $c.CimClassQualifiers['guid']
    if ($q -and ($q.Value -replace '[{}]','').ToUpperInvariant() -eq $GAMING_GUID) {
        $gamingClass = $c; break
    }
}
if (-not $gamingClass) {
    Emit 'FATAL: AcerGamingFunction class not found.' 'Red'
    $report | Set-Content $OutFile -Encoding utf8; return
}
Emit "Class    : $($gamingClass.CimClassName)" 'Green'

$methodByAcpiId = @{}
foreach ($m in $gamingClass.CimClassMethods) {
    $idQ = $m.Qualifiers['WmiMethodId']
    if ($idQ) { $methodByAcpiId[[int]$idQ.Value] = $m.Name }
}

$instance = Get-CimInstance -Namespace 'root\WMI' -ClassName $gamingClass.CimClassName `
            -ErrorAction SilentlyContinue | Select-Object -First 1
Emit "Instance : $(if ($instance) { 'found' } else { 'NONE (static calls)' })" `
     $(if ($instance) { 'Green' } else { 'Yellow' })

# --------------------------------------------------------------- invoke core
$script:LastError = $null

function Invoke-Acer {
<#
  Calls a method by ACPI id, casting the input to the exact declared CimType.
  Returns a hashtable of all [Out] parameters, or $null on failure
  (with $script:LastError set to the real exception message).
#>
    param([int]$Id, [uint64]$Value = 0)

    $script:LastError = $null
    if (-not $methodByAcpiId.ContainsKey($Id)) {
        $script:LastError = "method id $Id not present on this BIOS"; return $null
    }

    $mName = $methodByAcpiId[$Id]
    $mDef  = $gamingClass.CimClassMethods[$mName]
    $inDef = $mDef.Parameters | Where-Object { $_.Qualifiers['In'] } | Select-Object -First 1

    $arguments = @{}
    if ($inDef) {
        # THE v1 BUG: passing [uint64] into a UInt32 parameter makes CIM throw.
        $ct = "$($inDef.CimType)"
        $arguments[$inDef.Name] = switch -Regex ($ct) {
            'UInt8Array|Uint8Array' { [byte[]][BitConverter]::GetBytes([uint64]$Value) }
            '^UInt64$'              { [uint64]$Value }
            '^UInt32$'              { [uint32]$Value }
            '^UInt16$'              { [uint16]$Value }
            '^UInt8$|^Byte$'        { [byte]$Value }
            default                 { $Value }
        }
    }

    try {
        $r = if ($instance) {
            Invoke-CimMethod -InputObject $instance -MethodName $mName `
                -Arguments $arguments -ErrorAction Stop
        } else {
            Invoke-CimMethod -Namespace 'root\WMI' -ClassName $gamingClass.CimClassName `
                -MethodName $mName -Arguments $arguments -ErrorAction Stop
        }
    } catch {
        $script:LastError = $_.Exception.Message
        return $null
    }

    $out = @{}
    foreach ($p in ($mDef.Parameters | Where-Object { $_.Qualifiers['Out'] })) {
        $out[$p.Name] = $r.($p.Name)
    }
    return $out
}

function To-U64 {
    param($Raw)
    if ($null -eq $Raw) { return $null }
    if ($Raw -is [byte[]]) {
        $b = New-Object byte[] 8
        [Array]::Copy($Raw, $b, [Math]::Min(8, $Raw.Length))
        return [BitConverter]::ToUInt64($b, 0)
    }
    return [uint64]$Raw
}
function Fld {
    param([uint64]$V, [int]$Lo, [int]$Hi)
    $w = $Hi - $Lo + 1
    $m = if ($w -ge 64) { [uint64]::MaxValue } else { ([uint64]1 -shl $w) - 1 }
    return ($V -shr $Lo) -band $m
}
function Scalar {
    param([int]$Id, [uint64]$Value = 0)
    $o = Invoke-Acer -Id $Id -Value $Value
    if ($null -eq $o) { return $null }
    $first = $o.Keys | Where-Object { $_ -match 'gmOutput|Output' } | Select-Object -First 1
    if (-not $first) { $first = $o.Keys | Select-Object -First 1 }
    return To-U64 $o[$first]
}

# ------------------------------------------------------------- core probes
Emit-Header 'CORE PROBES (kernel-documented)'

# --- misc setting 0x0B : platform / thermal profile
$pp = Scalar -Id 23 -Value 0x0B
if ($null -ne $pp) {
    $st = Fld $pp 0 7; $v = Fld $pp 8 15
    Emit ("GetGamingMiscSetting(0x0B) = 0x{0:X16}  status={1}  value={2}" -f $pp, $st, $v) 'White'
    if ($st -eq 0) {
        $nm = if ($ThermalProfiles.ContainsKey([int]$v)) { $ThermalProfiles[[int]$v] } else { 'unknown' }
        Emit "  >> THERMAL PROFILE SUPPORTED - currently: $v ($nm)" 'Green'
    } else { Emit "  >> status $st - unsupported" 'Yellow' }
} else { Emit "GetGamingMiscSetting(0x0B) FAILED: $script:LastError" 'Red' }

# --- sys info / sensors
Emit ''
$sys = Scalar -Id 5 -Value 0x0000
if ($null -ne $sys) {
    $st = Fld $sys 0 7; $bmp = Fld $sys 24 39
    Emit ("GetGamingSysInfo(0x0000) = 0x{0:X16}  status={1}  sensorBitmap=0x{2:X4}" -f $sys, $st, $bmp) 'White'
    if ($st -eq 0) {
        foreach ($id in ($Sensors.Keys | Sort-Object)) {
            if ((($bmp -shr ($id - 1)) -band 1) -eq 0) {
                Emit ("  [no ] {0}" -f $Sensors[$id]) 'DarkGray'; continue
            }
            $rd = Scalar -Id 5 -Value ([uint64]0x0001 -bor ([uint64]$id -shl 8))
            if ($null -ne $rd -and (Fld $rd 0 7) -eq 0) {
                Emit ("  [YES] {0,-18} = {1}" -f $Sensors[$id], (Fld $rd 8 23)) 'Green'
            } else {
                Emit ("  [YES] {0,-18} = <read failed>" -f $Sensors[$id]) 'Yellow'
            }
        }
    }
} else { Emit "GetGamingSysInfo FAILED: $script:LastError" 'Red' }

# --- fan behavior
Emit ''
foreach ($f in @(@{n='CPU'; bit=0x01; lo=8;  hi=9},
                 @{n='GPU'; bit=0x08; lo=14; hi=15})) {
    $fb = Scalar -Id 15 -Value ([uint64]$f.bit)
    if ($null -ne $fb) {
        $st = Fld $fb 0 7; $mode = Fld $fb $f.lo $f.hi
        $nm = if ($FanModes.ContainsKey([int]$mode)) { $FanModes[[int]$mode] } else { 'unknown' }
        Emit ("GetGamingFanBehavior({0}) = 0x{1:X16}  status={2}  mode={3} ({4})" -f `
            $f.n, $fb, $st, $mode, $nm) $(if ($st -eq 0) { 'Green' } else { 'Yellow' })
    } else { Emit "GetGamingFanBehavior($($f.n)) FAILED: $script:LastError" 'Red' }
}

# --- fan duty
Emit ''
foreach ($f in @(@{n='CPU'; id=0x01}, @{n='GPU'; id=0x04})) {
    $fs = Scalar -Id 17 -Value ([uint64]$f.id)
    if ($null -ne $fs) {
        $st = Fld $fs 0 7; $pct = Fld $fs 8 15
        if ($st -eq 0) { Emit ("GetGamingFanSpeed({0}) = {1}%  >> MANUAL DUTY SUPPORTED" -f $f.n, $pct) 'Green' }
        else           { Emit ("GetGamingFanSpeed({0}) status={1} - not supported" -f $f.n, $st) 'Yellow' }
    } else { Emit "GetGamingFanSpeed($($f.n)) FAILED: $script:LastError" 'Red' }
}

# ------------------------------------------------- beyond-kernel discoveries
Emit-Header 'BEYOND-KERNEL METHODS (this BIOS exposes more than acer-wmi.c)'

$extra = @(
    @{ id=3;  name='GetGamingProfile';        val=0x00; note='separate from misc 0x0B' }
    @{ id=19; name='GetGamingFanTable';       val=0x00; note='full custom fan curve - not in kernel' }
    @{ id=9;  name='GetGamingProfileSetting'; val=0x00; note='per-profile tunables' }
    @{ id=7;  name='GetGamingRgbKb';          val=0x00; note='4-zone RGB keyboard' }
    @{ id=11; name='GetGamingLEDBehavior';    val=0x00; note='LED behaviour' }
    @{ id=13; name='GetGamingLEDColor';       val=0x00; note='LED colour' }
    @{ id=21; name='GetGamingKBBacklight';    val=0x00; note='kb backlight level' }
)
foreach ($e in $extra) {
    $r = Scalar -Id $e.id -Value ([uint64]$e.val)
    if ($null -ne $r) {
        Emit ("{0,-24} (id {1,2}) = 0x{2:X16}  status={3}   [{4}]" -f `
            $e.name, $e.id, $r, (Fld $r 0 7), $e.note) 'Green'
    } else {
        Emit ("{0,-24} (id {1,2}) FAILED: {2}" -f $e.name, $e.id, $script:LastError) 'DarkGray'
    }
}

# --- CPU OC profile uses a byte-array in/out, handle separately
Emit ''
$oc = Invoke-Acer -Id 25 -Value 0
if ($null -ne $oc) {
    Emit 'GetCPUOverclockingProfile (id 25):' 'Green'
    foreach ($k in $oc.Keys) {
        $v = $oc[$k]
        $disp = if ($v -is [byte[]]) { ($v | ForEach-Object { '{0:X2}' -f $_ }) -join ' ' } else { $v }
        Emit ("  {0,-20} = {1}" -f $k, $disp)
    }
} else {
    Emit "GetCPUOverclockingProfile (id 25) FAILED: $script:LastError" 'DarkGray'
}

# --- sweep every misc-setting index, read-only, to find undocumented ones
Emit-Header 'MISC-SETTING INDEX SWEEP (read-only, indices 0x00-0x1F)'
for ($i = 0; $i -le 0x1F; $i++) {
    $r = Scalar -Id 23 -Value ([uint64]$i)
    if ($null -eq $r) { continue }
    $st = Fld $r 0 7
    if ($st -eq 0) {
        $known = switch ($i) {
            0x05 { 'OC_1' } 0x07 { 'OC_2' }
            0x0A { 'supported profiles (unreliable per kernel)' }
            0x0B { 'PLATFORM PROFILE' }
            default { 'undocumented' }
        }
        Emit ("  index 0x{0:X2}  value={1,-5} raw=0x{2:X16}   {3}" -f $i, (Fld $r 8 15), $r, $known) 'Green'
    }
}

Emit ''
Emit 'No state was modified.' 'Cyan'
$report | Set-Content $OutFile -Encoding utf8
Emit "Report written to: $OutFile" 'Cyan'
