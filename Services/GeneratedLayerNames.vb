Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Die Namen, die FerrumPix einer neuen Ebene oder Gruppe SELBST gibt ("Gruppe 2",
    ''' "Auswahl 1", "Maskenebene 3"). Gespeichert wird immer der deutsche Grundwortlaut, übersetzt
    ''' erst beim Anzeigen. Vorher stand der Name in der Sprache im Rezept, in der die Ebene
    ''' entstanden war: nach einem Sprachwechsel hieß sie weiter "Gruppe 1", und eine Datei aus
    ''' einer englischen Sitzung zeigte in der deutschen "Group 1". "Auswahl N" lief gar nicht durch
    ''' die Übersetzung und stand in jeder Sprache deutsch da (Nutzerbefund).
    '''
    ''' Ein selbst vergebener Name, der zufällig so heißt, wird mit übersetzt - das schadet nicht.
    ''' Namen, die schon in einer anderen Sprache gespeichert sind, bleiben, wie sie sind.</summary>
    Public NotInheritable Class GeneratedLayerNames

        Private Sub New()
        End Sub

        Public Const Group As String = "Gruppe"
        Public Const Selection As String = "Auswahl"
        Public Const SelectionLayer As String = "Auswahlebene"
        Public Const MaskLayer As String = "Maskenebene"
        Public Const RadialGradient As String = "Radialer Verlauf"
        Public Const LinearGradient As String = "Linearer Verlauf"
        Public Const MigratedMaskLayer As String = "Übernommene Maskenebene"
        Public Const PastedImage As String = "Eingefügtes Bild"
        Public Const Stroke As String = "Kontur"
        Private Const CopySuffix As String = " Kopie"

        Private Shared ReadOnly NumberedName As New Regex("^(.*\S) (\d+)$", RegexOptions.CultureInvariant)

        ''' <summary>Der gespeicherte Name mit Nummer, im Grundwortlaut.</summary>
        Public Shared Function Numbered(baseName As String, number As Integer) As String
            Return baseName & " " & number.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Der gespeicherte Name einer Kopie. Der Zusatz bleibt deutsch und wird wie der
        ''' Rest beim Anzeigen übersetzt.</summary>
        Public Shared Function CopyOf(name As String) As String
            Return name & CopySuffix
        End Function

        ''' <summary>Der Name, wie er angezeigt wird: ein Grundwortlaut, auch mit Nummer, übersetzt;
        ''' alles andere unverändert.</summary>
        Public Shared Function Display(name As String) As String
            If String.IsNullOrWhiteSpace(name) Then Return If(name, "")
            ' Eine duplizierte Ebene heißt "<Name> Kopie", auch mehrfach hintereinander.
            If name.EndsWith(CopySuffix, StringComparison.Ordinal) AndAlso name.Length > CopySuffix.Length Then
                Return Display(name.Substring(0, name.Length - CopySuffix.Length)) & " " & LocalizationService.T("Kopie")
            End If
            Dim whole = Translate(name)
            If whole IsNot Nothing Then Return whole
            Dim match = NumberedName.Match(name)
            If Not match.Success Then Return name
            Dim baseText = Translate(match.Groups(1).Value)
            Return If(baseText Is Nothing, name, baseText & " " & match.Groups(2).Value)
        End Function

        ''' <summary>Jeder Grundwortlaut als eigenes Literal in T(): so findet die Diagnose seinen
        ''' Schlüssel, was sie bei T() mit einer Variablen nicht könnte.</summary>
        Private Shared Function Translate(baseName As String) As String
            Select Case baseName
                Case Group : Return LocalizationService.T("Gruppe")
                Case Selection : Return LocalizationService.T("Auswahl")
                Case SelectionLayer : Return LocalizationService.T("Auswahlebene")
                Case MaskLayer : Return LocalizationService.T("Maskenebene")
                Case RadialGradient : Return LocalizationService.T("Radialer Verlauf")
                Case LinearGradient : Return LocalizationService.T("Linearer Verlauf")
                Case MigratedMaskLayer : Return LocalizationService.T("Übernommene Maskenebene")
                Case PastedImage : Return LocalizationService.T("Eingefügtes Bild")
                Case Stroke : Return LocalizationService.T("Kontur")
                Case Else : Return Nothing
            End Select
        End Function
    End Class

End Namespace
