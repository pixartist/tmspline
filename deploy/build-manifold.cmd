@echo off
rem Builds the Manifold boolean library (C bindings) as manifoldc.dll for win-x64 and copies it next to TmSpline.Core.
rem Needs: git, CMake (pip install cmake), Visual Studio 2022 Build Tools (C++). Source: ..\..\manifold-src at tag v3.5.4.
setlocal
set SRC=%~dp0..\..\manifold-src
set OUT=%~dp0..\src\TmSpline.Core\runtimes\win-x64\native
if not exist "%SRC%" git clone https://github.com/elalish/manifold.git "%SRC%" || exit /b 1
git -C "%SRC%" checkout --quiet v3.5.4 || exit /b 1
set CMAKE=cmake
where cmake >nul 2>nul || set CMAKE=python -m cmake
%CMAKE% -S "%SRC%" -B "%SRC%\build" -G "Visual Studio 17 2022" -A x64 ^
  -DMANIFOLD_CBIND=ON -DMANIFOLD_TEST=OFF -DMANIFOLD_PAR=OFF -DMANIFOLD_CROSS_SECTION=ON ^
  -DMANIFOLD_PYBIND=OFF -DMANIFOLD_JSBIND=OFF -DBUILD_SHARED_LIBS=ON || exit /b 1
%CMAKE% --build "%SRC%\build" --config Release --parallel || exit /b 1
if not exist "%OUT%" mkdir "%OUT%"
for /r "%SRC%\build" %%f in (*.dll) do (
  echo %%f | findstr /i "\\Release\\" >nul && copy /y "%%f" "%OUT%" >nul
)
dir /b "%OUT%"
