# Local companion for an existing Fable-2-Recomp 1.5.0 installation.
# Does NOT patch fable_2.exe or DLLs, bypass hashes, enable dump collection,
# modify Windows settings, upload files, or touch saves/data.
[CmdletBinding()]
param(
    [ValidateSet('Current', 'Readback')][string]$Mode = 'Current',
    [string]$GameDirectory = $PSScriptRoot,
    [string]$DataDirectory = '',
    [switch]$SelfTest
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$ParserPath = Join-Path $PSScriptRoot 'lib\TomlTextDocument.cs'
if (-not ('Fable2Launcher.TomlTextDocument' -as [type])) {
    Add-Type -Path $ParserPath -ReferencedAssemblies 'System.dll', 'System.Core.dll'
}

function Assert-True([bool]$Value, [string]$Message) {
    if (-not $Value) { throw "SELF-TEST FAILED: $Message" }
}

if ($SelfTest) {
    $text = "[patches] # keep section comment`r`nrealtime_texture_morphing = true#keep inline`r`nhero_dog_texture_readback=false`r`n[other]`r`npath='C:\Folder #1'`r`n"
    $d = New-Object Fable2Launcher.TomlTextDocument($text)
    Assert-True ($d.GetValue('patches','realtime_texture_morphing') -eq 'true') 'header with comment'
    $d.SetValue('patches','realtime_texture_morphing','false')
    $d.SetValue('patches','hero_dog_texture_readback','true')
    $out = $d.ToString()
    Assert-True ($out.Contains('false#keep inline')) 'inline comment without a space'
    Assert-True ($out.Contains("path='C:\Folder #1'")) 'literal # preserved'
    Assert-True ($out.Contains("`r`n")) 'CRLF preserved'
    Assert-True (([regex]::Matches($out, '\[patches\]')).Count -eq 1) 'no duplicate section'
    $again = New-Object Fable2Launcher.TomlTextDocument($out)
    $again.SetValue('patches','hero_dog_texture_readback','true')
    Assert-True ($again.ToString() -eq $out) 'repeat write idempotent'
    $v = "quoted `"value`" # comment-like \ slash`nnext"
    Assert-True ([Fable2Launcher.TomlTextDocument]::Unquote([Fable2Launcher.TomlTextDocument]::Quote($v)) -eq $v) 'escaped string roundtrip'
    foreach ($bad in @("[patches]`nx=1`n[patches]`ny=2", "[patches]`nx=1`nx=2", 'note="""multiline')) {
        $rejected = $false
        try { $null = New-Object Fable2Launcher.TomlTextDocument($bad) } catch { $rejected = $true }
        Assert-True $rejected 'unsafe input rejected'
    }
    $root = New-Object Fable2Launcher.TomlTextDocument("# root`n[other]`na=true`n")
    $root.SetValue('', 'readback_resolve_force_addresses', '"0x12704000"')
    Assert-True ($root.ToString().IndexOf('readback_resolve_force_addresses') -lt $root.ToString().IndexOf('[other]')) 'root insertion before tables'
    Write-Host 'PASS: 11 parser assertions. No game files were opened or modified.'
    exit 0
}

function Write-Report([string]$Path, $Report) {
    $temp = $Path + '.tmp'
    [IO.File]::WriteAllText($temp, ($Report | ConvertTo-Json -Depth 8), $Utf8)
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temp, $Path, $null) }
    else { [IO.File]::Move($temp, $Path) }
}

function Copy-Bounded([string]$Source, [string]$Target, [int]$MaxBytes) {
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target))
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $sourceStream = New-Object IO.FileStream($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    try {
        $size = $sourceStream.Length
        if ($size -gt $MaxBytes) { $sourceStream.Position = $size - $MaxBytes }
        $targetStream = [IO.File]::Create($Target)
        try {
            $buffer = New-Object byte[] 81920
            $remaining = [Math]::Min([long]$MaxBytes, $size)
            while ($remaining -gt 0) {
                $read = $sourceStream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                if ($read -eq 0) { break }
                $targetStream.Write($buffer, 0, $read)
                $remaining -= $read
            }
        } finally { $targetStream.Dispose() }
        return ($size -gt $MaxBytes)
    } finally { $sourceStream.Dispose() }
}

function Copy-Configs([string]$Destination) {
    $null = [IO.Directory]::CreateDirectory($Destination)
    foreach ($name in @('fable2_config.toml','fable_2.toml','launcher-settings.toml','fable2_build.json')) {
        $path = Join-Path $GameDirectory $name
        if (-not [IO.File]::Exists($path)) { continue }
        $file = Get-Item -LiteralPath $path
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($file.Length -gt 2MB) { throw "Config too large to snapshot safely: $name" }
        $null = Copy-Bounded $path (Join-Path $Destination $name) 2MB
    }
}

$Report = $null
$Session = $null
$Process = $null
try {
    $GameDirectory = (Resolve-Path -LiteralPath $GameDirectory).Path
    if ([string]::IsNullOrWhiteSpace($DataDirectory)) { $DataDirectory = $GameDirectory }
    $DataDirectory = (Resolve-Path -LiteralPath $DataDirectory).Path
    $Exe = Join-Path $GameDirectory 'fable_2.exe'
    $ConfigPath = Join-Path $GameDirectory 'fable2_config.toml'
    if (-not [IO.File]::Exists($Exe)) { throw 'Extract this tool next to fable_2.exe, not into the source-code folder.' }
    if (-not [IO.File]::Exists((Join-Path $DataDirectory 'default.xex')) -or -not [IO.Directory]::Exists((Join-Path $DataDirectory 'data'))) {
        throw 'default.xex and the data folder were not found. Existing game files are required; none are supplied by this tool.'
    }
    $running = @(Get-Process -Name 'fable_2' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) { throw 'Close Fable 2 before starting a diagnostic session.' }
    if (-not [IO.File]::Exists($ConfigPath)) { throw 'fable2_config.toml is missing. Start the regular launcher once before using this tool.' }
    $original = [IO.File]::ReadAllText($ConfigPath)
    $document = New-Object Fable2Launcher.TomlTextDocument($original)
    $version = $document.GetValue('general','config_version')
    if ($version -and $version -ne '1') { throw 'Unsupported config version. No changes made.' }

    Write-Host 'Fable 2 texture diagnostics v1 - local test tool, not a rebuilt game.'
    Write-Host 'Close the regular launcher so it does not overwrite settings during this test.'
    Write-Host ('Game: ' + $GameDirectory)
    if ($Mode -eq 'Readback') {
        Write-Host 'This test disables forced realtime morphing and enables targeted hero/dog readback.'
        Write-Host 'Only the two texture toggles are saved. A backup is created first.'
        Write-Host 'A launch argument overrides readback_resolve_force_addresses ONLY for this process.'
        Write-Host 'The global readback_resolve mode, graphics, input, tick rates and saves are NOT changed.'
        $answer = Read-Host 'Apply this compatibility test and launch the game? Type Y'
        if ($answer -notmatch '^[yY]$') { Write-Host 'Cancelled. No changes made.'; exit 0 }
    }
    $Session = Join-Path $GameDirectory ('diagnostics\session-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $null = [IO.Directory]::CreateDirectory($Session)
    Copy-Configs (Join-Path $Session 'config-before')
    $Report = [ordered]@{
        report_version = 1; tool = 'texture-diagnostics-v1'; mode = $Mode
        started_utc = [DateTime]::UtcNow.ToString('o'); status = 'prepared'
        game_directory = $GameDirectory; data_directory = $DataDirectory
        arguments = @(); process_id = $null; exit_code = $null; exit_code_hex = $null
        ended_utc = $null; notes = @(); binary_hashes = @{}; copied_logs = @()
    }
    $ReportPath = Join-Path $Session 'session.json'
    Write-Report $ReportPath $Report
    if ($Mode -eq 'Readback') {
        $document.SetValue('patches','realtime_texture_morphing','false')
        $document.SetValue('patches','hero_dog_texture_readback','true')
        $temp = $ConfigPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [IO.File]::WriteAllText($temp, $document.ToString(), $Utf8)
            # Do not overwrite a config changed by another editor/launcher while the prompt was open.
            if ([IO.File]::ReadAllText($ConfigPath) -cne $original) { throw 'Config changed during confirmation. Restart this tool; nothing was overwritten.' }
            [IO.File]::Replace($temp, $ConfigPath, $null)
        } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    }
    Copy-Configs (Join-Path $Session 'config-start')
    foreach ($name in @('fable_2.exe','rexruntime.dll','rexgpu-xenos.dll','rexgpu-xenos-vulkan.dll')) {
        $path = Join-Path $GameDirectory $name
        if ([IO.File]::Exists($path)) {
            $Report.binary_hashes[$name] = @{ sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); size = (Get-Item -LiteralPath $path).Length }
        }
    }
    $sourceStreams = @('Configuration inputs, NOT proof of effective GPU behaviour.', ('Requested mode: ' + $Mode))
    foreach ($key in @('REX_READBACK_RESOLVE_FORCE_ADDRESSES','REX_READBACK_RESOLVE')) {
        $value = [Environment]::GetEnvironmentVariable($key, 'Process')
        if ($null -eq $value) { $value = '<not set>' }
        $sourceStreams += $key + '=' + $value
    }
    [IO.File]::WriteAllLines((Join-Path $Session 'texture-request.txt'), [string[]]$sourceStreams, $Utf8)
    $oldLogs = @{}
    $logDir = Join-Path $GameDirectory 'logs'
    if ([IO.Directory]::Exists($logDir)) {
        foreach ($f in Get-ChildItem -LiteralPath $logDir -Filter '*.log' -File) {
            $oldLogs[$f.Name] = [string]$f.Length + ':' + [string]$f.LastWriteTimeUtc.Ticks
        }
    }
    # Windows paths cannot contain a double quote. Keep the entire argument quoted for spaces.
    $arguments = @('--game_data_root=' + $DataDirectory)
    if ($Mode -eq 'Readback') { $arguments += '--readback_resolve_force_addresses=0x12704000' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.WorkingDirectory = $GameDirectory
    $start.UseShellExecute = $false
    $start.Arguments = (($arguments | ForEach-Object { '"' + [regex]::Replace($_, '(\\+)$', '$1$1') + '"' }) -join ' ')
    $Report.arguments = $arguments
    $Process = New-Object Diagnostics.Process
    $Process.StartInfo = $start
    if (-not $Process.Start()) { throw 'Game process did not start.' }
    $Report.process_id = $Process.Id
    $Report.status = 'running'
    Write-Report $ReportPath $Report
    Write-Host 'Game started. Leave this PowerShell window open (you can minimize it).'
    $Process.WaitForExit()
    $code = [int]$Process.ExitCode
    $Report.exit_code = $code
    $Report.exit_code_hex = '0x{0:X8}' -f [BitConverter]::ToUInt32([BitConverter]::GetBytes($code), 0)
    $Report.ended_utc = [DateTime]::UtcNow.ToString('o')
    $Report.status = 'process_exited'
    try { Copy-Configs (Join-Path $Session 'config-end') } catch { $Report.notes += $_.Exception.Message }
    $evidence = @('Runtime excerpts; a missing line is not proof that a feature was disabled.')
    if ([IO.Directory]::Exists($logDir)) {
        $changed = @(Get-ChildItem -LiteralPath $logDir -Filter '*.log' -File | Where-Object {
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            (-not $oldLogs.ContainsKey($_.Name) -or $oldLogs[$_.Name] -ne ([string]$_.Length + ':' + [string]$_.LastWriteTimeUtc.Ticks))
        } | Sort-Object LastWriteTimeUtc -Descending)
        if ($changed.Count -gt 8) { $Report.notes += 'Only the 8 most recently changed logs were copied. Rotation may include a previous session.' }
        foreach ($f in ($changed | Select-Object -First 8)) {
            try {
                $target = Join-Path (Join-Path $Session 'logs') $f.Name
                $truncated = Copy-Bounded $f.FullName $target 32MB
                if ($truncated) { $Report.notes += 'Tail only (32 MiB): ' + $f.Name }
                $Report.copied_logs += $f.Name
                foreach ($line in [IO.File]::ReadLines($target)) {
                    if ($evidence.Count -ge 200) { break }
                    if ($line -match '\[texture-runtime\]|\[texture-mode\]|\[texture-morph\]|hero_dog_texture_readback') {
                        $evidence += $f.Name + ': ' + $line
                    }
                }
            } catch { $Report.notes += $_.Exception.Message }
        }
    }
    [IO.File]::WriteAllLines((Join-Path $Session 'texture-runtime.txt'), [string[]]$evidence, $Utf8)
    Write-Report $ReportPath $Report
    Write-Host ('Game exited: ' + $Report.exit_code_hex)
    Write-Host ('Report and config backup: ' + $Session)
    Write-Host 'Nothing was uploaded. Review paths/settings before sharing. No dump or save files are included.'
    exit 0
} catch {
    Write-Host ('ERROR: ' + $_.Exception.Message) -ForegroundColor Red
    if ($null -ne $Report -and $Session) {
        $Report.status = 'tool_error'
        $Report.notes += $_.Exception.Message
        try { Write-Report (Join-Path $Session 'session.json') $Report } catch { }
        Write-Host ('Existing backup/report: ' + $Session)
    }
    Write-Host 'No game binaries or saves were changed. The texture toggles may already have been saved if launch failed afterwards; restore them from config-before if necessary.'
    exit 1
} finally {
    if ($null -ne $Process) { $Process.Dispose() } # closes the handle; does not kill the game
}
