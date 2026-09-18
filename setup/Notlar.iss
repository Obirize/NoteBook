; NoteBook installer — Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Per-user install: no administrator rights, no HKLM, Windows Notepad is left untouched.
; Build: build-setup.cmd (build.cmd first produces app\).

#define AppName "NoteBook"
; Version comes from the built executable (csproj <Version>, or the git tag on CI).
#define FullVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\app\Notlar.exe")
#define AppVersion Copy(FullVersion, 1, RPos(".", FullVersion) - 1)
#define AppExe "Not Defteri.exe"
#define ProgId "NoteBook.Text"

[Setup]
AppId={{7D2C1D5E-3F0B-4E5C-9C1A-6A0E4B2F9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Wallece
AppPublisherURL=https://github.com/Obirize/NoteBook
AppSupportURL=https://github.com/Obirize/NoteBook/issues
AppUpdatesURL=https://github.com/Obirize/NoteBook/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=NoteBook-Setup-{#AppVersion}
SetupIconFile=..\notes.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
; The data\ folder (encrypted notes) survives uninstall; Inno only removes files it installed.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
english.Shortcuts=Shortcuts:
english.Integration=Windows integration:
english.TaskDesktop=Create a desktop shortcut
english.TaskTxt=Add NoteBook to the "Open with" list and Default apps for .txt files
english.TaskNewNote=Add "New note (NoteBook)" to the desktop and folder right-click menu
english.TextNote=Text note
english.OpenWith=Open with NoteBook
english.NewNote=New note (NoteBook)
english.AppDescription=Encrypted local notebook
english.RunApp=Launch NoteBook
english.OpenSettings=Open Settings to choose the default app for .txt files
english.TaskStartup=Start NoteBook with Windows (in the notification area)
turkish.Shortcuts=Kısayollar:
turkish.Integration=Windows ile bütünleşme:
turkish.TaskDesktop=Masaüstüne kısayol ekle
turkish.TaskTxt=NoteBook'u .txt dosyaları için "Birlikte aç" listesine ve Varsayılan uygulamalar'a ekle
turkish.TaskNewNote=Masaüstü ve klasör sağ tık menüsüne "Yeni not (NoteBook)" ekle
turkish.TextNote=Metin notu
turkish.OpenWith=NoteBook ile aç
turkish.NewNote=Yeni not (NoteBook)
turkish.AppDescription=Şifreli, yerel not defteri
turkish.RunApp=NoteBook'u başlat
turkish.OpenSettings=.txt için varsayılan uygulamayı seçmek üzere Ayarlar'ı aç
turkish.TaskStartup=NoteBook'u Windows ile başlat (bildirim alanında)
spanish.Shortcuts=Accesos directos:
spanish.Integration=Integración con Windows:
spanish.TaskDesktop=Crear un acceso directo en el escritorio
spanish.TaskTxt=Añadir NoteBook a la lista "Abrir con" y a Aplicaciones predeterminadas para archivos .txt
spanish.TaskNewNote=Añadir "Nueva nota (NoteBook)" al menú contextual del escritorio y las carpetas
spanish.TextNote=Nota de texto
spanish.OpenWith=Abrir con NoteBook
spanish.NewNote=Nueva nota (NoteBook)
spanish.AppDescription=Bloc de notas local cifrado
spanish.RunApp=Iniciar NoteBook
spanish.OpenSettings=Abrir Configuración para elegir la aplicación predeterminada de .txt
spanish.TaskStartup=Iniciar NoteBook con Windows (en el área de notificación)
brazilianportuguese.Shortcuts=Atalhos:
brazilianportuguese.Integration=Integração com o Windows:
brazilianportuguese.TaskDesktop=Criar atalho na área de trabalho
brazilianportuguese.TaskTxt=Adicionar o NoteBook à lista "Abrir com" e aos Aplicativos padrão para arquivos .txt
brazilianportuguese.TaskNewNote=Adicionar "Nova nota (NoteBook)" ao menu de contexto da área de trabalho e das pastas
brazilianportuguese.TextNote=Nota de texto
brazilianportuguese.OpenWith=Abrir com o NoteBook
brazilianportuguese.NewNote=Nova nota (NoteBook)
brazilianportuguese.AppDescription=Bloco de notas local criptografado
brazilianportuguese.RunApp=Iniciar o NoteBook
brazilianportuguese.OpenSettings=Abrir Configurações para escolher o aplicativo padrão de .txt
brazilianportuguese.TaskStartup=Iniciar o NoteBook com o Windows (na área de notificação)
russian.Shortcuts=Ярлыки:
russian.Integration=Интеграция с Windows:
russian.TaskDesktop=Создать ярлык на рабочем столе
russian.TaskTxt=Добавить NoteBook в список «Открыть с помощью» и в приложения по умолчанию для файлов .txt
russian.TaskNewNote=Добавить «Новая заметка (NoteBook)» в контекстное меню рабочего стола и папок
russian.TextNote=Текстовая заметка
russian.OpenWith=Открыть в NoteBook
russian.NewNote=Новая заметка (NoteBook)
russian.AppDescription=Зашифрованный локальный блокнот
russian.RunApp=Запустить NoteBook
russian.OpenSettings=Открыть «Параметры», чтобы выбрать приложение по умолчанию для .txt
russian.TaskStartup=Запускать NoteBook вместе с Windows (в области уведомлений)
japanese.Shortcuts=ショートカット:
japanese.Integration=Windows との連携:
japanese.TaskDesktop=デスクトップにショートカットを作成
japanese.TaskTxt=.txt ファイルの「プログラムから開く」と既定のアプリに NoteBook を追加
japanese.TaskNewNote=デスクトップとフォルダーの右クリック メニューに「新規ノート (NoteBook)」を追加
japanese.TextNote=テキスト ノート
japanese.OpenWith=NoteBook で開く
japanese.NewNote=新規ノート (NoteBook)
japanese.AppDescription=暗号化されたローカル ノート
japanese.RunApp=NoteBook を起動
japanese.OpenSettings=設定を開いて .txt の既定のアプリを選ぶ
japanese.TaskStartup=Windows と同時に NoteBook を起動（通知領域に）
german.Shortcuts=Verknüpfungen:
german.Integration=Windows-Integration:
german.TaskDesktop=Desktop-Verknüpfung erstellen
german.TaskTxt=NoteBook zur Liste „Öffnen mit“ und zu den Standard-Apps für .txt-Dateien hinzufügen
german.TaskNewNote=„Neue Notiz (NoteBook)“ zum Rechtsklick-Menü von Desktop und Ordnern hinzufügen
german.TextNote=Textnotiz
german.OpenWith=Mit NoteBook öffnen
german.NewNote=Neue Notiz (NoteBook)
german.AppDescription=Verschlüsseltes lokales Notizbuch
german.RunApp=NoteBook starten
german.OpenSettings=Einstellungen öffnen, um die Standard-App für .txt zu wählen
german.TaskStartup=NoteBook mit Windows starten (im Infobereich)
french.Shortcuts=Raccourcis :
french.Integration=Intégration à Windows :
french.TaskDesktop=Créer un raccourci sur le Bureau
french.TaskTxt=Ajouter NoteBook à la liste « Ouvrir avec » et aux applications par défaut pour les fichiers .txt
french.TaskNewNote=Ajouter « Nouvelle note (NoteBook) » au menu contextuel du Bureau et des dossiers
french.TextNote=Note texte
french.OpenWith=Ouvrir avec NoteBook
french.NewNote=Nouvelle note (NoteBook)
french.AppDescription=Bloc-notes local chiffré
french.RunApp=Lancer NoteBook
french.OpenSettings=Ouvrir les Paramètres pour choisir l'application par défaut des .txt
french.TaskStartup=Lancer NoteBook avec Windows (dans la zone de notification)
korean.Shortcuts=바로 가기:
korean.Integration=Windows 통합:
korean.TaskDesktop=바탕 화면 바로 가기 만들기
korean.TaskTxt=.txt 파일의 "연결 프로그램" 목록과 기본 앱에 NoteBook 추가
korean.TaskNewNote=바탕 화면 및 폴더 오른쪽 클릭 메뉴에 "새 메모 (NoteBook)" 추가
korean.TextNote=텍스트 메모
korean.OpenWith=NoteBook으로 열기
korean.NewNote=새 메모 (NoteBook)
korean.AppDescription=암호화된 로컬 메모장
korean.RunApp=NoteBook 실행
korean.OpenSettings=설정을 열어 .txt 기본 앱 선택
korean.TaskStartup=Windows와 함께 NoteBook 시작(알림 영역에)

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:Shortcuts}"
Name: "txtopenwith"; Description: "{cm:TaskTxt}"; GroupDescription: "{cm:Integration}"
Name: "newnote"; Description: "{cm:TaskNewNote}"; GroupDescription: "{cm:Integration}"
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:Integration}"

[Files]
Source: "..\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.tr.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Start with Windows, straight into the notification area (per user, no administrator rights).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Notlar"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: startup
; ProgID and "Open with" for .txt — added next to Windows Notepad, which is not modified.
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "{cm:TextNote}"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "{cm:TextNote}"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueData: "{cm:OpenWith}"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""; Tasks: txtopenwith
; Default Programs registration so the app appears in Settings > Default apps.
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AppDescription}"; Tasks: txtopenwith
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".txt"; ValueData: "{#ProgId}"; Tasks: txtopenwith
; Right-click > "New note (NoteBook)" on the desktop and folder backgrounds. Creates no file; opens a note directly.
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\NoteBook.NewNote"; ValueType: string; ValueData: "{cm:NewNote}"; Flags: uninsdeletekey; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\NoteBook.NewNote"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\shell\NoteBook.NewNote\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --new"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\NoteBook.NewNote"; ValueType: string; ValueData: "{cm:NewNote}"; Flags: uninsdeletekey; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\NoteBook.NewNote"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: newnote
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\NoteBook.NewNote\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" --new"; Tasks: newnote

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunApp}"; Flags: nowait postinstall skipifsilent
; Silent in-app update (/UPDATE=1): relaunch the app when the install finishes.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsUpdate
; Windows only lets the user pick a file type's default; this link opens the Settings page.
Filename: "ms-settings:defaultapps"; Description: "{cm:OpenSettings}"; Flags: shellexec nowait postinstall skipifsilent unchecked; Tasks: txtopenwith

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// Close a running NoteBook before installing (CloseApplications only sees locked files; the launcher is closed too).
procedure CurStepChanged(CurStep: TSetupStep);
var R: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Gentle close first (the app saves its last change), then force whatever is left.
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Notlar.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(2000);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Notlar.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  end;
end;
