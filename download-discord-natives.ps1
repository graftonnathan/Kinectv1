# Download Discord.Net native audio libraries - FRESH COPY FROM SOURCE
Write-Host "?? Downloading FRESH Discord.Net native audio libraries from GitHub..."
Write-Host "?? This will replace any existing/corrupted libraries"

# Create directories
$libsDir = "libs"
$nativeDir = "native-libs"
$tempDir = "temp-discord-natives"

# Ensure directories exist
@($libsDir, $nativeDir, $tempDir) | ForEach-Object {
    if (!(Test-Path $_)) {
        New-Item -ItemType Directory -Path $_ -Force
        Write-Host "?? Created directory: $_"
    }
}

try {
    # Correct URL for x64 natives ZIP file
    $nativesZipUrl = "https://github.com/discord-net/Discord.Net/raw/dev/voice-natives/vnext_natives_win32_x64.zip"
    $zipFile = "$tempDir\vnext_natives_win32_x64.zip"
    
    Write-Host ""
    Write-Host "?? Downloading Discord.Net native libraries ZIP from official repository..."
    Write-Host "   Source: https://github.com/discord-net/Discord.Net/tree/dev/voice-natives"
    Write-Host "   Package: vnext_natives_win32_x64.zip"
    Write-Host ""
    
    # Download the ZIP file
    Write-Host "?? Downloading vnext_natives_win32_x64.zip..."
    try {
        Invoke-WebRequest -Uri $nativesZipUrl -OutFile $zipFile -UserAgent "PowerShell-Discord-Native-Downloader" -TimeoutSec 60
        Write-Host "? ZIP file downloaded successfully"
    } catch {
        Write-Host "? Error downloading ZIP file: $($_.Exception.Message)"
        throw "Failed to download native libraries ZIP file"
    }
    
    # Verify ZIP file was downloaded
    if (!(Test-Path $zipFile)) {
        throw "ZIP file download verification failed - file not found"
    }
    
    $zipFileInfo = Get-Item $zipFile
    Write-Host "?? Downloaded ZIP size: $($zipFileInfo.Length) bytes"
    
    if ($zipFileInfo.Length -lt 100000) {
        throw "ZIP file seems too small - possible download corruption"
    }
    
    # Extract the ZIP file
    Write-Host "?? Extracting native libraries from ZIP..."
    try {
        # Use .NET classes for ZIP extraction (PowerShell 5.0+)
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipFile, $tempDir)
        Write-Host "? ZIP file extracted successfully"
    } catch {
        Write-Host "? Error extracting ZIP: $($_.Exception.Message)"
        Write-Host "?? Trying alternative extraction method..."
        
        # Try using Expand-Archive cmdlet as fallback
        Expand-Archive -Path $zipFile -DestinationPath $tempDir -Force
        Write-Host "? ZIP file extracted successfully (alternative method)"
    }
    
    # Find the extracted DLL files (note: they're named libopus.dll and libsodium.dll in the ZIP)
    Write-Host "?? Locating extracted DLL files..."
    $extractedFiles = Get-ChildItem -Path $tempDir -Recurse -Include "*.dll"
    
    Write-Host "?? Found DLL files in ZIP:"
    $extractedFiles | ForEach-Object { Write-Host "   - $($_.Name)" }
    
    $libopusFile = $extractedFiles | Where-Object { $_.Name -eq "libopus.dll" } | Select-Object -First 1
    $libsodiumFile = $extractedFiles | Where-Object { $_.Name -eq "libsodium.dll" } | Select-Object -First 1
    
    if (!$libopusFile) {
        throw "libopus.dll not found in extracted files"
    }
    if (!$libsodiumFile) {
        throw "libsodium.dll not found in extracted files"
    }
    
    Write-Host "? Found libopus.dll: $($libopusFile.FullName)"
    Write-Host "? Found libsodium.dll: $($libsodiumFile.FullName)"
    
    # Verify file sizes before copying
    Write-Host ""
    Write-Host "?? Verifying extracted file integrity..."
    Write-Host "?? libopus.dll size: $($libopusFile.Length) bytes"
    Write-Host "?? libsodium.dll size: $($libsodiumFile.Length) bytes"
    
    if ($libopusFile.Length -lt 400000) {
        Write-Host "?? WARNING: libopus.dll seems smaller than expected"
    } else {
        Write-Host "? libopus.dll size verification passed"
    }
    
    if ($libsodiumFile.Length -lt 400000) {
        Write-Host "?? WARNING: libsodium.dll seems smaller than expected"
    } else {
        Write-Host "? libsodium.dll size verification passed"
    }
    
    # Copy to libs directory (rename libopus.dll to opus.dll for Discord.Net compatibility)
    Write-Host ""
    Write-Host "?? Copying fresh libraries to libs directory..."
    Copy-Item $libopusFile.FullName "$libsDir\opus.dll" -Force
    Copy-Item $libsodiumFile.FullName "$libsDir\libsodium.dll" -Force
    Write-Host "? Fresh libraries copied to $libsDir"
    Write-Host "   ?? libopus.dll ? opus.dll (renamed for Discord.Net compatibility)"
    Write-Host "   ?? libsodium.dll ? libsodium.dll"
    
    # Also copy to native-libs directory for backup
    Copy-Item $libopusFile.FullName "$nativeDir\opus.dll" -Force
    Copy-Item $libsodiumFile.FullName "$nativeDir\libsodium.dll" -Force
    Write-Host "? Backup copies created in $nativeDir"
    
    # Remove old corrupted files from lib directory if they exist
    if (Test-Path "lib\libopus.dll") {
        Remove-Item "lib\libopus.dll" -Force
        Write-Host "??? Removed old lib\libopus.dll"
    }
    if (Test-Path "lib\libsodium.dll") {
        Remove-Item "lib\libsodium.dll" -Force
        Write-Host "??? Removed old lib\libsodium.dll"
    }
    
    # Copy to output directories with correct names for Discord.Net
    Write-Host ""
    Write-Host "?? Installing to output directories..."
    
    $outputDirs = @("bin\Debug\net481", "bin\Release\net481", "bin\x64\Debug\net481", "bin\x64\Release\net481")
    foreach ($dir in $outputDirs) {
        if (!(Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force
            Write-Host "?? Created output directory: $dir"
        }
        
        # Copy with correct names for Discord.Net (opus.dll, not libopus.dll)
        Copy-Item "$libsDir\opus.dll" "$dir\opus.dll" -Force
        Copy-Item "$libsDir\libsodium.dll" "$dir\libsodium.dll" -Force
        Write-Host "? Installed to $dir"
    }
    
    # Final verification of installed files
    Write-Host ""
    Write-Host "?? Final verification of installed files..."
    
    $finalOpusFile = Get-Item "$libsDir\opus.dll"
    $finalLibsodiumFile = Get-Item "$libsDir\libsodium.dll"
    
    Write-Host "?? Final file verification:"
    Write-Host "   opus.dll: $($finalOpusFile.Length) bytes ?"
    Write-Host "   libsodium.dll: $($finalLibsodiumFile.Length) bytes ?"
    
    Write-Host ""
    Write-Host "?? Fresh Discord.Net native libraries installed successfully!"
    Write-Host ""
    Write-Host "?? Files installed to multiple locations:"
    Write-Host "   ?? libs\opus.dll (from libopus.dll - renamed for compatibility)"
    Write-Host "   ?? libs\libsodium.dll (fresh copy from ZIP)"
    Write-Host "   ?? native-libs\* (backup copies)"
    Write-Host "   ?? bin\Debug\net481\*.dll"
    Write-Host "   ?? bin\x64\Debug\net481\*.dll"
    Write-Host "   ?? (and Release variants)"
    Write-Host ""
    Write-Host "? Key improvements:"
    Write-Host "   ?? Downloaded from official Discord.Net ZIP package"
    Write-Host "   ?? Proper renaming: libopus.dll ? opus.dll (Discord.Net expects 'opus.dll')"
    Write-Host "   ??? Removed any old/corrupted files from lib directory"
    Write-Host "   ?? Correct naming for Discord.Net compatibility"
    Write-Host "   ?? Installed to all possible output directories including x64"
    Write-Host "   ?? File integrity verification at each step"
    Write-Host "   ?? Automatic extraction from official ZIP package"
    Write-Host ""
    Write-Host "?? Next steps:"
    Write-Host "   1. Restart your application"
    Write-Host "   2. Try the !join command in Discord"
    Write-Host "   3. Watch console for 'Native libraries loaded successfully' message"
    Write-Host "   4. If you still get DLL errors, check the console output for detailed diagnostics"
    
} catch {
    Write-Host ""
    Write-Host "? Error installing fresh libraries: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "?? Troubleshooting steps:"
    Write-Host "1. Check internet connection"
    Write-Host "2. Verify GitHub is accessible"
    Write-Host "3. Try running PowerShell as Administrator"
    Write-Host "4. Check Windows Defender/Antivirus (may block downloads/extraction)"
    Write-Host "5. Temporarily disable antivirus and retry"
    Write-Host ""
    Write-Host "?? Manual download URL:"
    Write-Host "   https://github.com/discord-net/Discord.Net/raw/dev/voice-natives/vnext_natives_win32_x64.zip"
    Write-Host ""
    Write-Host "?? Manual installation:"
    Write-Host "   1. Download the ZIP file from the URL above"
    Write-Host "   2. Extract libopus.dll and libsodium.dll"
    Write-Host "   3. Rename libopus.dll to opus.dll"
    Write-Host "   4. Place them in:"
    Write-Host "      - libs\opus.dll (renamed from libopus.dll)"
    Write-Host "      - libs\libsodium.dll"
    Write-Host "      - bin\Debug\net481\opus.dll"
    Write-Host "      - bin\Debug\net481\libsodium.dll"
    Write-Host "      - bin\x64\Debug\net481\opus.dll"
    Write-Host "      - bin\x64\Debug\net481\libsodium.dll"
    Write-Host ""
    Write-Host "?? CRITICAL: Discord.Net expects 'opus.dll' (not 'libopus.dll')"
} finally {
    # Clean up temporary files
    if (Test-Path $tempDir) {
        try {
            Remove-Item $tempDir -Recurse -Force
            Write-Host "?? Cleaned up temporary files"
        } catch {
            Write-Host "?? Could not clean up temporary directory: $tempDir"
        }
    }
}