@echo off
rem Starts this StellarDrive Demo as a dedicated server: no window, no player, a console for commands.
rem Copy this file next to StellarDrive.exe (with DedicatedServer-<version>.dll in Mods\) and run it.
rem Options (all optional):  -world <name or path to world.json>  -port <udp port>  -maxplayers <n>  -dsdata <folder>
rem Settings: UserData\DedicatedServer\dedicatedserver.conf (or use the Server Manager)   Type "help" in the console window for commands.
rem Keep this window open: when the server restarts itself ("restart" in its console, or a timed
rem restart), it exits with code 3 and this starts it again. "stop" (code 0) ends it for good.
cd /d "%~dp0"
title StellarDrive dedicated server (restarts it when asked)
:run
rem Unity empties its log as the game starts: keep the last run's (the server files it under UserData\DedicatedServer\logs).
if exist "UserData\DedicatedServer-Player.log" move /y "UserData\DedicatedServer-Player.log" "UserData\DedicatedServer-Player-prev.log" >nul
rem -nographics: no graphics device at all, so the server never waits on a GPU your game is using.
start "" /wait StellarDrive.exe -batchmode -nographics -dedicated -logFile "UserData\DedicatedServer-Player.log" %*
if %ERRORLEVEL% EQU 3 (
    echo %date% %time%  The server asked to restart. Starting it again...
    timeout /t 3 /nobreak >nul
    goto run
)
echo %date% %time%  The server stopped (exit code %ERRORLEVEL%).
