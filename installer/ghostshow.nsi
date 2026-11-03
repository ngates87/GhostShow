; The Windows installer, written for NSIS (https://nsis.sourceforge.io). installer/build.sh builds it, and passes:
;   VERSION     three numbers, like 1.2.0
;   APP_DIR     the published app, with the browser app in its browser-app folder
;   VC_REDIST   Microsoft's Visual C++ runtime installer
;   SETUP_FILE  the installer to write
;
; What ends up on the computer:
;   <install folder>\bin\            the program, replaced whole on every upgrade
;   <install folder>\uninstall.exe
;   %APPDATA%\GhostShow\             settings and fixture profiles, written by the app. Never touched here.

Unicode true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\GhostShow"
!define SETTINGS_KEY "Software\GhostShow"
!define APP_EXE "bin\ghostshow-app.exe"

Name "GhostShow"
OutFile "${SETUP_FILE}"
InstallDir "$PROGRAMFILES64\GhostShow"
; An upgrade goes where the last version went.
InstallDirRegKey HKLM "${SETTINGS_KEY}" "InstallDir"
RequestExecutionLevel admin

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "GhostShow"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "GhostShow installer"
VIAddVersionKey "LegalCopyright" "The GhostShow authors"

!define MUI_ICON "../apps/GhostShow.App/Assets/ghostshow.ico"
!define MUI_UNICON "../apps/GhostShow.App/Assets/ghostshow.ico"
!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Start GhostShow"
!define MUI_FINISHPAGE_RUN_FUNCTION StartAppAsTheUser
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; A running program's file can't be opened for writing, which tells us whether GhostShow is running. It might be running
; a show, so ask the person to close it rather than closing it for them.
!macro WaitForAppToClose un
Function ${un}WaitForAppToClose
  retry:
    IfFileExists "$INSTDIR\${APP_EXE}" 0 closed
    ClearErrors
    FileOpen $0 "$INSTDIR\${APP_EXE}" a
    IfErrors running
    FileClose $0
    Goto closed
  running:
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "GhostShow is running. Close it, then choose Retry." /SD IDCANCEL IDRETRY retry
    Abort
  closed:
FunctionEnd
!macroend
!insertmacro WaitForAppToClose ""
!insertmacro WaitForAppToClose "un."

; This installer runs as administrator, and a program it starts directly would too: it would use the administrator's
; settings if that's a different account. Explorer starts it as the person who is logged in.
Function StartAppAsTheUser
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APP_EXE}"'
FunctionEnd

; The sound library (SoundFlow's miniaudio.dll) needs the Visual C++ runtime, which a fresh Windows may not have. The
; rest of the app runs without it, so a failure here is a warning and not the end of the install.
Function InstallVisualCppRuntimeIfMissing
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" "Installed"
  StrCmp $0 "1" done
  DetailPrint "Installing the Visual C++ runtime..."
  InitPluginsDir
  File "/oname=$PLUGINSDIR\VC_redist.x64.exe" "${VC_REDIST}"
  ExecWait '"$PLUGINSDIR\VC_redist.x64.exe" /install /quiet /norestart' $0
  ; 3010: installed, restart needed. 1638: a newer version is already there.
  StrCmp $0 "0" done
  StrCmp $0 "3010" done
  StrCmp $0 "1638" done
  MessageBox MB_OK|MB_ICONEXCLAMATION "The Visual C++ runtime couldn't be installed (error $0), so GhostShow may not be able to play sound." /SD IDOK
  done:
FunctionEnd

Function .onInit
  SetRegView 64
  SetShellVarContext all
  ${IfNot} ${RunningX64}
  ${OrIfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "GhostShow needs 64-bit Windows 10 or newer." /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

Section "GhostShow" SectionApp
  SectionIn RO
  Call WaitForAppToClose
  Call InstallVisualCppRuntimeIfMissing

  RMDir /r "$INSTDIR\bin"
  SetOutPath "$INSTDIR\bin"
  File /r "${APP_DIR}/*"

  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortCut "$SMPROGRAMS\GhostShow.lnk" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKLM "${SETTINGS_KEY}" "InstallDir" "$INSTDIR"
  ; For Windows' list of installed apps.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" $0
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "GhostShow"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "GhostShow"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Desktop shortcut" SectionDesktopShortcut
  CreateShortCut "$DESKTOP\GhostShow.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

; Only removes what the installer put there, and the install folder only if that leaves it empty: the person may have
; chosen a folder with other things in it. Settings in %APPDATA% and shows stay. The Visual C++ runtime stays too,
; because other programs may be using it.
Section "Uninstall"
  Call un.WaitForAppToClose
  Delete "$SMPROGRAMS\GhostShow.lnk"
  Delete "$DESKTOP\GhostShow.lnk"
  RMDir /r "$INSTDIR\bin"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  DeleteRegKey HKLM "${SETTINGS_KEY}"
SectionEnd
