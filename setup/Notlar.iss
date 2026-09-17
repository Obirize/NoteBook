; Notlar kurulum betiği — Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Kullanıcı düzeyinde kurulur: yönetici izni istemez, HKLM'e ve Windows Not Defteri'ne dokunmaz.
; Derleme: build-setup.cmd (önce build.cmd ile app\ üretilir).

#define AppName "Notlar"
; Sürüm, derlenen uygulamadan okunur (csproj <Version> veya CI'da git etiketi).
#define FullVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\app\Notlar.exe")
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)
#define AppExe "Not Defteri.exe"
#define ProgId "Notlar.Text"

[Setup]
AppId={{7D2C1D5E-3F0B-4E5C-9C1A-6A0E4B2F9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Wallece
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Notlar-Kurulum-{#AppVersion}
SetupIconFile=..\notes.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
; Kaldırma sırasında data\ klasörü (şifreli notlar) korunur; Inno yalnızca kurduğu dosyaları siler.

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol ekle"; GroupDescription: "Kısayollar:"
Name: "txtopenwith"; Description: "Notlar'ı .txt dosyaları için ""Birlikte aç"" listesine ve Varsayılan uygulamalar'a ekle"; GroupDescription: "Windows ile bütünleşme:"
Name: "newnote"; Description: "Masaüstü ve klasör sağ tık menüsüne ""Yeni not (Notlar)"" ekle"; GroupDescription: "Windows ile bütünleşme:"

[Files]
Source: "..\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} — Kaldır"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Uygulama kimliği ve .txt için "Birlikte aç" — Windows'un Not Defteri kaydına dokunmaz, yanına eklenir.
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "Metin notu"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Metin notu"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueData: "Notlar ile aç"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""; Tasks: txtopenwith
; Ayarlar > Varsayılan uygulamalar listesinde görünmesi için (Default Programs kaydı).
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Şifreli, yerel not defteri"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".txt"; ValueData: "{#ProgId}"; Tasks: txtopenwith
; Sağ tık > "Yeni not (Notlar)" — masaüstü arka planı ve klasör arka planı. Dosya oluşturmaz, doğrudan not açar.
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\Notlar.NewNote"; ValueType: string; ValueData: "Yeni not (Notlar)"; Flags: uninsdeletekey; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\Notlar.NewNote"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\Notlar.NewNote\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --new"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Notlar.NewNote"; ValueType: string; ValueData: "Yeni not (Notlar)"; Flags: uninsdeletekey; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Notlar.NewNote"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Notlar.NewNote\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --new"; Tasks: newnote

[Run]
Filename: "{app}\{#AppExe}"; Description: "Notlar'ı başlat"; Flags: nowait postinstall skipifsilent
; Uygulama içinden sessiz güncellemede (/UPDATE=1) kurulum bitince uygulama kendiliğinden yeniden açılır.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsUpdate
; Windows, varsayılan uygulama seçimini yalnızca kullanıcıya bırakır; bu bağlantı Ayarlar sayfasını açar.
Filename: "ms-settings:defaultapps"; Description: ".txt için varsayılan uygulamayı seçmek üzere Ayarlar'ı aç"; Flags: shellexec nowait postinstall skipifsilent unchecked; Tasks: txtopenwith

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// Kurulum öncesi çalışan Notlar kapatılır (CloseApplications yalnızca kilitli dosyaları görür; başlatıcı da kapatılır).
procedure CurStepChanged(CurStep: TSetupStep);
var R: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Önce nazikçe kapat (uygulama son değişikliği kaydeder), sonra kalan varsa zorla.
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Notlar.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(2000);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Notlar.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  end;
end;
