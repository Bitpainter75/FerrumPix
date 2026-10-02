Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Packt ein dekodiertes Bild als PNG-Strom fuer die Formate, die Skia selbst nicht lesen kann
    ''' (JPEG XL, HEIF, TIFF, PSD, ICO, Videostandbild). Der Strom ist nur ein Zwischenschritt: der
    ''' Abnehmer liest ihn gleich wieder ein, als Miniatur, im Betrachter oder als Ausgangsbild des
    ''' Editors.
    '''
    ''' DESHALB UNGEPACKT. Skias Vorgabe (zlib 6, alle Filter durchprobiert) kostete an einem
    ''' 18-MP-Bild 1,9 s auf einem Kern, das Einlesen danach 0,19 s. Ungepackt sind es 0,28 s und
    ''' 0,05 s, bei gleichem Inhalt. Alle Decodes laufen durch dasselbe Tor (DecodeGate), und jede
    ''' Miniatur eines solchen Formats stand mit dieser Zeit vor dem grossen Bild in der Schlange:
    ''' ein Ordner voller JPEG XL hielt den Betrachter ueber eine Minute auf (Nutzermeldung).
    '''
    ''' VERLUSTFREI MUSS ES BLEIBEN. JPEG waere ebenso schnell, verloere aber die Transparenz,
    ''' halbierte die Farbaufloesung und gaebe dem Editor ein schon einmal komprimiertes
    ''' Ausgangsbild.
    '''
    ''' Der Preis ist Speicher, solange der Strom lebt: Breite mal Hoehe mal Bytes je Punkt. Ab
    ''' <see cref="StoredLimitBytes"/> wird deshalb doch gepackt, mit der schnellsten Stufe: ein
    ''' MemoryStream endet bei 2 GB, und ein sehr grosses TIFF passte ungepackt nicht mehr hinein.
    ''' </summary>
    Public NotInheritable Class PreviewStreamEncoder

        Private Sub New()
        End Sub

        ''' <summary>Rohgroesse, bis zu der ungepackt geschrieben wird (512 MB, gut 130 MP bei
        ''' 8 Bit mit Alpha).</summary>
        Friend Const StoredLimitBytes As Long = 512L * 1024L * 1024L

        ''' <summary>Das Bild als PNG-Strom, an den Anfang gespult, oder Nothing.
        '''
        ''' Ueber das Pixmap, nicht ueber SKImage.FromBitmap: Raster-SKImages verlangen Premul oder
        ''' Opaque, ein Bild mit Alpha liegt hier aber oft als Unpremul vor (PNG speichert
        ''' geradliniges Alpha, so bleibt es verlustfrei).</summary>
        Public Shared Function Encode(bitmap As SKBitmap) As MemoryStream
            If bitmap Is Nothing Then Return Nothing
            Using pixmap = bitmap.PeekPixels()
                If pixmap Is Nothing Then Return Nothing
                Using data = pixmap.Encode(OptionsFor(CLng(pixmap.RowBytes) * pixmap.Height))
                    If data Is Nothing Then Return Nothing
                    Dim ms As New MemoryStream()
                    data.SaveTo(ms)
                    ms.Position = 0
                    Return ms
                End Using
            End Using
        End Function

        Friend Shared Function OptionsFor(rawBytes As Long) As SKPngEncoderOptions
            If rawBytes <= StoredLimitBytes Then Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 0)
            ' Gemessen die schnellste gepackte Form: der Sub-Filter vor zlib 1 schlug "ohne Filter".
            Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)
        End Function

    End Class

End Namespace
