$ErrorActionPreference = "Stop"

$workspace = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$baselinePath = Join-Path $PSScriptRoot "ROUTE1_SHA256_BASELINE.txt"
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

    if ($parts[1].StartsWith("src/UstcCourseAssistant/", [StringComparison]::OrdinalIgnoreCase)) {
        # Git checks out these text files with CRLF; the baseline hashes Git's LF form.
        $bytes = [System.IO.File]::ReadAllBytes($target)
        $normalized = [System.IO.MemoryStream]::new()
        for ($index = 0; $index -lt $bytes.Length; $index++) {
            if ($bytes[$index] -eq 13 -and $index + 1 -lt $bytes.Length -and $bytes[$index + 1] -eq 10) {
                continue
            }
            $normalized.WriteByte($bytes[$index])
        }
        $actual = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($normalized.ToArray())).ToLowerInvariant()
        $normalized.Dispose()
    }
    else {
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($actual -ne $parts[0]) {
        $failures += "文件已变化：$($parts[1])"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "路线一保护基线验证通过。"
