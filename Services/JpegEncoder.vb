Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Das JPEG, das der Nutzer speichert: Export aus Editor und Stapel, Collage.
    '''
    ''' VOLLE FARBAUFLOESUNG (4:4:4). Skias Vorgabe halbiert die Farbe in beide Richtungen (4:2:0).
    ''' Gemessen an TEL_0674 mit einem Kanalkurven-Preset, gegen den Export des Referenzprogramms, der 4:4:4
    ''' schreibt: die Steilheit farbiger Kanten (rote Strahlen, Schrift) bei Qualitaet 96
    ''' 0,73 mit 4:2:0 und 1,10 mit 4:4:4. Die Datei wird gut ein Drittel groesser (3,1 auf
    ''' 4,0 MB). Fuer Kacheln und Zwischenbilder der Anwendung bleibt Skias Vorgabe.
    '''
    ''' Kodiert wird ueber das Pixmap wie im <see cref="PngEncoder"/>; JPEG kennt kein Alpha, der
    ''' Aufrufer legt transparente Bereiche vorher auf Weiss.
    '''
    ''' MIT sRGB-PROFIL. Die geschriebenen Bildpunkte sind immer sRGB (FORMATE_UND_SPEICHERN,
    ''' Farbmanagement). Das Pixmap bekommt deshalb den Farbraum sRGB zugeschrieben - nicht
    ''' umgerechnet, nur benannt -, und Skia legt dafuer ein kleines ICC-Profil in APP2. Ohne das trug
    ''' ein JPEG ohne Aufnahmedaten gar keine Farbangabe, und ein Programm, das dann nicht sRGB
    ''' annimmt, deutete die Datei nach Belieben. Das Profil der QUELLE bleibt weiter draussen
    ''' (ImageProcessorSave.IsJpegIccSegment); dieses hier beschreibt die geschriebenen Punkte.
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
                Using srgb = SKColorSpace.CreateSrgb()
                    Using tagged = New SKPixmap(pixmap.Info.WithColorSpace(srgb), pixmap.GetPixels(), pixmap.RowBytes)
                        Return tagged.Encode(options)
                    End Using
                End Using
            End Using
        End Function

    End Class

End Namespace
