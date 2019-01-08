@echo off
chcp 1252
msbuild -nologo -t:build -p:Configuration=Release 
msbuild -t:pack -p:Configuration=Release -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg