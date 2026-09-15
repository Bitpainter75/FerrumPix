Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.ApplicationLifetimes
Imports Avalonia.Platform.Storage

Namespace Services

    ''' <summary>Der Dateiwaehler fuer Aktionen, die aus einem Ansichtsmodell heraus ausgeloest
    ''' werden.
    '''
    ''' Normalerweise oeffnet die Ansicht ihn selbst und reicht die Pfade weiter - sie kennt ihr
    ''' Fenster. Ein Eintrag im Kontextmenue kennt es nicht: dort steht ein Befehl, und der lebt im
    ''' Ansichtsmodell. Damit dafuer nicht jede Ansicht ihren eigenen Draht bekommt, sucht diese
    ''' Stelle das Fenster selbst - das VORDERSTE, sonst das Hauptfenster.</summary>
    Public NotInheritable Class FilePickerService

        Private Sub New()
        End Sub

        ''' <summary>Laesst EINE vorhandene Datei waehlen. Leer heisst abgebrochen - oder es gab
        ''' kein Fenster, an das sich der Waehler haengen konnte.</summary>
        ''' <param name="typeLabel">Wie der Filter im Waehler heisst, etwa "GPS-Aufzeichnung".</param>
        ''' <param name="patterns">Muster wie "*.gpx".</param>
        Public Shared Async Function PickOpenFileAsync(title As String, typeLabel As String,
                                                       patterns As IEnumerable(Of String)) As Task(Of String)
            Try
                Dim storageProvider = TryCast(FindTopLevel(), TopLevel)?.StorageProvider
                If storageProvider Is Nothing Then Return ""

                Dim fileType As New FilePickerFileType(typeLabel) With {
                    .Patterns = patterns.ToList()}
                Dim files = Await storageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {fileType}})
                If files Is Nothing OrElse files.Count = 0 Then Return ""
                Return If(files(0).Path?.LocalPath, "")
            Catch ex As Exception
                DiagnosticLogService.LogException("FilePicker.PickOpenFile", ex)
                Return ""
            End Try
        End Function

        ''' <summary>Das Fenster, an dem der Waehler haengen soll. Das aktive zuerst: der Betrachter
        ''' und der Editor koennen in einem eigenen Fenster stehen, und ein Waehler, der dann hinter
        ''' dem Hauptfenster aufgeht, ist verloren.</summary>
        Private Shared Function FindTopLevel() As Object
            Dim lifetime = TryCast(Application.Current?.ApplicationLifetime,
                                   IClassicDesktopStyleApplicationLifetime)
            If lifetime Is Nothing Then Return Nothing
            Dim active = lifetime.Windows.FirstOrDefault(Function(w) w.IsActive)
            Return If(active, lifetime.MainWindow)
        End Function

    End Class

End Namespace
