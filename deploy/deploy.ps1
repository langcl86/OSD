<#
.SYNOPSIS
SpendMend OS Deployment v2
.DESCRIPTION
This script performs the following:
- Updates system BIOS
- Configures BIOS
- Prepares disk for OS deployment
- Applies golden image
- Installs device drivers 
- Installs deployment task sequence
.AUTHOR
clang@spendmend.com 
.REMARKS
- 2021-04-29 (clang): First created
- 2021-06-22 (clang): Implemented 'Flash64W.exe' used to update BIOS firmware. Standalone installers had unpredictable behaviour. 
- 2021-12-07 (clang): Revised event message helper function and path variables. 
- 2021-12-29 (clang): v.20 - Revised entire script and directory structure of deploymnet media. 
- 2021-12-29 (clang): Implemented https://docs.microsoft.com/en-us/powershell/scripting/developer/cmdlet/cmdlet-development-guidelines?view=powershell-7.2
- 2022-03-10 (clang): Fixed error in reboot function preventing some machines from booting to usb after reboot.
- 2023-01-27 (clang): Added prompt to continue if no drivers are found to prevent auto-start with no drivers.
#>

Add-Type -Path "OSD.dll"
$osdDrivers = [OSD.Drivers]::new();
$ErrorActionPreference = "SilentlyContinue"
$ComputerModel = (Get-CimInstance Win32_ComputerSystem).Model
$Root = (Get-Location).Drive.Root
$basePath = [System.IO.Path]::Combine($Root, "lib")
$scriptPath = [System.IO.Path]::Combine($root, "bin")
$DevPath = [System.IO.Path]::Combine($basePath, "dev", $ComputerModel)
$logPath = [System.IO.Path]::Combine($env:TEMP, "osd-logs")
$image = [System.IO.Path]::Combine($basePath, "wim", "w10.wim")

Start-Transcript -LiteralPath (Join-Path $logPath "OSD-Install.log") -Append | Out-Null
function eventInfo {
	Param (
    [Parameter(Mandatory=$true,Position=0,HelpMessage="Message Text")][string]$msg,
    [Parameter(Mandatory=$false,Position=1,HelpMessage="Message Color")]$color
	)
<#
.SYNOPSIS
	Displays information and events.
.DESCRIPTION
    This is a helper function to display information to the user. 
.EXAMPLE
    eventInfo "The event $x has failed" Red
#>
        if ([System.ConsoleColor].GetEnumValues() -contains $color) {
        $splat = @{ForegroundColor = $color}
        }

		
		Write-Host "`r`n+-----------------------------------------------------------------------+";
		Write-Host $msg @splat
		Write-Host "+-----------------------------------------------------------------------+`r`n";		
}
function cctk ([string]$cmd) {
<#
.SYNOPSIS
	Executes Dell Command Configure Tool Kit Command
.DESCRIPTION
    This function is used to read/configure BIOS settings using Dell CCTK.
	Function parameters at position 0 will be passed to 'cctk.exe'.
.EXAMPLE
	cctk --Asset		# Returns asset value in system BIOS
#>	
		X:\Command_Configure\x86_64\cctk.exe $cmd
}

function reboot {
<#
.SYNOPSIS
	Prepares environment to resume process after reboot. 
.DESCRIPTION
	- Device is 'marked' as in progress using asset tag in BIOS settings
	- Look for drive serial number in boot order list returned by CCTK
	- Read drive ID used by CCTK and attempt to set as first boot device 
	- Reboot System
	- Script searches for specified string in asset tag to resume session.
#>
    $assetFlag = "finish_" + $hostname;
    cctk --Asset=$assetFlag | Out-Null

<#	
	## Old method
 	$vol  = Get-Volume -FileSystemLabel "Image Data";
    $part = Get-Partition -DriveLetter $vol.DriveLetter;
    $disk = Get-Disk -Path $part.DiskPath;
    $match = $list -match "(usbhdd..)";
    $match = $Matches[0];	
#>
	
	$disk = Get-Volume -FileSystemLabel "Image Data" | Get-Partition | Get-Disk
    $list = cctk bootorder | Select-String -SimpleMatch $disk.SerialNumber
	$usb = [Regex]::Match($list, ("usbhdd..")).Value.Trim();	# This ID is not the same on every system.

    (X:\Command_Configure\x86_64\cctk.exe BootOrder --BootListType=uefi --sequence=$usb) | Out-Null
    wpeutil reboot;	
}

Set-BIOS ($type) {
    Write-Host "`r`n";
    if ((Get-CimInstance Win32_ComputerSystem).Manufacturer -match "Dell") {
        eventInfo "Configuring BIOS for $type..." Blue
    <#
        BIOS Settings Go Here
    #>
        cctk --Asset=$hostname
        cctk --EmbSataRaid=AHCI
        cctk --TpmSecurity=Enabled
		cctk "BootOrder --BootListType=uefi --sequence=hdd,usbhdd,embnicipv4 > null"		
    }

    else {
        eventInfo "BIOS configuration is not supported for this computer system." Yellow
    }
}

function Update-BIOS {
<#
.SYNOPSIS
	Updates BIOS firmware
.DESCRIPTION
	If firmware for the device exists on the media attempt to update using 'Flash64W.exe'.
	Not using Flash64W.exe can yeild unexpected results in Windows PE.
.NOTES
	https://www.dell.com/support/home/en-us/drivers/DriversDetails?driverId=V5PJD
#>
    Write-Host "`r`nSearching for BIOS update..." -NoNewline
	
	try {
	    [System.IO.FileInfo]$file = ([System.IO.DirectoryInfo]$DevPath).GetFiles("*.exe")[0]
	}
	catch {
		# Do nothing 
	}

    if (!$file.Exists) {
        Write-Host "Not Found" -ForegroundColor Yellow
		return		
	}

    $path = $file.FullName
    Write-Host "FOUND" -ForegroundColor Green
    eventInfo "Starting firmware update" Blue
    
    try {
        $log = Join-Path $logPath "bios.txt"
        $process = Start-Process -FilePath (Join-Path $scriptPath "Flash64W.exe") -ArgumentList "/b=`"$path`" /s /l=`"$log`"" -Verb RunAs -Wait -PassThru
        ##cctk --EmbSataRaid=AHCI 
        Get-Content $log
        if ($process.ExitCode -eq "2") {
            Write-Host "A reboot is required to complete the update process`r`nThe system will now restart" -ForegroundColor Yellow
            reboot
        }
    }

    catch {
        eventInfo "There was a problem while trying to update the BIOS" Red
        Write-Host $_.Exception.Message"`r`n" -ForegroundColor Yellow
        pause
    }
}

function PrepareDisk {
<#
.SYNOPSIS
	Prepares disk for image
.DESCRIPTION
	- Executes DISKPART script ".\diskpart.txt"
#>
    eventInfo "Preparing disk for imaging.." Blue;
	$disk = Get-Disk -Number 0
	if ($disk.BusType -ne "USB") {		## Don't Clean USB drive!
		diskpart /s '.\diskpart.txt'
    }

    else {
        Write-Host "Primary disk not detected." -ForeGround Red
        Write-Host "Process will resume after system reboot." -ForeGround Yellow
        reboot
    }
}
function DeployImage($img) {
<#
.SYNOPSIS
	Apply golden image 
.DESCRIPTION
	- Applies WIM using DISM
	- Configures boot files
	- Configures WindowsRE
#>
	eventInfo "Image Application Process Started" Blue
	try {
	## Windows Image 
			$log = Join-Path $logPath "dism-img.log"
			dism /LogPath:$log /Apply-Image /ImageFile:$img /Index:1 /ApplyDir:W:\
			if ($LASTEXITCODE -ne 0) { throw } 
			Write-Host "`r`nImage applied successfully.`r`n" -ForegroundColor Green
	## Boot
			Write-Host "Configuring boot partition...`r`n"
			W:\Windows\System32\bcdboot W:\Windows /s S:
			if ($LASTEXITCODE -ne 0) { throw }	
			Write-Host "Done`r`n" -ForegroundColor Green
	## Windows RE
			Write-Host "Setting up Windows RE...`r`n"
			New-Item -Path "R:\Recovery" -Name "WindowsRE" -ItemType "Directory" | Out-Null
			xcopy /h W:\Windows\System32\Recovery\Winre.wim R:\Recovery\WindowsRE\ /Y
			W:\Windows\System32\Reagentc /Setreimage /Path R:\Recovery\WindowsRE /Target W:\Windows
			W:\Windows\System32\Reagentc /Info /Target W:\Windows
			Write-Host "Done`r`n" -ForegroundColor Green			
	}

	catch {
		eventInfo "Image Process FAILED!" RED;			# f#*%
		pause
		EXIT 1
	}			
}

function Find-Drivers {
<#
.SYNOPSIS 
	Finds device drivers for appropriate model.
.DESCRIPTION
	- Displays a message to indicate presence of drivers
	- Returns CAB file for available device drivers
#>
	[System.IO.FileInfo]$drivers = ([System.IO.DirectoryInfo]$devPath).GetFiles("*.cab")[0]
	
    try {    
	    if ($drivers.Exists) {
		    Write-Host "`r`nDrivers found for "$ComputerModel"`r`nThese drivers will be applied to the image`r`n" -ForegroundColor Green
		    return $drivers
	    }
        elseif ($osdDrivers.NetworkEnabled) {
            if ($osdDrivers.GetDriverPackage($ComputerModel)) {  
                Write-Host "Device drivers will be downloaded after image has been applied`r`n";
                return $osdDrivers.DriverPackage;
            }
        }

        else { throw; }
    }
    catch {
    	Write-Host "`r`n!!! No drivers found for "$ComputerModel" !!!`r`nNo drivers will be installed`r`n" -ForegroundColor Red
		$cont = $null;
		while ($cont -ne "y" -and $cont -ne "n")
		{
			$cont = Read-Host "Would you like to continue anyways? (y/n)";
			if ($cont -eq "y") { wpeutil shutdown }
		}
		return $false
    }
}

function ApplyDrivers ($src) {
<# 
.SYNOPSIS
	Install available device drivers
.DESCRIPTION
	- CAB file is copied to %TEMP% on target system
	- CAB file is extracted and then removed
	- Drivers are added using DISM
.EXAMPLE 
	ApplyDrivers [System.IO.FileInfo]"C:\Temp\Latitude-3400.CAB"	
.NOTES
	- Drivers obtained from: https://www.dell.com/support/kbdoc/en-us/000124139/dell-command-deploy-driver-packs-for-enterprise-client-os-deployment
#>
	if (!$src.Exists) {
		return
	}

	eventInfo "Preparing to add drivers.." Blue

	if ($src.Extension -match ".CAB") {
		$path = "W:\Windows\Temp\drivers"
		New-Item -path $path -ItemType Directory | Out-Null

		Write-Host "Copying device drivers..." -NoNewline
		Copy-Item -Path $src.FullName -Destination $path | Out-Null
		Write-Host "Done" -ForegroundColor Green

		Write-Host "Extracting device drivers..." -NoNewline
#		$cab = (Get-ChildItem $dest | Select -first 1 -ExpandProperty FullName)	
		$cab = ([System.IO.DirectoryInfo]$path).GetFiles("*.cab")[0].FullName
		Expand $cab -F:* $path | Out-Null		
		Remove-Item $cab -force
		Write-Host "Done" -ForegroundColor Green
   `]

    else {
        $osdDrivers.TargetDevice = $path;
        $osdDrivers.getDrivers(); 
    }

		Write-Host "Installing device drivers..." -NoNewline
		$log = Join-Path $logPath "dism-drivers.log"
		dism /LogPath:$log /Image:W:\ /Add-Driver /Driver:$path /Recurse
		Write-Host "`r`nDriver Installation has been completed." -ForegroundColor Green
	}
}

function  Set-AnswerFile {
<# 
.SYNOPSIS
	Configures answerfile to set machine hostname
.DESCRIPTION
	- The hostname is applied using the Windows Setup Answer File (unattended.xml)
	- The golden image contains an answer file with the hostname 'RMAdmin-PC' 
	- The hostname string is replaced with the provided asset tag 
.NOTES
	https://docs.microsoft.com/en-us/windows-hardware/manufacture/desktop/update-windows-settings-and-scripts-create-your-own-answer-file-sxs
 #>
 Write-Host "Preparing answer file..." -NoNewline
 try {
	 $xmlFile = [System.IO.FileInfo]"W:\Windows\Panther\Unattend.xml"		
	 $xml = Get-Content -Raw $xmlFile							# Read Answer File
	 $xml = $xml -replace "RMAdmin-PC", $hostname				# Update Hostname
	 $utf8 = New-Object System.Text.UTF8Encoding $false			# UTF-8 No BOM
	 [System.IO.File]::WriteAllLines($xmlFile, $xml, $utf8)		# Save file 
	 Write-Host "Answer file has been updated. " -ForegroundColor Green
	 
 }
 catch {
	 Write-Host "Something bad happened" -ForegroundColor Red
	 Write-Host "There was a problem preparing the answerfile. This will likely result in the computer name not being set." -ForegroundColor Yellow
	 Write-Host $_.Exception.Message -ForeGround Yellow
	 pause
 }

}

function Install-TS {
<#
.SYNOPSIS
	Installs deployment task sequence files.
.DESCRIPTION
	- This function installs 'SetupComplete.cmd' and copies
	- The contents of the '.\tx\Temp' directory to C:\Temp. 
	- The files and applications copied over assist to prepare
	  the Operating System for a new user.  
.NOTES
	https://docs.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-a-custom-script-to-windows-setup
#>
	eventInfo "Installing Deployment Task Sequence"

	$txTmp = [System.IO.Path]::Combine($root, "tx", "Temp")
	$txSC = [System.IO.Path]::Combine($root, "tx", "SetupComplete.cmd")	

	Write-Host "Installing TS..." -NoNewline
	Copy-Item -Path $txTmp -Destination W:\ -Recurse -Force | Out-Null
	New-Item "W:\Windows\Setup\Scripts\" -ItemType "Directory" | Out-Null
	Copy-Item -Path $txSC -Destination W:\Windows\Setup\Scripts\SetupComplete.cmd | Out-Null
	Write-Host "Done" -ForegroundColor Green

	Write-Host "Configuring default app associations..." -NoNewline
	$log = Join-Path $logPath "dism-assoc.log"
	DISM /LogPath:$log /Quiet /Image:W:\ /Import-DefaultAppAssociations:W:\Temp\Scripts\AppAssociations.xml
	Write-Host "Done`r`n" -ForegroundColor Green
}

function Get-Asset {
	$asset = (cctk --Asset).Replace("Asset=", "").Trim()		# Clean string 
    $strFormat = '(^L-\d{4}$)|(^W-\d{4}$)'

	switch ($asset) 
	{
		{$_ -match "finish_"} 
		# Is this returning from reboot() ?		
			{																					
				$asset = $asset.Replace("finish_","").Trim();									# Remove prefix tag 
				Write-Host "`r`nContinuing installation for $asset `r`n";						# Resume using hostname retained in BIOS 
				$script:hostname = $asset
				reutn
			}

		{$_ -match $strFormat} 
		# Does valid asset tag already exist in BIOS?
			{
                Write-Host "`r`nAsset tag found: "$_"`r`n`r`n"  -BackgroundColor DarkGreen -ForegroundColor Yellow
			}

		default
			{
				while ($asset -notmatch $strFormat) {							## Continue on valid asset tag 
					$asset = Read-Host "Asset Tag "
				}
			}
	}

	$script:hostname = $asset	
	Update-BIOS
}

function  CopyLogs {
<#
.SYNOPSIS 
	Copies logfiles to temp location.
.DESCRIPTION
	Log files are copied to task sequece script path on target system.
#>
	Stop-Transcript | Out-Null
	Copy-Item -Path $logPath -Destination "W:\Temp\scripts\"  -Recurse  | Out-Null
}

function Main {
<#
	Script Main Entry Point 
#>

<#	$asset = (cctk --Asset).Trim("Asset=")

	if ($asset -match "finish_") {														# Is this returning from reboot() ?
		$hostname = $asset.Trim("finish_");												# Clean string and save asset tag
		Write-Host "`r`nContinuing installation for $hostname `r`n";					# Resume using hostname retained in BIOS 	
	}
	
	else {																## First-run, not returning

		while ($hostname -notmatch '(^L-\d{4}$)|(^W-\d{4}$)') {			## Continue on valid asset tag 
			$hostname = Read-Host "Asset Tag "
		}

		Update-BIOS
	}#>
	
	$drivers = Find-Drivers
	Get-Asset
	Set-BIOS $ComputerModel
	PrepareDisk
	DeployImage $image
	Set-AnswerFile
	ApplyDrivers $drivers
	Install-TS
	
	eventInfo "Process Complete" Green

	Stop-Transcript | Out-Null
	Copy-Item -Path $logPath -Destination "W:\Temp\scripts\"  -Recurse  | Out-Null
}

Main	
#pause wait
wpeutil reboot