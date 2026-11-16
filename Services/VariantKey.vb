Imports System

Namespace Services

    ''' <summary>
    ''' Der Schluessel einer zusaetzlichen Variante einer Aufnahme: der Pfad der Datei, dahinter die
    ''' Kennung der Variante ("/fotos/bild.cr2?variant=9b1e44c0").
    '''
    ''' Die Anwendung kennt ein Bild ueberall an seinem Pfad: Galerie, Betrachter, Editor, Katalog
    ''' und Kachelspeicher reichen eine Zeichenkette weiter. Eine zusaetzliche Variante ist dieselbe
    ''' Datei mit einem anderen Rezept und eigenen Katalogdaten; sie braucht also eine zweite
    ''' Zeichenkette, die sich wie ein Pfad weiterreichen laesst. Die Standardvariante ist die Datei
    ''' selbst und traegt ihren blanken Pfad.
    '''
    ''' Der Schluessel ist KEIN Pfad auf der Platte. Wer eine Datei anfasst, nimmt
    ''' <see cref="FileOf"/>; wer Rezept oder Katalogdaten meint, reicht den Schluessel an
    ''' RawSidecarService und LibraryService, die ihn beide verstehen.
    '''
    ''' Das Fragezeichen ist unter Windows in Dateinamen verboten, und dahinter muessen genau acht
    ''' Hexziffern stehen: eine echte Bilddatei endet nie so, weil sie auf ihre Endung endet.
    ''' </summary>
    Public NotInheritable Class VariantKey

        Private Sub New()
        End Sub

        Private Const Marker As String = "?variant="
        Private Const IdLength As Integer = 8

        ''' <summary>Der Schluessel fuer diese Variante der Datei; ohne Kennung der Pfad selbst.</summary>
        Public Shared Function Make(filePath As String, variantId As String) As String
            If String.IsNullOrEmpty(filePath) OrElse String.IsNullOrEmpty(variantId) Then Return filePath
            Return FileOf(filePath) & Marker & variantId
        End Function

        ''' <summary>Zerlegt einen Schluessel. Das Ergebnis ist der Pfad der Datei,
        ''' <paramref name="variantId"/> die Kennung oder leer bei einem gewoehnlichen Pfad.</summary>
        Public Shared Function Split(key As String, ByRef variantId As String) As String
            variantId = ""
            If String.IsNullOrEmpty(key) Then Return key
            Dim at = key.Length - IdLength - Marker.Length
            If at <= 0 OrElse String.CompareOrdinal(key, at, Marker, 0, Marker.Length) <> 0 Then Return key
            For index = at + Marker.Length To key.Length - 1
                If Not Uri.IsHexDigit(key(index)) Then Return key
            Next
            variantId = key.Substring(at + Marker.Length)
            Return key.Substring(0, at)
        End Function

        ''' <summary>Der Pfad der Datei hinter einem Schluessel; ein gewoehnlicher Pfad bleibt, wie er ist.</summary>
        Public Shared Function FileOf(key As String) As String
            Dim ignored As String = Nothing
            Return Split(key, ignored)
        End Function

        ''' <summary>Die Kennung der Variante; leer bei einem gewoehnlichen Pfad.</summary>
        Public Shared Function IdOf(key As String) As String
            Dim id As String = Nothing
            Split(key, id)
            Return id
        End Function

        Public Shared Function IsVariant(key As String) As Boolean
            Return IdOf(key).Length > 0
        End Function

        ''' <summary>Eine neue Kennung: acht kleine Hexziffern.</summary>
        Friend Shared Function NewId() As String
            Return Guid.NewGuid().ToString("N").Substring(0, IdLength)
        End Function

    End Class

End Namespace
