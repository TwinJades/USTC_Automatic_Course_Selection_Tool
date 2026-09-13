$ErrorActionPreference = "Stop"

$workspace = $PSScriptRoot
$baselinePath = Join-Path $workspace "ROUTE1_SHA256_BASELINE.txt"
$failures = @()

foreach ($line in Get-Content -LiteralPath $baselinePath) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
        continue
    }

    $parts = $line -split "  ", 2
    if ($parts.Count -ne 2) {
        $failures += "无法解析基线行：$line"
        continue
    }

    $target = Join-Path $workspace $parts[1]
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        $failures += "文件缺失：$($parts[1])"
        continue
    }

    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $parts[0]) {
        $failures += "文件已变化：$($parts[1])"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "路线一保护基线验证通过。"
