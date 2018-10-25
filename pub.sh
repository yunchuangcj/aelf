#!/bin/bash
echo publish project start
sudo dotnet publish --configuration Release -o ~/work/aelf/publish
echo publish finish
