param(
  [switch] $Arm64
)

$ErrorActionPreference = 'Stop'

$rid = if ($Arm64) { 'win-arm64' } else { 'win-x64' }
$outDir = "publish/$rid"

Write-Host "Publishing $rid single-file, self-contained..."

dotnet restore "Shype Login Server TCP.csproj"

dotnet publish "Shype Login Server TCP.csproj" `
  -c Release `
  -r $rid `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $outDir

Write-Host "Output: $outDir"
Get-ChildItem -Force $outDir
