@echo off
setlocal
cd /d "%~dp0"

echo Limpando builds anteriores...
dotnet clean -c Release >nul 2>&1
if exist "bin\Release\net8.0-windows\win-x64\publish" rmdir /s /q "bin\Release\net8.0-windows\win-x64\publish"

echo.
echo Publicando AppInstall em arquivo unico...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

echo.
echo ============================================================
echo A pasta publish deve conter apenas o AppInstall.exe
echo ^(e, dependendo do SDK, nenhum PDB^).
echo.
echo programas.json sera criado automaticamente somente quando
echo voce usar "Adicionar ao programas.json".
echo ============================================================
pause
