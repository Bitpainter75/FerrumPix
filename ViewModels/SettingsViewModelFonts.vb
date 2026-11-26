Imports System
Imports System.Globalization
Imports System.IO
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Zeile "Eigene Schriften" auf der Seite "Katalog", unter dem Datenordner: wo
    ''' der Ordner Fonts liegt, wie viele Familien darin gefunden wurden, dazu "Ordner öffnen" und
    ''' "Neu einlesen" (<see cref="FontLibraryService"/>). Der Ordner ist nicht waehlbar, er liegt
    ''' immer im Datenordner und zieht mit ihm um.
    '''
    ''' Im Browser gibt es keinen Dateimanager beim Nutzer: statt des Knopfes steht dort ein
    ''' Hinweis, dass die Schriften auf dem Server in diesen Ordner gehoeren.</summary>
    Partial Public Class SettingsViewModel

        Private _fontLibraryHooked As Boolean
        Private _fontLibraryReloading As Boolean

        ''' <summary>Wo die eigenen Schriften gelesen werden.</summary>
        Public ReadOnly Property FontsFolderText As String
            Get
                HookFontLibrary()
                Return FontLibraryService.Folder
            End Get
        End Property

        ''' <summary>Wie viele Familien gefunden wurden, oder dass gerade gelesen wird.</summary>
        Public ReadOnly Property FontLibraryCountText As String
            Get
                HookFontLibrary()
                Dim count = FontLibraryService.LoadedFamilyCount
                If _fontLibraryReloading OrElse count < 0 Then Return LocalizationService.T("Schriften werden eingelesen ...")
                If count = 0 Then Return LocalizationService.T("Noch keine eigenen Schriften gefunden.")
                Return String.Format(CultureInfo.CurrentCulture, LocalizationService.T("Gefundene Schriftfamilien: {0}"), count)
            End Get
        End Property

        ''' <summary>Ob der Knopf "Ordner öffnen" Sinn hat: nicht im Browser.</summary>
        Public ReadOnly Property CanOpenFontsFolder As Boolean
            Get
                Return Not BrowserBridge.IsActive
            End Get
        End Property

        ''' <summary>Der Hinweis statt des Knopfes im Browser.</summary>
        Public ReadOnly Property ShowFontsFolderServerHint As Boolean
            Get
                Return BrowserBridge.IsActive
            End Get
        End Property

        Public ReadOnly Property OpenFontsFolderCommand As ICommand =
            ReactiveCommand.Create(Sub() OpenFontsFolder())

        Public ReadOnly Property ReloadFontLibraryCommand As ICommand =
            ReactiveCommand.CreateFromTask(Function() ReloadFontLibraryAsync())

        ''' <summary>Oeffnet den Ordner im Dateimanager und legt ihn dafuer an: angelegt wird er
        ''' sonst erst, wenn jemand etwas hineinlegt.</summary>
        Private Shared Sub OpenFontsFolder()
            Dim folder = FontLibraryService.Folder
            Try
                Directory.CreateDirectory(folder)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.FontsFolder", ex)
            End Try
            ShellOpenService.Open(folder, "Settings.FontsFolder")
        End Sub

        Private Async Function ReloadFontLibraryAsync() As Threading.Tasks.Task
            _fontLibraryReloading = True
            Me.RaisePropertyChanged(NameOf(FontLibraryCountText))
            Try
                Await FontLibraryService.ReloadAsync()
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.ReloadFonts", ex)
            Finally
                _fontLibraryReloading = False
                Me.RaisePropertyChanged(NameOf(FontLibraryCountText))
            End Try
        End Function

        ''' <summary>Meldet sich einmal fuer das Ende des Einlesens an, damit die Zahl nach dem Start
        ''' im Hintergrund nachkommt. Schwach gehalten, wie die Editoren.</summary>
        Private Sub HookFontLibrary()
            If _fontLibraryHooked Then Return
            _fontLibraryHooked = True
            Dim weakSelf As New WeakReference(Of SettingsViewModel)(Me)
            Dim handler As EventHandler = Nothing
            handler = Sub(sender, e)
                          Dim alive As SettingsViewModel = Nothing
                          If Not weakSelf.TryGetTarget(alive) Then
                              RemoveHandler FontLibraryService.Changed, handler
                              Return
                          End If
                          Try
                              Avalonia.Threading.Dispatcher.UIThread.Post(Sub()
                                                                              alive.RaisePropertyChanged(NameOf(FontLibraryCountText))
                                                                              alive.RaisePropertyChanged(NameOf(FontsFolderText))
                                                                          End Sub)
                          Catch ex As Exception
                              DiagnosticLogService.LogException("Settings.FontLibraryChanged", ex)
                          End Try
                      End Sub
            AddHandler FontLibraryService.Changed, handler
        End Sub

    End Class

End Namespace
