@echo off
setlocal
rem txt-kaydet.cmd ile eklenen kayitlari geri alir. Windows Not Defteri'ne dokunmaz.
reg delete "HKCU\Software\Classes\Notlar.Text" /f >nul 2>&1
reg delete "HKCU\Software\Classes\Applications\Not Defteri.exe" /f >nul 2>&1
reg delete "HKCU\Software\Classes\.txt\OpenWithProgids" /v "Notlar.Text" /f >nul 2>&1
echo Notlar'in .txt kaydi kaldirildi. Varsayilan uygulama Ayarlar'dan yeniden secilebilir.
