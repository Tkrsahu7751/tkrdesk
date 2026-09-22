# Sample ApnaRemote.exe memory during an attended soak.
# Start Share/Connect first, then run this script.
param(
    [int]$Minutes = 30,
    [int]$IntervalSeconds = 30
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'ApnaRemote'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$csv = Join-Path $dir "soak-$stamp.csv"
"Utc,Pid,ProcessName,WorkingSetMB,PrivateMB" | Set-Content -Path $csv -Encoding utf8

$end = (Get-Date).AddMinutes($Minutes)
Write-Host "Soak logging until $end"
Write-Host "CSV: $csv"
Write-Host "Leave Apna Remote session running. Ctrl+C aborts logging only."

while ((Get-Date) -lt $end) {
    $procs = Get-Process -Name 'ApnaRemote' -ErrorAction SilentlyContinue
    if (-not $procs) {
        $line = "{0},,NONE,0,0" -f (Get-Date).ToUniversalTime().ToString('o')
        Add-Content -Path $csv -Value $line
        Write-Host "$(Get-Date -Format 'HH:mm:ss')  no ApnaRemote process"
    }
    else {
        foreach ($p in $procs) {
            $ws = [math]::Round($p.WorkingSet64 / 1MB, 1)
            $priv = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            $line = "{0},{1},{2},{3},{4}" -f (Get-Date).ToUniversalTime().ToString('o'), $p.Id, $p.ProcessName, $ws, $priv
            Add-Content -Path $csv -Value $line
            Write-Host "$(Get-Date -Format 'HH:mm:ss')  pid=$($p.Id)  WS=${ws}MB  Private=${priv}MB"
        }
    }
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Host "Done. Review: $csv"
