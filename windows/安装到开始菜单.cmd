@echo off
start "" /wait "%~dp0MediaProjection.exe" --install
exit /b %errorlevel%
