Option Explicit

Dim shell, files, appFolder, runtimeFolder, trackerPath
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
appFolder = files.GetParentFolderName(WScript.ScriptFullName)
runtimeFolder = appFolder & "\.dotnet"
trackerPath = appFolder & "\D2AbilityTracker.exe"

If files.FileExists(runtimeFolder & "\dotnet.exe") Then
    shell.Environment("Process")("DOTNET_ROOT") = runtimeFolder
    shell.Environment("Process")("DOTNET_ROOT_X64") = runtimeFolder
ElseIf Not HasSystemDesktopRuntime() Then
    If MsgBox("The .NET 9 Desktop Runtime is missing. Open Microsoft's download page?", _
        vbQuestion Or vbYesNo, "Destiny 2 Ability Recharge Tracker") = vbYes Then
        shell.Run "https://dotnet.microsoft.com/en-us/download/dotnet/9.0", 1, False
    End If
    WScript.Quit 1
End If

shell.CurrentDirectory = appFolder
shell.Run """" & trackerPath & """", 1, False

Function HasSystemDesktopRuntime()
    Dim registry, version, path
    Set registry = GetObject("winmgmts:\\.\root\default:StdRegProv")
    path = "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
    HasSystemDesktopRuntime = HasRuntimeKey(registry, &H80000002, path) Or _
        HasRuntimeKey(registry, &H80000001, path)
End Function

Function HasRuntimeKey(registry, rootKey, path)
    Dim versions, version
    HasRuntimeKey = False
    If registry.EnumValues(rootKey, path, versions) <> 0 Then Exit Function
    If IsNull(versions) Then Exit Function
    For Each version In versions
        If Left(version, 4) = "9.0." And InStr(version, "-") = 0 Then
            HasRuntimeKey = True
            Exit Function
        End If
    Next
End Function
