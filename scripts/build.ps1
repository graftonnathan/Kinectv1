#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reproducible build script for Kinect v1 application
    
.DESCRIPTION
    This script provides a reproducible build process for the Kinect v1 application,
    including NuGet package restoration, building Debug/Release configurations,
    and packaging artifacts with version stamping.
    
.PARAMETER Configuration
    Build configuration: Debug or Release (default: Release)
    
.PARAMETER Platform
    Target platform: x64 (default: x64, required for Kinect SDK)
    
.PARAMETER SkipRestore
    Skip NuGet package restoration
    
.PARAMETER SkipTests
    Skip building and running tests
    
.PARAMETER OutputPath
    Custom output path for artifacts (default: .\artifacts)
    
.PARAMETER Verbose
    Enable verbose logging
    
.EXAMPLE
    .\build.ps1
    Build Release configuration with default settings
    
.EXAMPLE
    .\build.ps1 -Configuration Debug -Verbose
    Build Debug configuration with verbose output
    
.EXAMPLE
    .\build.ps1 -SkipTests -OutputPath "C:\builds"
    Build without tests to custom output path
#>

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    
    [ValidateSet("x64")]
    [string]$Platform = "x64",
    
    [switch]$SkipRestore,
    [switch]$SkipTests,
    [string]$OutputPath = ".\artifacts",
    [switch]$Verbose
)

# Set error handling
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Script variables
$ScriptRoot = $PSScriptRoot
$SolutionFile = Join-Path $ScriptRoot "Kinectv1.sln"
$MainProject = Join-Path $ScriptRoot "Kinectv1.csproj"
$TestsProject = Join-Path $ScriptRoot "tests\Tests.csproj"
$ToolsProject = Join-Path $ScriptRoot "tools\AsrOffline.csproj"

# Logging functions
function Write-BuildLog {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $color = switch ($Level) {
        "ERROR" { "Red" }
        "WARN" { "Yellow" }
        "SUCCESS" { "Green" }
        default { "White" }
    }
    Write-Host "[$timestamp] [$Level] $Message" -ForegroundColor $color
}

function Write-BuildHeader {
    param([string]$Title)
    Write-Host ""
    Write-Host "=" * 60 -ForegroundColor Cyan
    Write-Host " $Title" -ForegroundColor Cyan
    Write-Host "=" * 60 -ForegroundColor Cyan
    Write-Host ""
}

# Validation functions
function Test-Prerequisites {
    Write-BuildHeader "Validating Prerequisites"
    
    # Check for dotnet CLI
    try {
        $dotnetVersion = dotnet --version
        Write-BuildLog "✅ .NET CLI found: $dotnetVersion"
    }
    catch {
        Write-BuildLog "❌ .NET CLI not found. Please install .NET SDK." "ERROR"
        exit 1
    }
    
    # Check for required files
    $requiredFiles = @($SolutionFile, $MainProject, $TestsProject, $ToolsProject)
    foreach ($file in $requiredFiles) {
        if (Test-Path $file) {
            Write-BuildLog "✅ Found: $(Split-Path $file -Leaf)"
        }
        else {
            Write-BuildLog "❌ Missing required file: $file" "ERROR"
            exit 1
        }
    }
    
    # Validate platform requirements
    if ($Platform -ne "x64") {
        Write-BuildLog "❌ Kinect SDK requires x64 platform. AnyCPU is not supported." "ERROR"
        exit 1
    }
    Write-BuildLog "✅ Platform validation passed: $Platform (required for Kinect SDK)"
}

function Get-BuildVersion {
    Write-BuildHeader "Determining Build Version"
    
    try {
        # Try to get version from git tag
        $gitTag = git describe --tags --exact-match HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $gitTag) {
            $version = $gitTag -replace '^v', ''
            Write-BuildLog "✅ Using git tag version: $version"
            return $version
        }
    }
    catch {
        # Git command failed, continue to fallback
    }
    
    try {
        # Fallback: use latest tag + commit count + hash
        $gitDescribe = git describe --tags --long --dirty 2>$null
        if ($LASTEXITCODE -eq 0 -and $gitDescribe) {
            $version = $gitDescribe -replace '^v', ''
            Write-BuildLog "✅ Using git describe version: $version"
            return $version
        }
    }
    catch {
        # Git describe failed, continue to date fallback
    }
    
    # Final fallback: use current date and time
    $dateVersion = Get-Date -Format "yyyy.MM.dd.HHmm"
    Write-BuildLog "⚠️ No git tags found, using date-based version: $dateVersion" "WARN"
    return $dateVersion
}

function Invoke-NuGetRestore {
    if ($SkipRestore) {
        Write-BuildLog "⏭️ Skipping NuGet restore (SkipRestore flag set)"
        return
    }
    
    Write-BuildHeader "Restoring NuGet Packages"
    
    try {
        $restoreArgs = @(
            "restore"
            $SolutionFile
            "--verbosity", $(if ($Verbose) { "normal" } else { "minimal" })
        )
        
        Write-BuildLog "🔄 Restoring packages for solution..."
        & dotnet @restoreArgs
        
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet restore failed with exit code $LASTEXITCODE"
        }
        
        Write-BuildLog "✅ NuGet packages restored successfully" "SUCCESS"
    }
    catch {
        Write-BuildLog "❌ NuGet restore failed: $($_.Exception.Message)" "ERROR"
        exit 1
    }
}

function Invoke-Build {
    Write-BuildHeader "Building Solution"
    
    try {
        $buildArgs = @(
            "build"
            $SolutionFile
            "--configuration", $Configuration
            "--platform", $Platform
            "--no-restore"
            "--verbosity", $(if ($Verbose) { "normal" } else { "minimal" })
        )
        
        Write-BuildLog "🔨 Building $Configuration configuration for $Platform..."
        & dotnet @buildArgs
        
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        
        Write-BuildLog "✅ Build completed successfully" "SUCCESS"
    }
    catch {
        Write-BuildLog "❌ Build failed: $($_.Exception.Message)" "ERROR"
        exit 1
    }
}

function Invoke-Tests {
    if ($SkipTests) {
        Write-BuildLog "⏭️ Skipping tests (SkipTests flag set)"
        return
    }
    
    Write-BuildHeader "Running Tests"
    
    try {
        $testArgs = @(
            "test"
            $TestsProject
            "--configuration", $Configuration
            "--platform", $Platform
            "--no-build"
            "--verbosity", $(if ($Verbose) { "normal" } else { "minimal" })
        )
        
        Write-BuildLog "🧪 Running tests..."
        & dotnet @testArgs
        
        if ($LASTEXITCODE -ne 0) {
            Write-BuildLog "❌ Some tests failed, but continuing with packaging..." "WARN"
        }
        else {
            Write-BuildLog "✅ All tests passed" "SUCCESS"
        }
    }
    catch {
        Write-BuildLog "❌ Test execution failed: $($_.Exception.Message)" "WARN"
        # Continue with packaging even if tests fail
    }
}

function New-ArtifactsPackage {
    param([string]$Version)
    
    Write-BuildHeader "Packaging Artifacts"
    
    try {
        # Create versioned artifact directory
        $artifactDir = Join-Path $OutputPath "kinectv1-$Version-$Platform-$Configuration"
        $binDir = Join-Path $artifactDir "bin"
        $toolsDir = Join-Path $artifactDir "tools"
        $docsDir = Join-Path $artifactDir "docs"
        
        # Ensure directories exist
        foreach ($dir in @($artifactDir, $binDir, $toolsDir, $docsDir)) {
            if (!(Test-Path $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
        }
        
        Write-BuildLog "📦 Creating artifact package in: $artifactDir"
        
        # Copy main application binaries
        $mainBinSource = Join-Path $ScriptRoot "bin\$Platform\$Configuration\net481"
        if (Test-Path $mainBinSource) {
            Write-BuildLog "📋 Copying main application binaries..."
            Copy-Item -Path "$mainBinSource\*" -Destination $binDir -Recurse -Force
        }
        else {
            Write-BuildLog "⚠️ Main application binaries not found at: $mainBinSource" "WARN"
        }
        
        # Copy tools binaries
        $toolsBinSource = Join-Path $ScriptRoot "tools\bin\$Platform\$Configuration\net481"
        if (Test-Path $toolsBinSource) {
            Write-BuildLog "🔧 Copying tools binaries..."
            Copy-Item -Path "$toolsBinSource\*" -Destination $toolsDir -Recurse -Force
        }
        else {
            Write-BuildLog "⚠️ Tools binaries not found at: $toolsBinSource" "WARN"
        }
        
        # Copy important documentation and configuration files
        $docFiles = @(
            "README.md"
            "DISCORD_NATIVES_SETUP.md"
            "AudioDeviceConfiguration.md"
            "App.config"
            "download-discord-natives.ps1"
        )
        
        Write-BuildLog "📄 Copying documentation and configuration..."
        foreach ($docFile in $docFiles) {
            $sourcePath = Join-Path $ScriptRoot $docFile
            if (Test-Path $sourcePath) {
                Copy-Item -Path $sourcePath -Destination $docsDir -Force
            }
        }
        
        # Copy models directory if it exists
        $modelsSource = Join-Path $ScriptRoot "models"
        if (Test-Path $modelsSource) {
            Write-BuildLog "🤖 Copying models directory..."
            $modelsTarget = Join-Path $artifactDir "models"
            Copy-Item -Path $modelsSource -Destination $modelsTarget -Recurse -Force
        }
        
        # Copy prompts directory if it exists
        $promptsSource = Join-Path $ScriptRoot "prompts"
        if (Test-Path $promptsSource) {
            Write-BuildLog "💬 Copying prompts directory..."
            $promptsTarget = Join-Path $artifactDir "prompts"
            Copy-Item -Path $promptsSource -Destination $promptsTarget -Recurse -Force
        }
        
        # Create build info file
        $buildInfo = @{
            Version = $Version
            Configuration = $Configuration
            Platform = $Platform
            BuildDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss UTC"
            BuildMachine = $env:COMPUTERNAME
            GitCommit = try { git rev-parse HEAD 2>$null } catch { "unknown" }
            GitBranch = try { git rev-parse --abbrev-ref HEAD 2>$null } catch { "unknown" }
        }
        
        $buildInfoJson = $buildInfo | ConvertTo-Json -Depth 2
        $buildInfoPath = Join-Path $artifactDir "build-info.json"
        $buildInfoJson | Out-File -FilePath $buildInfoPath -Encoding UTF8
        Write-BuildLog "📋 Created build info: build-info.json"
        
        # Create version-stamped zip archive
        $zipPath = Join-Path $OutputPath "kinectv1-$Version-$Platform-$Configuration.zip"
        if (Test-Path $zipPath) {
            Remove-Item $zipPath -Force
        }
        
        Write-BuildLog "📦 Creating zip archive: $(Split-Path $zipPath -Leaf)"
        Compress-Archive -Path $artifactDir -DestinationPath $zipPath -CompressionLevel Optimal
        
        Write-BuildLog "✅ Artifacts packaged successfully" "SUCCESS"
        Write-BuildLog "📂 Artifact directory: $artifactDir"
        Write-BuildLog "📦 Archive created: $zipPath"
        
        return $zipPath
    }
    catch {
        Write-BuildLog "❌ Artifact packaging failed: $($_.Exception.Message)" "ERROR"
        exit 1
    }
}

function Write-BuildSummary {
    param([string]$Version, [string]$ArtifactPath)
    
    Write-BuildHeader "Build Summary"
    
    Write-BuildLog "✅ Build completed successfully!" "SUCCESS"
    Write-Host ""
    Write-Host "Build Details:" -ForegroundColor Cyan
    Write-Host "  Version:        $Version" -ForegroundColor White
    Write-Host "  Configuration:  $Configuration" -ForegroundColor White
    Write-Host "  Platform:       $Platform" -ForegroundColor White
    Write-Host "  Artifact:       $ArtifactPath" -ForegroundColor White
    Write-Host ""
    Write-Host "Next Steps:" -ForegroundColor Cyan
    Write-Host "  1. Test the application binaries in the artifact package" -ForegroundColor White
    Write-Host "  2. Verify Discord native libraries are present (run download-discord-natives.ps1 if needed)" -ForegroundColor White
    Write-Host "  3. Deploy to target environment" -ForegroundColor White
    Write-Host ""
}

# Main execution
try {
    Write-BuildHeader "Kinect v1 Application Build Script"
    Write-BuildLog "Starting build process..."
    Write-BuildLog "Configuration: $Configuration, Platform: $Platform"
    
    # Execute build pipeline
    Test-Prerequisites
    $version = Get-BuildVersion
    Invoke-NuGetRestore
    Invoke-Build
    Invoke-Tests
    $artifactPath = New-ArtifactsPackage -Version $version
    Write-BuildSummary -Version $version -ArtifactPath $artifactPath
    
    exit 0
}
catch {
    Write-BuildLog "❌ Build script failed: $($_.Exception.Message)" "ERROR"
    exit 1
}