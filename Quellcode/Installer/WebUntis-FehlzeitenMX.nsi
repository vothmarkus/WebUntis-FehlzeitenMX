Unicode true
Target amd64-unicode
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WordFunc.nsh"
!include "WinVer.nsh"

; Keep the existing installation identity and executable path for upgrades.
!define LEGACY_NAME "WebUntis-Auswertung"
!define APP_EXE "${LEGACY_NAME}.exe"
!define INSTALL_PATH "$LOCALAPPDATA\Programs\${LEGACY_NAME}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${LEGACY_NAME}"
!define EDGE_KEY "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define APP_MUTEX "Local\WebUntisAuswertung-Installation"
!define BRAND_ICON "${__FILEDIR__}/../Windows/Assets/BKT.ico"

Name "${APP} ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "${INSTALL_PATH}"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetOverwrite on
ShowInstDetails hide
ShowUninstDetails hide
BrandingText "${APP}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1031 "ProductName" "${APP}"
VIAddVersionKey /LANG=1031 "FileDescription" "${APP} Setup"
VIAddVersionKey /LANG=1031 "CompanyName" "Markus Voth"
VIAddVersionKey /LANG=1031 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1031 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1031 "LegalCopyright" "Markus Voth"

Var InstallMutex
Var BackupDir
Var HadProgram
Var PreviousName
Var RuntimeVersion

!define MUI_ICON "${BRAND_ICON}"
!define MUI_UNICON "${BRAND_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Dieses Setup installiert ${APP} für dein Windows-Konto. Eine vorhandene Installation wird aktualisiert.$\r$\n$\r$\nBeim Installieren wird eine geöffnete App regulär geschlossen."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Programm\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "${APP} starten"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "German"

!macro AcquireInstallMutex CLOSE_APP
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${APP_MUTEX}")p.r0'
    StrCpy $InstallMutex $0
    ${If} $InstallMutex == 0
        MessageBox MB_ICONSTOP|MB_OK "Das Setup konnte den Installationsstatus nicht prüfen." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    waitForInstallation:
    System::Call 'kernel32::WaitForSingleObject(p $InstallMutex, i 0)i.r0'
    !if "${CLOSE_APP}" == "yes"
        ${If} $0 == 258
            Call RequestAppClose
            ${If} $0 != 0
                DetailPrint "Warte darauf, dass sich ${APP} schließt …"
                System::Call 'kernel32::WaitForSingleObject(p $InstallMutex, i 15000)i.r0'
            ${EndIf}
        ${EndIf}
    !endif
    ${If} $0 != 0
    ${AndIf} $0 != 128
        MessageBox MB_ICONEXCLAMATION|MB_RETRYCANCEL "Bitte schließe ${APP} und jedes andere Setup-Fenster. Wähle dann Wiederholen." /SD IDCANCEL IDRETRY waitForInstallation
        System::Call 'kernel32::CloseHandle(p $InstallMutex)'
        StrCpy $InstallMutex 0
        SetErrorLevel 1
        Abort
    ${EndIf}
!macroend

!macro ReleaseInstallMutex
    ${If} $InstallMutex != 0
        System::Call 'kernel32::ReleaseMutex(p $InstallMutex)'
        System::Call 'kernel32::CloseHandle(p $InstallMutex)'
        StrCpy $InstallMutex 0
    ${EndIf}
!macroend

Function .onInit
    ${IfNot} ${AtLeastWin10}
    ${OrIfNot} ${AtLeastBuild} 17763
        MessageBox MB_ICONSTOP|MB_OK "Dieses Setup benötigt Windows 10 ab Version 1809 oder Windows 11." /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    SetShellVarContext current
    SetRegView 64
    StrCpy $INSTDIR "${INSTALL_PATH}"
FunctionEnd

Function CheckInstalledVersion
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "DisplayVersion"
    ${If} $0 != ""
        ${VersionCompare} $0 "${VERSION}" $1
        ${If} $1 == 1
            !insertmacro ReleaseInstallMutex
            MessageBox MB_ICONINFORMATION|MB_OK "Es ist bereits eine neuere Version installiert ($0)." /SD IDOK
            SetErrorLevel 1
            Abort
        ${EndIf}
    ${EndIf}
FunctionEnd

Function RequestAppClose
    ; Only close the main window belonging to this installed executable.
    StrCpy $0 0
    nextWindow:
    System::Call 'user32::FindWindowExW(p 0, p r0, p 0, p 0)p.r0'
    ${If} $0 == 0
        Return
    ${EndIf}
    System::Call 'user32::IsWindowVisible(p r0)i.r1'
    ${If} $1 == 0
        Goto nextWindow
    ${EndIf}
    System::Call 'user32::GetWindowTextW(p r0, w .r1, i ${NSIS_MAX_STRLEN})i'
    StrLen $2 "${APP} "
    StrCpy $2 $1 $2
    StrLen $3 "${LEGACY_NAME} "
    StrCpy $3 $1 $3
    ${If} $2 != "${APP} "
    ${AndIf} $3 != "${LEGACY_NAME} "
        Goto nextWindow
    ${EndIf}
    System::Call 'user32::GetWindowThreadProcessId(p r0, *i .r1)i.r2'
    ${If} $2 == 0
        Goto nextWindow
    ${EndIf}
    System::Call 'kernel32::OpenProcess(i 0x1000, i 0, i r1)p.r2'
    ${If} $2 == 0
        Goto nextWindow
    ${EndIf}
    System::Call 'kernel32::QueryFullProcessImageNameW(p r2, i 0, w .r3, *i ${NSIS_MAX_STRLEN})i.r4'
    System::Call 'kernel32::CloseHandle(p r2)'
    ${If} $4 != 0
    ${AndIf} $3 == "$INSTDIR\Programm\${APP_EXE}"
        System::Call 'user32::PostMessageW(p r0, i 0x0010, p 0, p 0)i.r0'
        Return
    ${EndIf}
    Goto nextWindow
FunctionEnd

Function LaunchApp
    SetOutPath "$INSTDIR\Programm"
    Exec '"$INSTDIR\Programm\${APP_EXE}"'
FunctionEnd

Function FindWebView2
    SetRegView 32
    ReadRegStr $RuntimeVersion HKLM "${EDGE_KEY}" "pv"
    ${If} $RuntimeVersion == ""
    ${OrIf} $RuntimeVersion == "0.0.0.0"
        ReadRegStr $RuntimeVersion HKCU "${EDGE_KEY}" "pv"
    ${EndIf}
    SetRegView 64
    ${If} $RuntimeVersion == ""
    ${OrIf} $RuntimeVersion == "0.0.0.0"
        ReadRegStr $RuntimeVersion HKCU "${EDGE_KEY}" "pv"
    ${EndIf}
FunctionEnd

Function InstallWebView2
    Call FindWebView2
    ${If} $RuntimeVersion != ""
    ${AndIf} $RuntimeVersion != "0.0.0.0"
        Return
    ${EndIf}

    DetailPrint "Microsoft Edge WebView2 wird eingerichtet …"
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    ClearErrors
    File /oname=MicrosoftEdgeWebview2Setup.exe "${WEBVIEW_BOOTSTRAPPER}"
    ${IfNot} ${Errors}
        ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $0
        Call FindWebView2
        ${If} $RuntimeVersion != ""
        ${AndIf} $RuntimeVersion != "0.0.0.0"
            Return
        ${EndIf}
    ${EndIf}
    !insertmacro ReleaseInstallMutex
    MessageBox MB_ICONSTOP|MB_OK "Microsoft Edge WebView2 konnte nicht eingerichtet werden. Prüfe die Internetverbindung oder installiere WebView2 über deine Schul-IT." /SD IDOK
    SetErrorLevel 1
    Abort
FunctionEnd

Section "Programm"
    StrCpy $InstallMutex 0
    Call CheckInstalledVersion
    !insertmacro AcquireInstallMutex yes
    Call CheckInstalledVersion
    Call InstallWebView2
    StrCpy $HadProgram 0
    StrCpy $BackupDir "$INSTDIR\.vorherige-version"
    ClearErrors
    CreateDirectory "$INSTDIR"
    IfErrors StageFailed

    ${If} ${FileExists} "$BackupDir\${APP_EXE}"
    ${AndIfNot} ${FileExists} "$INSTDIR\Programm\${APP_EXE}"
        Rename "$BackupDir" "$INSTDIR\Programm"
        IfErrors StageFailed
    ${EndIf}
    RMDir /r "$INSTDIR\.neue-version"
    RMDir /r "$BackupDir"
    ClearErrors
    SetOutPath "$INSTDIR\.neue-version"
    File /r "${PAYLOAD}/*.*"
    IfErrors StageFailed
    WriteUninstaller "$INSTDIR\.neue-version\Deinstallieren.exe"
    IfErrors StageFailed

    ${If} ${FileExists} "$INSTDIR\Programm\${APP_EXE}"
        ClearErrors
        FileOpen $0 "$INSTDIR\Programm\${APP_EXE}" a
        IfErrors ProgramInUse
        FileClose $0
        ClearErrors
        Rename "$INSTDIR\Programm" "$BackupDir"
        IfErrors ProgramInUse
        StrCpy $HadProgram 1
    ${EndIf}

    SetOutPath "$INSTDIR"
    ClearErrors
    Rename "$INSTDIR\.neue-version" "$INSTDIR\Programm"
    IfErrors SwitchFailed

    ReadRegStr $PreviousName HKCU "${UNINSTALL_KEY}" "DisplayName"
    ClearErrors
    SetOutPath "$INSTDIR\Programm"
    CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\Programm\${APP_EXE}"
    IfErrors RegistrationFailed
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Markus Voth"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Programm\${APP_EXE}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Programm\Deinstallieren.exe$\"'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    IfErrors RegistrationFailed
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
    IfErrors RegistrationFailed
    Delete "$SMPROGRAMS\${LEGACY_NAME}.lnk"
    RMDir /r "$BackupDir"
    !insertmacro ReleaseInstallMutex
    Goto Done

    ProgramInUse:
        SetOutPath "$INSTDIR"
        RMDir /r "$INSTDIR\.neue-version"
        !insertmacro ReleaseInstallMutex
        MessageBox MB_ICONEXCLAMATION|MB_OK "Die bisherige Version ist geöffnet. Schließe sie und starte das Setup erneut." /SD IDOK
        SetErrorLevel 1
        Abort

    RegistrationFailed:
        SetOutPath "$INSTDIR"
        RMDir /r "$INSTDIR\Programm"
        ${If} $PreviousName != "${APP}"
            Delete "$SMPROGRAMS\${APP}.lnk"
        ${EndIf}
        ${If} $HadProgram == 0
            DeleteRegKey HKCU "${UNINSTALL_KEY}"
        ${ElseIf} $PreviousName != ""
            WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "$PreviousName"
        ${EndIf}
    SwitchFailed:
        ${If} $HadProgram == 1
            Rename "$BackupDir" "$INSTDIR\Programm"
        ${EndIf}
    StageFailed:
        SetOutPath "$LOCALAPPDATA"
        RMDir /r "$INSTDIR\.neue-version"
        !insertmacro ReleaseInstallMutex
        MessageBox MB_ICONSTOP|MB_OK "Die Programmdateien konnten nicht installiert werden." /SD IDOK
        SetErrorLevel 1
        Abort
    Done:
SectionEnd

Function un.onInit
    SetShellVarContext current
    SetRegView 64
    StrCpy $INSTDIR "${INSTALL_PATH}"
FunctionEnd

Section "Uninstall"
    !insertmacro AcquireInstallMutex no
    SetOutPath "$LOCALAPPDATA"
    ClearErrors
    RMDir /r "$INSTDIR\Programm"
    IfErrors UninstallFailed
    RMDir /r "$INSTDIR\.neue-version"
    RMDir /r "$INSTDIR\.vorherige-version"
    Delete "$SMPROGRAMS\${APP}.lnk"
    Delete "$SMPROGRAMS\${LEGACY_NAME}.lnk"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    RMDir "$INSTDIR"
    !insertmacro ReleaseInstallMutex
    Goto UninstallDone

    UninstallFailed:
        !insertmacro ReleaseInstallMutex
        MessageBox MB_ICONSTOP|MB_OK "Die Programmdateien konnten nicht vollständig entfernt werden." /SD IDOK
        SetErrorLevel 1
        Abort
    UninstallDone:
SectionEnd
