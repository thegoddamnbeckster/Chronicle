@echo off
SET PATH=C:\Program Files\nodejs;%PATH%
cd /d "%~dp0"
node node_modules\vite\bin\vite.js
