Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ' "Link zum Teilen erstellen" im Kontextmenue der Galerie, nur im Browser: eine Freigabe per
    ' Link fuer die Auswahl oder EINEN Ordner samt Unterordnern (BrowserBridgeShares.vb), mit den
    ' Vorgaben (30 Tage, ohne Kennwort, Herunterladen erlaubt, ohne Ort und Personen). Aendern und
    ' widerrufen geht in der mobilen Seite unter "Meine Freigaben" und in den Einstellungen unter
    ' "Freigaben". Am Rechner ohne Browser gibt es den Eintrag nicht: ein Link nuetzt nur, wenn die
    ' Seite von aussen erreichbar ist.
    Partial Public Class GalleryViewModel

        ''' <summary>Das Kommando fuer das Kontextmenue, Nothing ausserhalb des Browsers. Es meint die
        ''' Elemente, an denen das Menue stand (ContextItems), gelesen erst beim Klick.</summary>
        Public Function BrowserShareLinkCommand() As ICommand
            If Not BrowserBridge.IsActive Then Return Nothing
            Return New DelegateCommand(Sub()
                                           Dim ignored = CreateShareLinkAsync(ContextItems)
                                       End Sub)
        End Function

        ''' <summary>Legt die Freigabe an und zeigt den Link, mit "Link kopieren" fuer die
        ''' Zwischenablage des Browsers. Eine Ordnerkachel allein meint ihren Ordner, sonst gelten die
        ''' Bilder (wie beim Herunterladen ueber GetPathsForTransfer). Liefert den Link oder Nothing.</summary>
        Public Async Function CreateShareLinkAsync(items As IList(Of ImageItem)) As Task(Of String)
            If Not BrowserBridge.IsActive OrElse items Is Nothing OrElse items.Count = 0 Then Return Nothing
            Dim folder As String = Nothing
            Dim paths As List(Of String) = Nothing
            If items.Count = 1 AndAlso items(0) IsNot Nothing AndAlso items(0).IsFolder Then
                If Not items(0).IsFolderDownloadable Then Return Nothing
                folder = items(0).FilePath
            Else
                Dim chosen = items.Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder AndAlso i.IsDownloadable).ToList()
                paths = GetPathsForTransfer(chosen.Where(Function(i) Not i.IsVariant))
                ' Eine zusaetzliche Variante geht als eigenes Bild mit ihrem Rezept in die Freigabe,
                ' nicht als ihre Datei (wie in der mobilen Seite).
                paths.AddRange(chosen.Where(Function(i) i.IsVariant).Select(Function(i) i.ItemPath))
            End If
            Dim result = Await Task.Run(Function() BrowserBridge.CreateShareFromDesktop(paths, folder))
            Dim heading = LocalizationService.T("Link zum Teilen erstellen")
            If result.Url Is Nothing Then
                If _mainVm IsNot Nothing AndAlso Not String.IsNullOrEmpty(result.Problem) Then
                    Await _mainVm.ShowMessageAsync(heading, result.Problem)
                End If
                Return Nothing
            End If
            If _mainVm IsNot Nothing Then
                Dim message = LocalizationService.T("Der Link gilt 30 Tage, ohne Kennwort, mit Herunterladen, ohne Ort und Personen. Ändern und widerrufen lässt er sich in der mobilen Ansicht unter Meine Freigaben und in den Einstellungen unter Freigaben.") &
                              vbLf & vbLf & result.Url
                Dim copy = Await _mainVm.ShowConfirmAsync(heading, message, "Link kopieren", "Schließen")
                If copy Then BrowserBridge.CopyTextInBrowser(result.Url)
            End If
            Return result.Url
        End Function

    End Class

End Namespace
