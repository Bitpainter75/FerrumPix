Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Reflection
Imports System.Windows.Input
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Das Symbol vor der Ueberschrift eines Dialogs.
    '''
    ''' Ein Dialog gehoert fast immer zu einer Aktion, die im Menue schon ein Symbol traegt. Damit
    ''' beide dasselbe zeigen, kommt es zuerst aus dem Menuekatalog: traegt dort ein Eintrag den
    ''' Text der Ueberschrift, gilt sein Symbol. Sonst entscheidet die Art des Dialogs, und eine
    ''' blosse Meldung bekommt das Fragezeichen (mit Abbrechen) oder das Info-Zeichen (ohne).
    '''
    ''' Der Katalog wird dafuer durchgegangen: jede seiner Funktionen, die nur ein Kommando
    ''' nimmt, liefert Text und Symbol. So zieht ein neuer Menueeintrag sein Symbol von selbst in
    ''' den Dialog nach, ohne zweite Liste.
    ''' </summary>
    Friend NotInheritable Class DialogIcons

        Private Sub New()
        End Sub

        Private Const IconFolder As String = "avares://FerrumPix/Assets/Icons/phosphor/regular/"

        ''' <summary>Das Symbol als Quelle fuer SvgIcon; leer, wenn es keines gibt.</summary>
        ''' <param name="titleText">Die Ueberschrift, uebersetzt oder als deutscher Schluessel.</param>
        ''' <param name="hasCancel">Ob der Dialog einen Abbrechen-Knopf zeigt, also fragt.</param>
        Public Shared Function SourceFor(kind As AppDialogKind, titleText As String, hasCancel As Boolean) As String
            Dim source = FromMenu(titleText)
            If source.Length > 0 Then Return source
            Dim name = FromKind(kind, hasCancel)
            Return If(name.Length = 0, "", IconFolder & name & ".svg")
        End Function

        ''' <summary>Schneidet ab, was ein Menuetext gegenueber einer Ueberschrift mehr traegt:
        ''' die drei Punkte am Ende.</summary>
        Private Shared Function Bare(text As String) As String
            Return If(text, "").Trim().TrimEnd("…"c, "."c).Trim()
        End Function

        Private Shared Function FromMenu(titleText As String) As String
            Dim title = Bare(LocalizationService.T(If(titleText, "")))
            If title.Length = 0 Then Return ""
            Try
                For Each entry In ReadMenuEntries()
                    If String.Equals(Bare(entry.Label), title, StringComparison.Ordinal) Then Return entry.IconSource
                Next
            Catch
                ' Ohne Katalog bleibt es bei der Art des Dialogs.
            End Try
            Return ""
        End Function

        Private Shared Function FromKind(kind As AppDialogKind, hasCancel As Boolean) As String
            Select Case kind
                Case AppDialogKind.Rename, AppDialogKind.BatchRename, AppDialogKind.Input : Return "pencil-simple"
                Case AppDialogKind.SaveAs : Return "floppy-disk"
                Case AppDialogKind.FileConflict : Return "warning"
                Case AppDialogKind.Search : Return "magnifying-glass"
                Case AppDialogKind.BatchConvert : Return "swap"
                Case AppDialogKind.BatchResize : Return "resize"
                Case AppDialogKind.BatchFilter : Return "palette"
                Case AppDialogKind.WatermarkPreset : Return "stamp"
                Case AppDialogKind.ExportTo : Return "export"
                Case AppDialogKind.SetPlace : Return "map-pin"
                Case AppDialogKind.GpxTrack : Return "path"
                Case AppDialogKind.CaptureDate : Return "calendar"
                Case AppDialogKind.ImageDescription : Return "note-pencil"
                Case AppDialogKind.RemoveMetadata : Return "eraser"
                Case AppDialogKind.CatalogTransfer : Return "arrows-left-right"
                Case AppDialogKind.CompareMetadata : Return "columns"
                Case AppDialogKind.ModelDownload : Return "download-simple"
                Case AppDialogKind.Import : Return "tray-arrow-down"
                Case AppDialogKind.Backup : Return "archive"
                Case AppDialogKind.Restore : Return "clock-counter-clockwise"
                Case AppDialogKind.Shortcuts : Return "keyboard"
                ' Das Puzzleteil wie beim Werkzeug und im Bereich der Einstellungen.
                Case AppDialogKind.ExtensionDownload : Return "puzzle-piece"
                Case AppDialogKind.DiagnosticLog : Return "file-text"
                Case Else : Return If(hasCancel, "question", "info")
            End Select
        End Function

        ''' <summary>Geht den Menuekatalog durch, bei jedem Dialog neu: die Eintraege tragen ihren
        ''' Text in der Sprache von jetzt, und ein Dialog oeffnet sich selten genug. Die Funktionen
        ''' bekommen kein Kommando; sie bauen damit nur den Eintrag, an dem Text und Symbol haengen.</summary>
        Private Shared Function ReadMenuEntries() As List(Of AppAction)
            Dim result As New List(Of AppAction)()
            For Each method In GetType(FooterMenuCatalog).GetMethods(BindingFlags.Public Or BindingFlags.Static)
                If method.ReturnType IsNot GetType(AppAction) Then Continue For
                Dim parameters = method.GetParameters()
                If parameters.Length <> 1 OrElse parameters(0).ParameterType IsNot GetType(ICommand) Then Continue For
                Try
                    Dim entry = TryCast(method.Invoke(Nothing, New Object() {Nothing}), AppAction)
                    If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Label) OrElse String.IsNullOrEmpty(entry.IconSource) Then Continue For
                    result.Add(entry)
                Catch
                    ' Ein Eintrag, der sich ohne Kommando nicht bauen laesst, traegt nichts bei.
                End Try
            Next
            Return result
        End Function

    End Class

End Namespace
