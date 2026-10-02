Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>Wofuer ein PNG geschrieben wird. Danach richtet sich, wie stark gepackt wird;
    ''' verlustfrei ist es in jedem Fall.</summary>
    Public Enum PngPurpose
        ''' <summary>Wird sofort wieder gelesen und dann verworfen: die Fremdformate auf dem Weg
        ''' zu Betrachter, Miniatur und Editor, kleine Zwischenbilder. Ungepackt.</summary>
        Transient
        ''' <summary>Bleibt liegen, aber nur fuer die Anwendung selbst: Ebenen, die Retusche-Ebene,
        ''' Masken, Basis- und Anzeigebild im Buendel. Schnell gepackt.</summary>
        Stored
        ''' <summary>Die Datei, die der Nutzer als PNG speichert. Groesse zaehlt, die Zeit
        ''' zahlt er einmal. Skias Vorgabe.</summary>
        Export
    End Enum

    ''' <summary>
    ''' DER EINE WEG, ein PNG zu schreiben. Jede Stelle sagt dabei, wofuer (<see cref="PngPurpose"/>);
    ''' die Packstufe dazu steht nur hier. Eine Pruefung haelt fest, dass niemand daran vorbei
    ''' kodiert. Ausnahmen mit eigenem Kodierer: SVG (die Bibliothek schreibt selbst), HEIF auf
    ''' macOS (ImageIO) und das Standbild aus libmpv.
    '''
    ''' DIE QUALITAETSZAHL WIRKT BEI PNG NICHT. Skia nimmt sie nur fuer JPEG und WebP; bei PNG waren
    ''' 100, 90, 60 und 0 gemessen dieselbe Datei in derselben Zeit. Stellen, die mit "Stufe 60"
    ''' schnell sein wollten, liefen deshalb mit der vollen Vorgabe. Was wirkt, sind zlib-Stufe und
    ''' Filter, und die bestimmen nur Zeit und Groesse: die Bildpunkte sind in jeder Stufe dieselben.
    '''
    ''' Gemessen an einem 18-MP-Foto (Kodieren, Einlesen, Groesse):
    '''   Vorgabe, alle Filter, zlib 6   1,9 s    0,19 s   41,7 MB
    '''   Filter Sub, zlib 1             0,6 s    0,21 s   39,9 MB
    '''   ungepackt                      0,28 s   0,05 s   71,8 MB
    ''' Bei Fotos ist die schnelle Stufe also nicht einmal groesser. Bei glatten Flaechen schon: eine
    ''' fast leere Malebene hatte 427 statt 94 KB. Fuer Dateien der Anwendung ist das der richtige
    ''' Tausch, fuer die Ausgabe des Nutzers nicht.
    '''
    ''' Ungepackt bleibt auf <see cref="TransientUncompressedLimitBytes"/> begrenzt: ein
    ''' MemoryStream endet bei 2 GB, und ein sehr grosses TIFF passte ungepackt nicht mehr hinein.
    ''' Darueber wird auch das Durchreichen schnell gepackt.
    '''
    ''' Kodiert wird ueber das Pixmap, nicht ueber SKImage.FromBitmap: Raster-SKImages verlangen
    ''' Premul oder Opaque, ein Bild mit Alpha liegt aber oft als Unpremul vor (PNG speichert
    ''' geradliniges Alpha, so bleibt es verlustfrei).
    ''' </summary>
    Public NotInheritable Class PngEncoder

        Private Sub New()
        End Sub

        ''' <summary>Rohgroesse, bis zu der zum Durchreichen ungepackt geschrieben wird (512 MB, gut
        ''' 130 MP bei 8 Bit mit Alpha).</summary>
        Friend Const TransientUncompressedLimitBytes As Long = 512L * 1024L * 1024L

        ''' <summary>Das Bild als PNG, oder Nothing. Besitz beim Aufrufer.</summary>
        Public Shared Function Encode(bitmap As SKBitmap, purpose As PngPurpose) As SKData
            If bitmap Is Nothing Then Return Nothing
            Using pixmap = bitmap.PeekPixels()
                Return Encode(pixmap, purpose)
            End Using
        End Function

        ''' <summary>Dasselbe fuer Pixel ohne eigenes Bitmap, etwa den Schnappschuss einer
        ''' Zeichenflaeche (SKImage.PeekPixels). Besitz beim Aufrufer.</summary>
        Public Shared Function Encode(pixmap As SKPixmap, purpose As PngPurpose) As SKData
            If pixmap Is Nothing Then Return Nothing
            Return pixmap.Encode(OptionsFor(purpose, CLng(pixmap.RowBytes) * pixmap.Height))
        End Function

        ''' <summary>Dasselbe als Speicherstrom, an den Anfang gespult, oder Nothing.</summary>
        Public Shared Function EncodeToStream(bitmap As SKBitmap, purpose As PngPurpose) As MemoryStream
            Using data = Encode(bitmap, purpose)
                If data Is Nothing Then Return Nothing
                Dim ms As New MemoryStream()
                data.SaveTo(ms)
                ms.Position = 0
                Return ms
            End Using
        End Function

        ''' <summary>Dasselbe als Bytes, oder Nothing.</summary>
        Public Shared Function EncodeToBytes(bitmap As SKBitmap, purpose As PngPurpose) As Byte()
            Using data = Encode(bitmap, purpose)
                Return data?.ToArray()
            End Using
        End Function

        Friend Shared Function OptionsFor(purpose As PngPurpose, rawBytes As Long) As SKPngEncoderOptions
            Select Case purpose
                Case PngPurpose.Transient
                    If rawBytes <= TransientUncompressedLimitBytes Then
                        Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 0)
                    End If
                    Return FastCompressed()
                Case PngPurpose.Stored
                    Return FastCompressed()
                Case Else
                    Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)
            End Select
        End Function

        ''' <summary>Gemessen die schnellste gepackte Form: der Filter Sub vor zlib 1 schlug "ohne
        ''' Filter" (0,6 gegen 0,8 s) und packt Fotos so gut wie die Vorgabe.</summary>
        Private Shared Function FastCompressed() As SKPngEncoderOptions
            Return New SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)
        End Function

    End Class

End Namespace
