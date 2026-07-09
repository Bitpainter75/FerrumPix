Imports System.Collections.Generic
Imports Avalonia

Namespace Services

    ''' <summary>
    ''' Verschiebt alle Text-Schriftgrößen der Oberfläche um einen ganzzahligen Betrag, indem die
    ''' FP.Font.*-Ressourcen aus FerrumPixTheme.axaml zur Laufzeit überschrieben werden - dasselbe
    ''' Verfahren, mit dem SettingsViewModel.ApplyTheme die Farbpinsel austauscht.
    '''
    ''' Die FP.Glyph.*-Ressourcen bleiben bewusst unberührt: dort sitzen die Symbolzeichen der
    ''' Fenster- und Schließen-Schaltflächen (×, −, +, ↑, ↓, □, ⧉). Sie sind Grafik in fester
    ''' Schaltflächengröße; würden sie mitwachsen, ragten sie über ihren Rand hinaus.
    ''' </summary>
    Public NotInheritable Class FontScaleService

        Private Sub New()
        End Sub

        Private Shared ReadOnly TextSizeKeys As String() = {
            "FP.Font.Label",
            "FP.Font.Caption",
            "FP.Font.Small",
            "FP.Font.Body",
            "FP.Font.ItemTitle",
            "FP.Font.Subtitle",
            "FP.Font.Title",
            "FP.Font.Heading",
            "FP.Font.Display"
        }

        ''' Die im Theme deklarierten Ausgangsgrößen. Sie werden einmalig aus den Ressourcen gelesen,
        ''' bevor der erste Versatz sie überschreibt - damit bleibt das AXAML die einzige Stelle, an der
        ''' die Zahlen stehen, und ein späterer Wechsel dort wirkt automatisch hier.
        Private Shared _baseSizes As Dictionary(Of String, Double)

        Public Shared Sub Apply(offset As Integer)
            Dim app = Application.Current
            If app Is Nothing Then Return

            EnsureBaseSizes(app)
            offset = AppSettingsService.NormalizeFontSizeOffset(offset)

            For Each key In TextSizeKeys
                Dim baseSize As Double
                If Not _baseSizes.TryGetValue(key, baseSize) Then Continue For
                app.Resources(key) = baseSize + offset
            Next
        End Sub

        Private Shared Sub EnsureBaseSizes(app As Application)
            If _baseSizes IsNot Nothing Then Return

            _baseSizes = New Dictionary(Of String, Double)(StringComparer.Ordinal)
            For Each key In TextSizeKeys
                Dim value As Object = Nothing
                If app.TryGetResource(key, Nothing, value) AndAlso TypeOf value Is Double Then
                    _baseSizes(key) = CDbl(value)
                End If
            Next
        End Sub

    End Class

End Namespace
