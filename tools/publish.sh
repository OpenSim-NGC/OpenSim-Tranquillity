#!/bin/sh

dotnet publish Tranquillity.sln -c Release -r linux-x64 --self-contained false -o ./publish
dotnet publish Tranquillity.sln -c Release -r win-x64 --self-contained false -o ./publish