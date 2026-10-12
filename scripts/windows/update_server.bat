@echo off
rem Download / update the CS2 dedicated server (app 730). Edit the two paths if you used other folders (no spaces).
C:\steamcmd\steamcmd.exe +force_install_dir C:\cs2server +login anonymous +app_update 730 validate +quit
pause
