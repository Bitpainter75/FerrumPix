Imports System
Imports System.Collections.Generic
Imports System.IO
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>
    ''' Wohin eine Datei geht, die im Browser auf die Anwendung abgelegt wird. Die Bruecke fragt
    ''' hier, weil nur das Fenster weiss, was an der Stelle des Ablegens zu sehen ist.
    '''
    ''' <para>DIE REGEL: auf der Leinwand des Editors mit einem offenen Bild wird sie eine Ebene an
    ''' dieser Stelle, wie beim Ablegen aus dem Dateimanager. Sonst kommt sie in den Ordner, den die
    ''' Galerie zeigt, im Betrachter und Editor in den Ordner des gezeigten Bildes.</para>
    '''
    ''' <para>OHNE ORDNER WIRD ABGEWIESEN, statt einen Ersatz zu waehlen. Zeigt die Galerie eine
    ''' Suchliste oder einen Server, oder ist im Betrachter nichts offen, gibt es keinen Ordner, den
    ''' der Nutzer gerade vor sich hat; eine Datei in einem Ordner, den er nicht gewaehlt hat (etwa
    ''' Bilder), waere eine Ueberraschung im Bestand. Die Seite zeigt den Grund.</para>
    ''' </summary>
    Partial Public Class MainWindow

        ''' <summary>Abstand zweier gleichzeitig abgelegter Ebenen in Prozent, wie beim Ablegen aus
        ''' dem Dateimanager (EditorView.InsertImagesAt).</summary>
        Private Const BrowserLayerCascadePercent As Double = 3.0

        Private ReadOnly _browserRefreshFolders As New HashSet(Of String)(PathIdentity.Comparer)
        Private _browserRefreshTimer As DispatcherTimer

        ''' <summary>Meldet die Haken bei der Bruecke an, nur im Browser.</summary>
        Private Sub InstallBrowserFileHooks()
            If Not BrowserBridge.IsActive Then Return
            BrowserBridge.UploadTargetResolver = AddressOf ResolveBrowserUpload
            BrowserBridge.UploadStored = AddressOf OnBrowserUploadStored
        End Sub

        Private Function ResolveBrowserUpload(target As Window, point As Point?) As BrowserUploadTarget
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            ' Gesperrt kommt nichts hinein, auch nicht ueber eine noch offene Seite.
            If vm Is Nothing OrElse vm.IsAppLocked Then Return Nothing
            Select Case vm.CurrentMode
                Case AppMode.Editor
                    Dim editor = vm.Editor
                    Dim view As Control = Nothing
                    Dim x, y As Double
                    If point.HasValue AndAlso target Is Me AndAlso editor IsNot Nothing AndAlso
                       _modeViews.TryGetValue(editor, view) AndAlso
                       TryCast(view, EditorView)?.TryGetLayerDropPercent(Me, point.Value, x, y) = True Then
                        ' Das Bild, auf das abgelegt wurde: wechselt es waehrend des Hochladens,
                        ' kommt die Ebene nicht in das dann offene.
                        Dim document = editor.CurrentImagePath
                        Return BrowserUploadTarget.AsLayer(
                            Function(path, index) editor.InsertUploadedFileAsLayerAsync(path, x + index * BrowserLayerCascadePercent,
                                                                                        y + index * BrowserLayerCascadePercent, document))
                    End If
                    Return FolderOfShownImage(editor?.CurrentImagePath)
                Case AppMode.Viewer
                    Return FolderOfShownImage(vm.Viewer?.CurrentImagePath)
                Case AppMode.Gallery
                    Dim gallery = vm.Gallery
                    If gallery Is Nothing Then Return Nothing
                    If gallery.IsVirtualFolder Then
                        If gallery.IsImmichTab OrElse gallery.IsNextcloudTab Then Return ServerRefusal()
                        Return Nothing
                    End If
                    If String.IsNullOrEmpty(gallery.CurrentFolder) Then Return Nothing
                    Return BrowserUploadTarget.IntoFolder(gallery.CurrentFolder)
            End Select
            Return Nothing
        End Function

        ''' <summary>Der Ordner des gezeigten Bildes. Ein Serverbild liegt nur als Kopie im
        ''' Temp-Ordner; dorthin gehoert nichts.</summary>
        Private Shared Function FolderOfShownImage(imagePath As String) As BrowserUploadTarget
            If String.IsNullOrEmpty(imagePath) Then Return Nothing
            Dim imageFile = VariantKey.FileOf(imagePath)
            If Not File.Exists(imageFile) Then Return Nothing
            Dim temp = Path.GetFullPath(Path.GetTempPath())
            If Path.GetFullPath(imageFile).StartsWith(temp, PathIdentity.Comparison) Then Return ServerRefusal()
            Return BrowserUploadTarget.IntoFolder(Path.GetDirectoryName(imageFile))
        End Function

        Private Shared Function ServerRefusal() As BrowserUploadTarget
            Return BrowserUploadTarget.Refused(
                LocalizationService.T("Die Bilder hier liegen auf einem Server. Hochladen geht nur in einen Ordner auf diesem Rechner."))
        End Function

        ''' <summary>Eine Datei ist angekommen. Die Galerie liest ihren Ordner neu, gesammelt: wer
        ''' zwanzig Dateien ablegt, soll nicht zwanzigmal neu laden.</summary>
        Private Sub OnBrowserUploadStored(storedPath As String)
            Dim folder = Path.GetDirectoryName(storedPath)
            If String.IsNullOrEmpty(folder) Then Return
            _browserRefreshFolders.Add(folder)
            If _browserRefreshTimer Is Nothing Then
                _browserRefreshTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(400)}
                AddHandler _browserRefreshTimer.Tick, AddressOf OnBrowserRefreshTick
            End If
            _browserRefreshTimer.Stop()
            _browserRefreshTimer.Start()
        End Sub

        Private Sub OnBrowserRefreshTick(sender As Object, e As EventArgs)
            _browserRefreshTimer.Stop()
            Dim gallery = TryCast(DataContext, MainWindowViewModel)?.Gallery
            For Each folder In _browserRefreshFolders
                gallery?.RefreshFolder(folder)
            Next
            _browserRefreshFolders.Clear()
        End Sub

    End Class

End Namespace
