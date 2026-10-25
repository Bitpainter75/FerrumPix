Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Das JPEG, das der Nutzer speichert: Export aus Editor und Stapel, Collage.
    '''
    ''' VOLLE FARBAUFLOESUNG (4:4:4). Skias Vorgabe halbiert die Farbe in beide Richtungen (4:2:0).
    ''' Gemessen an TEL_0674 mit Cool Glow, gegen Lightrooms Export, der wie der von RAWmakase
    ''' 4:4:4 schreibt: die Steilheit farbiger Kanten (rote Strahlen, Schrift) bei Qualitaet 96
    ''' 0,73 mit 4:2:0 und 1,10 mit 4:4:4. Die Datei wird gut ein Drittel groesser (3,1 auf
    ''' 4,0 MB). Fuer Kacheln und Zwischenbilder der Anwendung bleibt Skias Vorgabe.
    '''
    ''' Kodiert wird ueber das Pixmap wie im <see cref="PngEncoder"/>; JPEG kennt kein Alpha, der
    ''' Aufrufer legt transparente Bereiche vorher auf Weiss.
    ''' </summary>
    Public NotInheritable Class JpegEncoder

        Private Sub New()
        End Sub

        ''' <summary>Das Bild als JPEG mit voller Farbaufloesung, oder Nothing. Besitz beim Aufrufer.</summary>
        Public Shared Function Encode(bitmap As SKBitmap, quality As Integer) As SKData
            If bitmap Is Nothing Then Return Nothing
            Using pixmap = bitmap.PeekPixels()
                If pixmap Is Nothing Then Return Nothing
                Dim options = New SKJpegEncoderOptions(Math.Max(1, Math.Min(100, quality)),
                                                       SKJpegEncoderDownsample.Downsample444,
                                                       SKJpegEncoderAlphaOption.Ignore)
                Return pixmap.Encode(options)
            End Using
        End Function

    End Class

End Namespace
