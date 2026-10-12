@echo off
rem Local LAN test server. Copy this file to C:\cs2server\ and double-click it. Type commands in this window.
rem Join from CS2 with the developer console:  connect 127.0.0.1
cd /d C:\cs2server
game\bin\win64\cs2.exe -dedicated -console -insecure +sv_lan 1 +game_type 0 +game_mode 0 +map de_dust2
pause
