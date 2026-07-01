param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$DestRoot
)

$ErrorActionPreference = "Stop"

$AllowedExtensions = @(".meta", ".ymt", ".xml", ".lua")
$PlaceholderText = "empty test file"

$SourcePath = [System.IO.Path]::GetFullPath($SourcePath)
$DestRoot = [System.IO.Path]::GetFullPath($DestRoot)

if (-not (Test-Path -LiteralPath $SourcePath -PathType Container)) {
    throw "Source path is not a directory: $SourcePath"
}

New-Item -ItemType Directory -Force -Path $DestRoot | Out-Null

function Should-SkipDirectory {
    param([System.IO.DirectoryInfo]$Dir)

    # Skip dot-directories such as .git, .vscode, .cache, etc.
    return $Dir.Name.StartsWith(".")
}

function Get-RelativePath {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    $baseUri = [System.Uri]((Join-Path $BasePath "") -replace "\\", "/")
    $fullUri = [System.Uri]($FullPath -replace "\\", "/")
    return [System.Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($fullUri).ToString()
    ) -replace "/", "\"
}

function Copy-TreeOptimized {
    param(
        [string]$CurrentSource,
        [string]$CurrentDest
    )

    New-Item -ItemType Directory -Force -Path $CurrentDest | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $CurrentSource -Force) {
        # Resolve symlinks/junctions as actual target paths where possible.
        $resolvedPath = $item.FullName
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            try {
                $resolvedPath = (Resolve-Path -LiteralPath $item.FullName).ProviderPath
            }
            catch {
                Write-Warning "Skipping unresolved link: $($item.FullName)"
                continue
            }
        }

        if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
            $dirInfo = Get-Item -LiteralPath $resolvedPath -Force

            if (Should-SkipDirectory $dirInfo) {
                continue
            }

            $destDir = Join-Path $CurrentDest $item.Name
            Copy-TreeOptimized -CurrentSource $resolvedPath -CurrentDest $destDir
        }
        elseif (Test-Path -LiteralPath $resolvedPath -PathType Leaf) {
            $destFile = Join-Path $CurrentDest $item.Name
            $ext = [System.IO.Path]::GetExtension($item.Name).ToLowerInvariant()

            if ($AllowedExtensions -contains $ext) {
                Copy-Item -LiteralPath $resolvedPath -Destination $destFile -Force
            }
            else {
                # Lightweight equivalent of creating a small replacement file.
                # This avoids copying the original file and then overwriting it.
                Set-Content -LiteralPath $destFile -Value $PlaceholderText -NoNewline -Encoding UTF8
            }
        }
    }
}

Copy-TreeOptimized -CurrentSource $SourcePath -CurrentDest $DestRoot

Write-Host "Done."
Write-Host "Copied from: $SourcePath"
Write-Host "Created at:  $DestRoot"