# Instalador de gard para Windows.
#
#   irm https://raw.githubusercontent.com/gard-net/gard/main/scripts/install.ps1 | iex
#
# Variables opcionales:
#   $env:GARD_VERSION = '0.3.0'          versión (default: último release)
#   $env:GARD_INSTALL_DIR = 'C:\tools'   destino (default: %LOCALAPPDATA%\Programs\gard)
#
# Baja el zip win-x64 del release, verifica su SHA256 contra SHA256SUMS, copia
# gard.exe y agrega la carpeta al PATH del usuario (no al del sistema; sin admin).
# Todo va dentro de una función: con `irm | iex` un `exit` cerraría la sesión.

function Install-Gard {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest es 10x más lento con la barra
    $repo = 'gard-net/gard'

    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($arch -ne 'X64' -and $arch -ne 'Arm64') {
        throw "gard-install: unsupported architecture: $arch"
    }
    # Windows on ARM ejecuta el binario x64 por emulación.
    $rid = 'win-x64'

    if ($env:GARD_VERSION) {
        $tag = 'v' + $env:GARD_VERSION.TrimStart('v')
    } else {
        $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
            -Headers @{ 'User-Agent' = 'gard-install' }
        $tag = $latest.tag_name
    }

    $asset = "gard-$tag-$rid.zip"
    $base = "https://github.com/$repo/releases/download/$tag"
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("gard-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        Write-Host "gard: downloading $asset"
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile (Join-Path $tmp $asset)
        Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS')

        $line = Get-Content (Join-Path $tmp 'SHA256SUMS') |
            Where-Object { $f = $_ -split '\s+'; $f.Count -ge 2 -and $f[1].TrimStart('*') -eq $asset } |
            Select-Object -First 1
        if (-not $line) { throw "gard-install: $asset is not listed in SHA256SUMS for $tag" }
        $expected = ($line -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
        if ($expected -ne $actual) {
            throw "gard-install: SHA256 mismatch for $asset (expected $expected, got $actual)"
        }

        Expand-Archive -Path (Join-Path $tmp $asset) -DestinationPath $tmp -Force
        $exe = Get-ChildItem -Path $tmp -Recurse -Filter 'gard.exe' | Select-Object -First 1
        if (-not $exe) { throw 'gard-install: archive does not contain gard.exe' }

        $dir = if ($env:GARD_INSTALL_DIR) { $env:GARD_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\gard' }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Copy-Item -Path $exe.FullName -Destination (Join-Path $dir 'gard.exe') -Force
        Write-Host "gard: installed to $(Join-Path $dir 'gard.exe')"

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $parts = @($userPath -split ';' | Where-Object { $_ })
        if ($parts -notcontains $dir) {
            [Environment]::SetEnvironmentVariable('Path', (($parts + $dir) -join ';'), 'User')
            $env:Path = "$env:Path;$dir"
            Write-Host "gard: added $dir to your user PATH (new terminals pick it up)"
        }

        & (Join-Path $dir 'gard.exe') --version
        Write-Host ''
        Write-Host '  Completions: gard completions powershell | Out-String | Invoke-Expression'
        Write-Host '  Get started: gard help'
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

Install-Gard
