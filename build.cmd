@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1
set "ANGLE_CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ANGLE_CSC%" set "ANGLE_CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%ANGLE_CSC%" (
    echo ERROR: .NET Framework compiler not found. Install .NET Framework 4.8.
    popd
    exit /b 1
)
if not exist "bin\Release" mkdir "bin\Release"
"%ANGLE_CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"bin\Release\AngleReader.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "src\AssemblyInfo.cs" "src\AngleReader.cs" "src\Steering.cs" "tests\SteeringTests.cs"
if errorlevel 1 (
    popd
    exit /b 1
)
copy /y "App.config" "bin\Release\AngleReader.exe.config" >nul
if errorlevel 1 (
    popd
    exit /b 1
)
echo Built: bin\Release\AngleReader.exe
popd
exit /b 0
