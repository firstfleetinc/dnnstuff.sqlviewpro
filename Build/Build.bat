@echo off
echo.
set version=%1

mkdir logs

set buildconfig=Release

REM restore packages
nuget.exe restore ..\

REM build install zip file
Msbuild.exe ModuleSpecific.targets /p:VisualStudioVersion=%VisualStudioVersion%;Version=%version%;Configuration=%buildconfig%;TargetFrameworkVersion=v4.6.1 /t:Install /l:FileLogger,Microsoft.Build.Engine;logfile=logs\Build_%buildconfig%.log;verbosity=diagnostic
if ERRORLEVEL 1 goto end

:end
