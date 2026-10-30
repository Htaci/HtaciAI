@echo off
setlocal
rem 把编译产物里「宿主真正需要的那几个文件」部署到宿主插件目录。
rem WinForms/Wpf 两个 dll 是为别的 UI 框架准备的，宿主用不到，带过去只多一份加载风险。

set SRC=%~dp0bin\Release\net10.0
set DST=D:\App\HtaciApp\data\plugins\htaci.browser

if not exist "%SRC%\HtaciAI.WebBrowser.dll" (
  echo [x] 先 dotnet build -c Release
  exit /b 1
)

if not exist "%DST%" mkdir "%DST%"

copy /Y "%SRC%\HtaciAI.WebBrowser.dll" "%DST%\" >nul
copy /Y "%SRC%\plugin.json" "%DST%\" >nul
copy /Y "%SRC%\Microsoft.Web.WebView2.Core.dll" "%DST%\" >nul
copy /Y "%SRC%\WebView2Loader.dll" "%DST%\" >nul

echo [ok] 已部署到 %DST%
dir /b "%DST%"
