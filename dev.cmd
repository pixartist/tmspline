@echo off
rem Runs the local dev stack (export server + editor), see dev.ps1. Arguments are passed on, e.g. dev -Wasm
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev.ps1" %*
