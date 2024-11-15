@echo off

dotnet new tool-manifest --force
dotnet tool install inedo.extensionpackager

cd Azure\InedoExtension
dotnet inedoxpack pack . C:\LocalDev\ProGet\Extensions\Azure.upack --build=Debug -o
cd ..\..