Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>Ordnervorlagen fuer Speichern unter und die Stapelfunktionen (Exportieren,
    ''' Konvertieren, Groesse aendern, Anpassungen und Wasserzeichen anwenden): ein Zielordner, der
    ''' Platzhalter tragen darf und erst je Bild aufgeloest wird. Aufgeloest wird in
    ''' MainWindowViewModel.ExpandTargetFolderTemplate; hier steht, was ohne Bild zu sagen ist.
    '''
    ''' PLATZHALTER: {folder} der Ordner des Bildes, {foldername} sein Name, {pictures} der
    ''' Bilder-Ordner, {home} der persoenliche Ordner; dazu alle des Dateinamens (Aufnahmedatum,
    ''' Kamera und die uebrigen, siehe ExpandTargetNamePattern). {folder}, {pictures} und {home}
    ''' stehen jeweils als ganzes Stueck zwischen zwei Schraegstrichen, meist am Anfang:
    ''' "{folder}/converted" legt neben jedem Bild einen Unterordner an (GitHub #102).</summary>
    Public NotInheritable Class TargetFolderTemplate

        Private Sub New()
        End Sub

        ''' <summary>Die Platzhalter, die nur im Ordner gelten und als ganze Pfade eingesetzt werden.</summary>
        Public Shared ReadOnly PathPlaceholders As String() = {"{folder}", "{pictures}", "{home}"}

        ''' <summary>Traegt der Text einen Platzhalter? Ein schlichter Ordner bleibt, wie er ist.</summary>
        Public Shared Function HasPlaceholders(template As String) As Boolean
            Return Not String.IsNullOrEmpty(template) AndAlso template.Contains("{"c)
        End Function

        ''' <summary>Die Stuecke eines Pfads, an beiden Schraegstrichen getrennt.</summary>
        Public Shared Function Segments(template As String) As String()
            Return If(template, "").Trim().Split({"/"c, "\"c})
        End Function

        ''' <summary>Der feste Anfang einer Vorlage: alles vor dem ersten Stueck mit Platzhalter, mit
        ''' {pictures} und {home} eingesetzt. Leer, wenn die Vorlage mit {folder} beginnt: dann liegt
        ''' jedes Ziel neben seinem Bild, und einen gemeinsamen Ordner gibt es nicht. Ohne Platzhalter
        ''' der Ordner selbst. Fuer das Anlegen und Pruefen VOR dem Lauf.</summary>
        Public Shared Function FixedRoot(template As String) As String
            Dim text = If(template, "").Trim()
            If Not HasPlaceholders(text) Then Return text
            Dim fixedParts As New List(Of String)()
            For Each part In Segments(text)
                If part = "{pictures}" Then
                    fixedParts.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
                ElseIf part = "{home}" Then
                    fixedParts.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                ElseIf part.Contains("{"c) Then
                    Exit For
                Else
                    fixedParts.Add(part)
                End If
            Next
            If fixedParts.Count = 0 Then Return ""
            Dim joined = String.Join(Path.DirectorySeparatorChar, fixedParts)
            ' "/a" zerfaellt in "" und "a"; nur der Wurzelstrich allein waere kein Ordner.
            Return If(joined.Length = 0, "", joined)
        End Function
    End Class

End Namespace
