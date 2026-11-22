@echo off
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Einrichtung.ps1" -SetupDirectory "%~dp0"
