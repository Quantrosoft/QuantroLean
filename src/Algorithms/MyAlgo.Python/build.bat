@echo off
REM MIT License
REM Copyright (c) 2025 Quantrosoft Pte. Ltd.
REM
REM Permission is hereby granted, free of charge, to any person obtaining a copy
REM of this software and associated documentation files (the "Software"), to deal
REM in the Software without restriction, including without limitation the rights
REM to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
REM copies of the Software, and to permit persons to whom the Software is
REM furnished to do so, subject to the following conditions:
REM
REM The above copyright notice and this permission notice shall be included in all
REM copies or substantial portions of the Software.
REM
REM THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
REM IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
REM FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
REM AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
REM LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
REM OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
REM SOFTWARE.
rem Byte-compile the Python algorithms in this folder and copy the .pyc files next to MyLauncher.
rem Argument 1: build configuration (Debug or Release), default Debug.
set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Debug

python -m compileall -lq "%~dp0."
if exist "%~dp0..\..\Lean\MyLauncher\bin\%CONFIG%" copy /y "%~dp0__pycache__\*.pyc" "%~dp0..\..\Lean\MyLauncher\bin\%CONFIG%" >NUL

rem Errors are intentionally discarded. This line ensures the exit code is 0.
exit /b 0
