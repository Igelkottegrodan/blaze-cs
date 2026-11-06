@if not exist "%~dp0artifacts\bin\blaze-cs-client.exe" goto managed
@"%~dp0artifacts\bin\blaze-cs-client.exe" %*
@exit /b %errorlevel%
:managed
@dotnet "%~dp0artifacts\bin\blaze-cs.dll" %*
