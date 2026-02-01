# Download Discord.Net native audio libraries - FRESH COPY FROM SOURCE
Write-Host "?? Downloading FRESH Discord.Net native audio libraries from GitHub..."
Write-Host "?? This will replace any existing/corrupted libraries"

# Create directories
$tempDir = "temp-discord-natives"

# Ensure directories exist
@($tempDir) | ForEach-Object {
    if (!(Test-Path $_)) {
        New-Item -ItemType Directory -Path $_ -Force | Out-Null
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
    if (!(Test-Path $zipFile)) { throw "ZIP file download verification failed - file not found" }
    $zipFileInfo = Get-Item $zipFile
    Write-Host "?? Downloaded ZIP size: $($zipFileInfo.Length) bytes"
    if ($zipFileInfo.Length -lt 100000) { throw "ZIP file seems too small - possible download corruption" }
    
    # Extract the ZIP file
    Write-Host "?? Extracting native libraries from ZIP..."
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zipFile, $tempDir)
        Write-Host "? ZIP file extracted successfully"
    } catch {
        Write-Host "? Error extracting ZIP: $($_.Exception.Message)"
        Write-Host "?? Trying alternative extraction method..."
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
    
    if (!$libopusFile) { throw "libopus.dll not found in extracted files" }
    if (!$libsodiumFile) { throw "libsodium.dll not found in extracted files" }
    
    Write-Host "? Found libopus.dll: $($libopusFile.FullName)"
    Write-Host "? Found libsodium.dll: $($libsodiumFile.FullName)"
    
    # Verify file sizes before copying
    Write-Host ""
    Write-Host "?? Verifying extracted file integrity..."
    Write-Host "?? libopus.dll size: $($libopusFile.Length) bytes"
    Write-Host "?? libsodium.dll size: $($libsodiumFile.Length) bytes"
    if ($libopusFile.Length -lt 400000) { Write-Host "?? WARNING: libopus.dll seems smaller than expected" } else { Write-Host "? libopus.dll size verification passed" }
    if ($libsodiumFile.Length -lt 400000) { Write-Host "?? WARNING: libsodium.dll seems smaller than expected" } else { Write-Host "? libsodium.dll size verification passed" }
    
    # Copy to output directory (x64 Debug only as requested)
    Write-Host ""
    Write-Host "?? Installing to output directories..."
    $outputDirs = @("bin\x64\Debug\net481")
    foreach ($dir in $outputDirs) {
        if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item $libopusFile.FullName "$dir\opus.dll" -Force   # rename libopus.dll -> opus.dll
        Copy-Item $libsodiumFile.FullName "$dir\libsodium.dll" -Force
        Write-Host "? Installed to $dir"
    }
    
    # Final verification of installed files
    Write-Host ""
    Write-Host "?? Final verification of installed files..."
    foreach ($dir in $outputDirs) {
        $finalOpusFile = Get-Item "$dir\opus.dll"
        $finalLibsodiumFile = Get-Item "$dir\libsodium.dll"
        Write-Host "?? Final file verification for $dir:"
        Write-Host "   opus.dll: $($finalOpusFile.Length) bytes ?"
        Write-Host "   libsodium.dll: $($finalLibsodiumFile.Length) bytes ?"
    }
    
    Write-Host ""
    Write-Host "?? Fresh Discord.Net native libraries installed successfully!"
    Write-Host ""
    Write-Host "?? Files installed:"
    Write-Host "   ?? bin\x64\Debug\net481\opus.dll"
    Write-Host "   ?? bin\x64\Debug\net481\libsodium.dll"

    Write-Host ""
    Write-Host "? Key notes:"
    Write-Host "   ?? Downloaded from official Discord.Net ZIP package"
    Write-Host "   ?? Proper renaming: libopus.dll ? opus.dll (Discord.Net expects 'opus.dll')"
    Write-Host "   ?? Installed to x64 Debug output only"
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
    Write-Host "      - bin\x64\Debug\net481\opus.dll"
    Write-Host "      - bin\x64\Debug\net481\libsodium.dll"
    Write-Host ""
    Write-Host "?? CRITICAL: Discord.Net expects 'opus.dll' (not 'libopus.dll')"
} finally {
    # Clean up temporary files
    if (Test-Path $tempDir) {
        try { Remove-Item $tempDir -Recurse -Force; Write-Host "?? Cleaned up temporary files" }
        catch { Write-Host "?? Could not clean up temporary directory: $tempDir" }
    }
}