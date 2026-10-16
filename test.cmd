@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1
call build.cmd
if errorlevel 1 (
    popd
    exit /b 1
)
if not exist "test-output" mkdir "test-output"
start "" /wait "bin\Release\AngleReader.exe" --self-test "%CD%\test-output\self-test.txt"
set "ANGLE_TEST_EXIT=%ERRORLEVEL%"
if exist "test-output\self-test.txt" type "test-output\self-test.txt"
if not "%ANGLE_TEST_EXIT%"=="0" (
    echo ERROR: self-test failed.
    popd
    exit /b 1
)
if not exist "test-output\self-test.txt" (
    echo ERROR: test report was not created.
    popd
    exit /b 1
)
findstr /x /c:"Failures: 0" "test-output\self-test.txt" >nul
if errorlevel 1 (
    echo ERROR: the report did not confirm success.
    popd
    exit /b 1
)
echo Tests passed. No real keyboard input was sent.
popd
exit /b 0
