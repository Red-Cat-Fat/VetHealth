@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set "PYTHONIOENCODING=utf-8"
set "VPY=%~dp0.venv\Scripts\python.exe"

echo ================================================
echo   Сбор замеров давления с тонометра CONTEC08A
echo ================================================
echo.

if exist "%VPY%" goto check_libs

echo Первый запуск: готовлю программу, это займёт пару минут.
echo.
call :find_python
if defined PY goto make_venv

echo Python не найден. Пробую установить его через winget...
winget install -e --id Python.Python.3.12 --scope user --accept-package-agreements --accept-source-agreements
call :find_python
if defined PY goto make_venv
echo.
echo Не получилось найти или установить Python.
echo Установите Python 3 с https://www.python.org/downloads/
echo и отметьте галочку "Add python.exe to PATH", затем запустите этот файл снова.
goto end

:make_venv
echo Найден Python: %PY%
%PY% -m venv "%~dp0.venv"
if not exist "%VPY%" (
  echo Не удалось создать окружение .venv
  goto end
)

:check_libs
"%VPY%" -c "import hid, openpyxl, pypdf" >nul 2>&1
if not errorlevel 1 goto run
echo Устанавливаю библиотеки hidapi, openpyxl, pypdf...
"%VPY%" -m pip install --disable-pip-version-check -q hidapi openpyxl pypdf
"%VPY%" -c "import hid, openpyxl, pypdf" >nul 2>&1
if errorlevel 1 (
  echo Не удалось установить библиотеки. Проверьте интернет и запустите файл снова.
  goto end
)
echo Готово.
echo.

:run
echo Закройте BPMeter, подключите и включите аппарат.
pause
echo.
"%VPY%" "%~dp0bp_device.py" %*
goto end

:find_python
set "PY="
py -3 -c "import sys" >nul 2>&1 && set "PY=py -3" && exit /b
python -c "import sys" >nul 2>&1 && set "PY=python" && exit /b
for %%V in (313 312 311 310 39) do (
  if exist "%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe" (
    set "PY="%LOCALAPPDATA%\Programs\Python\Python%%V\python.exe""
    exit /b
  )
)
exit /b

:end
echo.
pause
