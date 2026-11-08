@echo off
chcp 65001 >nul
setlocal
set ROOT=%~dp0
set EXE=%ROOT%src\LatentFolio.App\bin\Release\net10.0-windows\LatentFolio.exe

if not exist "%EXE%" (
    echo 第一次运行，先编译 Release...
    dotnet build "%ROOT%LatentFolio.slnx" -c Release -v q --nologo
    if errorlevel 1 (
        echo.
        echo 编译失败。确认装了 .NET 10 SDK：https://dotnet.microsoft.com/download
        pause
        exit /b 1
    )
)

start "" "%EXE%"
endlocal
