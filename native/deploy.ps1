# 一键发布并同步到本地测试目录
#   用法：powershell -ExecutionPolicy Bypass -File deploy.ps1
#         powershell -ExecutionPolicy Bypass -File deploy.ps1 -SkipPublish   # 只同步不重新编译
# 目录约定：
#   release\人联启动器.exe   外层薄壳（几 KB，编译自 LauncherStub\Program.cs）
#   release\runtime\         真正的程序文件（几百个 dll 全部藏在这里）
#   release\RenlianData\     运行数据（配置 / 日志 / 缓存 / 备份），同步时不动
#   release\dist\            发布压缩包

param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$proj   = Join-Path $PSScriptRoot 'RenlianLauncher'
$csproj = Join-Path $proj 'RenlianLauncher.csproj'
$pub    = Join-Path $proj 'publish'
$target = Join-Path $proj 'release'
$runtime= Join-Path $target 'runtime'
$stub   = Join-Path $PSScriptRoot 'LauncherStub\Program.cs'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

foreach ($d in @($target, $runtime)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d | Out-Null }
}

# 1. 结束正在运行的测试实例，否则 exe 被占用无法覆盖
Get-Process | Where-Object { $_.ProcessName -eq '人联启动器' } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

# 2. 编译发布（文件夹版，启动最快）
if (-not $SkipPublish) {
    if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
    & $dotnet publish $csproj -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -o $pub
    if ($LASTEXITCODE -ne 0) { throw '发布失败，已中止同步' }
}

# 3. 程序文件镜像到 runtime\（会清理旧版本残留的 dll）
$rc = @($pub, $runtime, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XF', '*.pdb')
robocopy @rc | Out-Null
if ($LASTEXITCODE -ge 8) { throw "同步失败，robocopy 退出码 $LASTEXITCODE" }

# 4. 编译外层薄壳
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$stubExe = Join-Path $target '人联启动器.exe'
$stubIco = Join-Path $proj 'wwwroot\icon.ico'
& $csc /nologo /target:winexe /optimize+ "/out:$stubExe" "/win32icon:$stubIco" `
    /r:System.dll /r:System.Windows.Forms.dll $stub
if ($LASTEXITCODE -ne 0) { throw '外壳编译失败' }

# 5. 清掉根目录不属于「薄壳 + runtime + RenlianData + dist」的残留
$keep = @('RenlianData', 'runtime', 'dist', '人联启动器.exe')
Get-ChildItem $target -Force |
    Where-Object { $keep -notcontains $_.Name } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ('已同步到：' + $target) -ForegroundColor Green
Write-Host ('启动：' + (Join-Path $target '人联启动器.exe'))

# 6. 重新打包发布压缩包（只含 人联启动器.exe + runtime\，不含 RenlianData 测试数据）
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist '人联启动器_v1.7.2_原生版.zip'
$stage = Join-Path $env:TEMP ('renlian_pack_' + [guid]::NewGuid().ToString('N'))
Remove-Item $zip -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $target '人联启动器.exe') $stage -Force
Copy-Item (Join-Path $target 'runtime') $stage -Recurse -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ('压缩包：' + $zip + ' (' + $zipMb + ' MB)') -ForegroundColor Green
