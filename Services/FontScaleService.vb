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

        ''' <summary>Der zuletzt angewandte Versatz. Wer eine feste Breite fuer Text vorhaelt, muss
        ''' ihn kennen - eine Leiste mit fester Breite schneidet sonst bei groesserer Schrift das
        ''' letzte Wort ab, und im Bild sieht das aus wie ein fehlender Text.</summary>
        Public Shared ReadOnly Property CurrentOffset As Integer

        Public Shared Sub Apply(offset As Integer)
            Dim app = Application.Current
            If app Is Nothing Then Return

            EnsureBaseSizes(app)
            offset = AppSettingsService.NormalizeFontSizeOffset(offset)
            _CurrentOffset = offset

            For Each key In TextSizeKeys
                Dim baseSize As Double
                If Not _baseSizes.TryGetValue(key, baseSize) Then Continue For
                app.Resources(key) = baseSize + offset
            Next
        End Sub

        ''' <summary>Die im Theme deklarierte Ausgangsgröße einer Schriftgrößen-Ressource, also der
        ''' Wert OHNE jeden Versatz. 0, wenn die Ressource nicht bekannt ist.
        '''
        ''' Wozu: Die Einstellungen zeigen die acht Stufen als Schaltflächen, jede mit ihrer eigenen
        ''' Schriftgröße - man soll sehen, was man wählt, statt eine Zahl zu deuten. Dafür braucht
        ''' die Ansicht die Ausgangsgröße, und sie soll sie nicht ein zweites Mal im Quelltext
        ''' stehen haben: das AXAML bleibt die einzige Stelle, an der die Zahlen stehen.</summary>
        Public Shared Function BaseSize(key As String) As Double
            Dim app = Application.Current
            If app Is Nothing OrElse String.IsNullOrEmpty(key) Then Return 0
            EnsureBaseSizes(app)
            Dim value As Double
            If _baseSizes IsNot Nothing AndAlso _baseSizes.TryGetValue(key, value) Then Return value
            Return 0
        End Function

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
