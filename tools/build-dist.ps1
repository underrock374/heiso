<#
  配布用の zip を作る(GitHub の Releases に置くもの)。
  使い方: .\tools\build-dist.ps1            (dist\ に書き出す)
          .\tools\build-dist.ps1 -OutDir D:\tmp\dist

  作るもの(<版> は csharp\Directory.Build.props の Version):
    HEISO-<版>-win-x64.zip        .NET のランタイム入り(展開するだけで動く。ふつうはこちら)
    HEISO-<版>-win-x64-light.zip  ランタイムなし(.NET 10 の Desktop Runtime と ASP.NET Core Runtime が要る)
    SHA256SUMS.txt                zip の SHA-256

  zip の中身:
    HeisoRecorder\  記録アプリ
    HeisoPlayer\    再生アプリ
    docs\           入れ方(install.md)・使い方(usage.md)・記録データの形式(session-format.md)と画像
    README.md / LICENSE / THIRD-PARTY-NOTICES.md / CHANGELOG.md

  個人設定(appsettings.Local.json)やログなど、配ってはいけないものが紛れ込んでいたら止める。
#>
param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "dist")
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# 版は Directory.Build.props の 1 か所だけ
[xml]$props = Get-Content (Join-Path $root "csharp\Directory.Build.props") -Encoding UTF8
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "csharp\Directory.Build.props に Version がありません" }
Write-Host "HEISO $version の配布用の zip を作ります → $OutDir"

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

# 配ってはいけないもの(見つけたら止める)
$forbidden = @("appsettings.Local.json", "*.pdb", "*.log", ".access_key", "fh6_registry*.json", "*.csv.gz", "*.raw.gz", "*.mp4", "*.heiso.zip")

$flavors = @(
    @{ Name = "HEISO-$version-win-x64";       SelfContained = "true";  Note = ".NET のランタイム入り" },
    @{ Name = "HEISO-$version-win-x64-light"; SelfContained = "false"; Note = "ランタイムなし" }
)

$sums = @()
foreach ($f in $flavors) {
    Write-Host ""
    Write-Host "== $($f.Name)($($f.Note))"
    $stage = Join-Path $OutDir "stage\$($f.Name)"
    New-Item -ItemType Directory -Path $stage | Out-Null

    foreach ($app in @(@{ Project = "Fh6.Recorder"; Folder = "HeisoRecorder" }, @{ Project = "Fh6.Player"; Folder = "HeisoPlayer" })) {
        $out = Join-Path $stage $app.Folder
        & dotnet publish (Join-Path $root "csharp\$($app.Project)") -c Release -r win-x64 --self-contained $f.SelfContained `
            -o $out -p:DebugType=none -p:GenerateDocumentationFile=false --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$($app.Project) を発行できませんでした" }
        # IIS 用の設定と、部品の説明の XML は要らない
        Remove-Item (Join-Path $out "web.config") -ErrorAction SilentlyContinue
        Get-ChildItem $out -Filter "*.xml" | Remove-Item
    }

    # 文書(README の docs\ へのリンクがそのまま使えるように、同じ並びで)
    New-Item -ItemType Directory -Path (Join-Path $stage "docs\images") | Out-Null
    foreach ($doc in @("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "CHANGELOG.md")) {
        Copy-Item (Join-Path $root $doc) $stage
    }
    foreach ($doc in @("install.md", "usage.md", "session-format.md")) {
        Copy-Item (Join-Path $root "docs\$doc") (Join-Path $stage "docs")
    }
    foreach ($dir in @("install", "usage")) {
        Copy-Item (Join-Path $root "docs\images\$dir") (Join-Path $stage "docs\images") -Recurse
    }

    # 配ってはいけないものが無いか、要るものがそろっているか
    $bad = foreach ($p in $forbidden) { Get-ChildItem $stage -Recurse -File -Filter $p }
    if ($bad) { throw "配ってはいけないファイルが入っています: $(($bad | ForEach-Object { $_.FullName }) -join ', ')" }
    foreach ($must in @("HeisoRecorder\HeisoRecorder.exe", "HeisoRecorder\appsettings.json", "HeisoRecorder\appsettings.Local.example.json",
                        "HeisoRecorder\wwwroot\index.html", "HeisoPlayer\HeisoPlayer.exe", "HeisoPlayer\wwwroot\index.html",
                        "HeisoPlayer\WebView2Loader.dll", "docs\install.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        if (-not (Test-Path (Join-Path $stage $must))) { throw "$must がありません" }
    }

    $zip = Join-Path $OutDir "$($f.Name).zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash  $($f.Name).zip"
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    $files = (Get-ChildItem $stage -Recurse -File).Count
    Write-Host "  $zip($mb MB、$files ファイル)"
}

Set-Content -Path (Join-Path $OutDir "SHA256SUMS.txt") -Value $sums -Encoding ascii
Remove-Item (Join-Path $OutDir "stage") -Recurse -Force
Write-Host ""
Write-Host "できました。SHA-256 は $(Join-Path $OutDir 'SHA256SUMS.txt')"
