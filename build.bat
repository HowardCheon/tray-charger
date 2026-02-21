@echo off
chcp 65001 >nul
echo TrayCharger 빌드 중...

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [오류] .NET Framework csc.exe를 찾을 수 없습니다.
    pause
    exit /b 1
)

"%CSC%" /target:winexe /optimize+ ^
    /r:System.Windows.Forms.dll ^
    /r:System.Drawing.dll ^
    /out:TrayCharger.exe ^
    TrayCharger.cs

if errorlevel 1 (
    echo [오류] 빌드 실패
    pause
    exit /b 1
)

echo 빌드 완료: TrayCharger.exe
echo 실행하려면 TrayCharger.exe를 더블클릭하세요.
pause
