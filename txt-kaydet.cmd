@echo off
setlocal
rem Notlar'i .txt dosyalari icin "Birlikte ac" listesine ekler (yalnizca bu Windows hesabi, yonetici gerekmez).
rem Varsayilan yapmak icin: Ayarlar > Uygulamalar > Varsayilan uygulamalar > Dosya turune gore > .txt > Notlar
set "EXE=%~dp0Not Defteri.exe"
reg add "HKCU\Software\Classes\Notlar.Text" /ve /d "Metin notu" /f >nul
reg add "HKCU\Software\Classes\Notlar.Text" /v "FriendlyTypeName" /d "Metin notu" /f >nul
reg add "HKCU\Software\Classes\Notlar.Text\DefaultIcon" /ve /d "\"%EXE%\",0" /f >nul
reg add "HKCU\Software\Classes\Notlar.Text\shell\open" /ve /d "Notlar ile ac" /f >nul
reg add "HKCU\Software\Classes\Notlar.Text\shell\open\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul
reg add "HKCU\Software\Classes\Applications\Not Defteri.exe" /v "FriendlyAppName" /d "Notlar" /f >nul
reg add "HKCU\Software\Classes\Applications\Not Defteri.exe\shell\open\command" /ve /d "\"%EXE%\" \"%%1\"" /f >nul
reg add "HKCU\Software\Classes\Applications\Not Defteri.exe\SupportedTypes" /v ".txt" /d "" /f >nul
reg add "HKCU\Software\Classes\.txt\OpenWithProgids" /v "Notlar.Text" /t REG_NONE /f >nul
echo Notlar, .txt icin "Birlikte ac" listesine eklendi.
echo Varsayilan yapmak icin: Ayarlar ^> Uygulamalar ^> Varsayilan uygulamalar ^> Dosya turune gore varsayilan ^> .txt ^> Notlar
