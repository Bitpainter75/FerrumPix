Imports SkiaSharp

Namespace Services

    ''' Farbmanagement der EINGANGSSEITE: bringt ein Bild, das ein anderes Farbprofil als sRGB
    ''' traegt, nach sRGB - und zwar genau einmal, direkt nach dem Decode.
    '''
    ''' Warum hier und nicht in der Reglerkette: die Kette rechnet auf farbraumlosen Bgra8888-Bitmaps
    ''' (siehe ImageProcessor). Farbraumlos heisst bei Skia "unverwaltet": die Zahlen werden genommen,
    ''' wie sie sind. Ein Adobe-RGB- oder Display-P3-Bild kam damit als sRGB an, und das ist ein
    ''' STILLER Fehler - das Bild wird flau und die Farben wandern, ohne dass irgendwo etwas meldet.
    ''' Sichtbar wird es vor allem an gesaettigtem Rot und Gruen.
    '''
    ''' Der Arbeitsfarbraum der Anwendung ist damit sRGB: alles kommt als sRGB herein, die Kette
    ''' rechnet darin, alles geht als sRGB hinaus. Ein waehlbarer Arbeits- oder Ausgabefarbraum ist
    ''' etwas anderes und hier ausdruecklich NICHT gebaut.
    '''
    ''' <para>Die Bitgleichheit ist die Bedingung des Umbaus: ein Bild OHNE Profil und ein Bild MIT
    ''' sRGB-Profil laufen unveraendert durch, ohne Kopie und ohne Rechnung. Nur ein tatsaechlich
    ''' abweichendes Profil nimmt den neuen Weg. Damit bleibt der Golden-Hash der Pipeline fuer alle
    ''' bisherigen Pruefbilder stehen.</para>
    '''
    ''' <para>Erste Quelle ist ein EINGEBETTETES ICC-Profil. Fehlt es, wird das EXIF gefragt: manche
    ''' Kameras legen ein Bild ohne Profil ab und beschriften Adobe RGB allein ueber ColorSpace=
    ''' Uncalibrated plus InteropIndex=R03 (siehe <see cref="ProfileFromExif"/>). Weiter geht die
    ''' Erkennung nicht - ein Profilname im XMP bleibt aussen vor.</para>
    Public NotInheritable Class ColorManagementService
        Private Sub New()
        End Sub

        ''' <summary>Das Ziel aller Wandlungen. Skia legt dahinter einen festen Wert an, deshalb ist
        ''' das Halten billiger als das wiederholte Erzeugen.</summary>
        Private Shared ReadOnly _srgb As SKColorSpace = SKColorSpace.CreateSrgb()

        ''' <summary>Adobe RGB (1998): dieselben Primaerfarben, dieselbe Kurve, nur eben aus zwei
        ''' EXIF-Feldern erschlossen statt aus einem eingebetteten Profil. Einmal angelegt, weil es
        ''' fuer jede so beschriftete Datei dasselbe ist.</summary>
        Private Shared ReadOnly _adobeRgb As SKColorSpace =
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.AdobeRgb)

        ''' <summary>EXIF-Wert 65535 fuer ColorSpace: "Uncalibrated", also ausdruecklich nicht sRGB.
        ''' Was es stattdessen ist, sagt erst der Kompatibilitaetseintrag.</summary>
        Private Const ExifColorSpaceUncalibrated As Integer = 65535

        ''' <summary>Das Profil, das fuer diese Datei GILT: das eingebettete, wenn es eines gibt,
        ''' sonst das aus dem EXIF erschlossene. Nothing heisst weiterhin "sRGB annehmen".
        '''
        ''' Die Reihenfolge ist keine Geschmacksfrage: ein ABWEICHENDES eingebettetes Profil ist eine
        ''' Tatsache, der EXIF-Weg eine Auslegung. Eine Auslegung darf eine Tatsache nie
        ''' ueberstimmen - deshalb wird gar nicht erst gefragt, sobald ein Profil da ist, das nicht
        ''' sRGB ist.
        '''
        ''' Bei sRGB wird dagegen gefragt, und das ist Absicht: Skia gibt fuer ein JPEG OHNE Profil
        ''' ebenfalls sRGB zurueck. "sRGB" heisst an dieser Stelle also nicht "die Datei sagt sRGB",
        ''' sondern "niemand hat etwas anderes gesagt" - und genau dann ist das EXIF die bessere
        ''' Auskunft. Eine Datei, die ein echtes sRGB-Profil einbettet UND sich im EXIF als Adobe RGB
        ''' beschriftet, widerspraeche sich selbst; Kameras, die das zweite tun, tun das erste
        ''' nicht.</summary>
        ''' <param name="data">Die Bilddatei, wie sie ohnehin schon im Speicher liegt. Der
        ''' Metadatenleser bekommt einen eigenen Datenstrom darauf - es wird nichts nachgeladen.</param>
        Public Shared Function EffectiveProfile(decoderProfile As SKColorSpace, data As SKData) As SKColorSpace
            If NeedsConversion(decoderProfile) Then Return decoderProfile
            If data Is Nothing Then Return decoderProfile
            Try
                Using stream = data.AsStream()
                    Return If(ProfileFromExif(stream), decoderProfile)
                End Using
            Catch ex As Exception
                ' Ein unlesbares EXIF heisst nur: kein Hinweis. Das Bild laeuft wie bisher.
                DiagnosticLogService.LogException("Color.EffectiveProfile", ex)
                Return decoderProfile
            End Try
        End Function

        ''' <summary>Wie oben, nur aus einem Datenstrom. Er wird an seine Ausgangsstelle
        ''' zurueckgesetzt: die Aufrufer lesen danach weiter, und ein Metadatenblick darf ihren
        ''' Decode nicht verstellen.</summary>
        Public Shared Function EffectiveProfile(decoderProfile As SKColorSpace, stream As IO.Stream) As SKColorSpace
            If NeedsConversion(decoderProfile) Then Return decoderProfile
            If stream Is Nothing OrElse Not stream.CanSeek Then Return decoderProfile
            Dim startPosition = stream.Position
            Try
                stream.Seek(0, IO.SeekOrigin.Begin)
                Return If(ProfileFromExif(stream), decoderProfile)
            Catch ex As Exception
                DiagnosticLogService.LogException("Color.EffectiveProfile", ex)
                Return decoderProfile
            Finally
                Try
                    stream.Seek(startPosition, IO.SeekOrigin.Begin)
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Liest den Farbraum aus den EXIF-Feldern. Adobe RGB wird NUR angenommen, wenn
        ''' BEIDE Felder es sagen: ColorSpace steht auf Uncalibrated und der Kompatibilitaetseintrag
        ''' auf "R03". Ein einzelnes Feld reicht bewusst nicht - eine falsche Annahme verschiebt die
        ''' Farben eines Bildes, das in Ordnung war, und das ist schlimmer als der Fall, den sie
        ''' beheben soll. Alles andere (auch "R98" fuer sRGB) ergibt Nothing.
        '''
        ''' Und ein EINGEBETTETES Profil schliesst den Weg von vornherein aus, auch ein sRGB-Profil:
        ''' hier - und nur hier - laesst sich "die Datei sagt sRGB" von "die Datei sagt nichts"
        ''' unterscheiden, denn der Metadatenleser sieht den ICC-Block selbst.</summary>
        Public Shared Function ProfileFromExif(stream As IO.Stream) As SKColorSpace
            If stream Is Nothing Then Return Nothing
            Dim verzeichnisse = MetadataExtractor.ImageMetadataReader.ReadMetadata(stream)
            If verzeichnisse Is Nothing Then Return Nothing

            Dim uncalibrated = False
            Dim r03 = False
            For Each verzeichnis In verzeichnisse
                If TypeOf verzeichnis Is MetadataExtractor.Formats.Icc.IccDirectory Then
                    ' DIE DATEI TRAEGT EIN PROFIL - dann gilt es, Punkt. Am Decoder ist das nicht
                    ' abzulesen: er meldet fuer "eingebettetes sRGB" und fuer "gar kein Profil"
                    ' dasselbe. Und der Fall ist keineswegs weit hergeholt: wer eine Adobe-RGB-Datei
                    ' nach sRGB wandelt, bekommt haeufig ein sRGB-Profil eingebettet, waehrend die
                    ' alten EXIF-Felder unveraendert stehen bleiben. Ohne diese Zeile wuerde so eine
                    ' Datei ein zweites Mal gewandelt und stuende zu bunt da.
                    Return Nothing
                ElseIf TypeOf verzeichnis Is MetadataExtractor.Formats.Exif.ExifSubIfdDirectory Then
                    ' Der ROHWERT, nicht die Beschreibung: die uebersetzt 65535 je nach Fassung zu
                    ' "Undefined" oder "Unknown (65535)", und daran haengt hier eine Entscheidung.
                    Dim roh = verzeichnis.GetObject(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagColorSpace)
                    If roh IsNot Nothing Then
                        Try
                            If Convert.ToInt32(roh, Globalization.CultureInfo.InvariantCulture) = ExifColorSpaceUncalibrated Then uncalibrated = True
                        Catch
                            ' Ein Feld, das keine Zahl ist, sagt hier nichts.
                        End Try
                    End If
                ElseIf TypeOf verzeichnis Is MetadataExtractor.Formats.Exif.ExifInteropDirectory Then
                    Dim roh = verzeichnis.GetObject(MetadataExtractor.Formats.Exif.ExifInteropDirectory.TagInteropIndex)
                    Dim text = If(roh?.ToString(), "")
                    If text.Trim().StartsWith("R03", StringComparison.OrdinalIgnoreCase) Then r03 = True
                End If
            Next

            If uncalibrated AndAlso r03 Then Return _adobeRgb
            Return Nothing
        End Function

        ''' <summary>Braucht dieses Profil eine Wandlung? Nothing und sRGB nicht.</summary>
        Public Shared Function NeedsConversion(profile As SKColorSpace) As Boolean
            If profile Is Nothing Then Return False
            Return Not profile.IsSrgb
        End Function

        ''' <summary>Bringt eine dekodierte Bitmap nach sRGB.
        '''
        ''' Rueckgabe wie bei ImageOrientationService.ApplyOrientation: war nichts zu tun, kommt die
        ''' QUELLE selbst zurueck. Der Aufrufer erkennt den Fall an der Referenz und darf nur dann
        ''' freigeben, wenn er eine neue Bitmap bekommen hat:
        '''
        ''' <code>
        ''' Dim managed = ColorManagementService.ToSrgb(decoded)
        ''' If Not Object.ReferenceEquals(managed, decoded) Then decoded.Dispose()
        ''' </code>
        ''' </summary>
        ''' <param name="sourceProfile">Das Profil der QUELLE, wenn die Bitmap es selbst nicht mehr
        ''' traegt (etwa nach einem Decode in eine farbraumlose Zielangabe). Nothing = das Profil der
        ''' Bitmap gilt.</param>
        Public Shared Function ToSrgb(source As SKBitmap, Optional sourceProfile As SKColorSpace = Nothing) As SKBitmap
            If source Is Nothing Then Return Nothing
            Dim profile = If(sourceProfile, source.ColorSpace)
            If Not NeedsConversion(profile) Then Return source

            Try
                ' Die Quellpixel mit ihrem Profil beschreiben - auch dann, wenn die Bitmap selbst
                ' keines mehr traegt. Ohne diesen Schritt waere die Wandlung eine Nulloperation.
                Dim sourceInfo = source.Info.WithColorSpace(profile)
                ' Bildaufbau der QUELLE beibehalten und nur den Farbraum tauschen. Ein fest
                ' verdrahtetes Bgra8888/Premul waere hier ein stiller Umbau: der HEIF-Weg etwa sagt
                ' seinen Aufrufern Unpremul zu, und die Zusage darf eine Farbwandlung nicht brechen.
                Dim managedInfo = sourceInfo.WithColorSpace(_srgb)
                ' Das Ergebnis ist ab hier sRGB, und die Kette erwartet farbraumlose Bitmaps: eine
                ' einzelne Bitmap MIT Profil mitten in einer Kette ohne Profile waere ein Sonderfall,
                ' den jede spaetere Stufe kennen muesste. Die Zielbitmap traegt deshalb von vornherein
                ' KEINEN Farbraum. Sie ist nur der Speicher dahinter - WONACH gewandelt wird, sagt die
                ' Angabe, die ReadPixels bekommt (managedInfo), nicht die Bitmap. Frueher wurde in eine
                ' Bitmap MIT sRGB-Profil gewandelt und der Farbraum danach durch eine zweite volle
                ' Bildkopie abgestreift; bei 24 MP war das eine Neuanlage von 96 MB und der doppelte
                ' Weg durch den Speicher, weil zeilenweise ueber ein Zwischenarray kopiert wurde.
                ' Bildaufbau, Zeilenlaenge und Rechnung sind unveraendert, nur die Kopie entfaellt.
                Dim plainInfo = New SKImageInfo(sourceInfo.Width, sourceInfo.Height,
                                                sourceInfo.ColorType, sourceInfo.AlphaType)
                Dim managed As SKBitmap = Nothing
                Try
                    managed = New SKBitmap(plainInfo)
                    Using pixels = New SKPixmap(sourceInfo, source.GetPixels(), source.RowBytes)
                        ' Hier faellt die eigentliche Rechnung an: Skia legt die Uebertragungsfunktion
                        ' und die Primaerfarben der Quelle an und rechnet in sRGB um.
                        If Not pixels.ReadPixels(managedInfo, managed.GetPixels(), managed.RowBytes) Then
                            DiagnosticLogService.LogAlways("Color.ToSrgb",
                                $"ReadPixels abgelehnt, Quelle bleibt unveraendert: {DescribeProfile(profile)}")
                            managed.Dispose()
                            Return source
                        End If
                    End Using

                    Return managed
                Catch
                    managed?.Dispose()
                    Throw
                End Try
            Catch ex As Exception
                ' Ein Bild lieber unverwaltet zeigen als gar nicht.
                DiagnosticLogService.LogException("Color.ToSrgb", ex)
                Return source
            End Try
        End Function

        ''' <summary>Ab welchem Abstand vom Grau die sanfte Abbildung zu wirken beginnt. Der Abstand
        ''' ist je Kanal (Maximum - Kanal) / Maximum im linearen sRGB: 0 ist grau, 1 der Rand von
        ''' sRGB, darueber liegt die Farbe ausserhalb. Unter der Schwelle bleibt jede Farbe, wie sie
        ''' ist. 0,8 ist der Wert der Referenz-Gamutkompression aus ACES, nicht an Bildern gemessen.</summary>
        Private Const GamutThreshold As Double = 0.8

        ''' <summary>Form der Kurve zwischen Schwelle und Rand (ebenfalls der Wert aus ACES).</summary>
        Private Const GamutPower As Double = 1.2

        ''' <summary>So viele Zeilen rechnet ein Faden am Stueck; das Zwischenfeld in Gleitkomma
        ''' bleibt damit klein (16 Byte je Bildpunkt), statt das ganze Bild zu verdoppeln.</summary>
        Private Const GamutBandRows As Integer = 64

        ''' <summary>sRGB mit linearer Kurve: der Raum, in dem die Abbildung rechnet.</summary>
        Private Shared ReadOnly _linearSrgb As SKColorSpace =
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Linear, SKColorSpaceXyz.Srgb)

        ''' <summary>Wie <see cref="ToSrgb"/>, aber Farben ausserhalb von sRGB werden SANFT
        ''' hereingeholt statt hart abgeschnitten. Gleicher Vertrag: ohne Profil oder mit sRGB kommt
        ''' die Quelle selbst zurueck, sonst ein neues Bitmap im Bildaufbau der Quelle (8 oder 16 Bit)
        ''' ohne Farbraum.
        '''
        ''' WARUM: die Kette rechnet in sRGB von 0 bis 1 (auch das tiefe Bild), Werte darueber hinaus
        ''' traegt sie nicht. Skia schneidet beim Wandeln jeden Kanal einzeln ab; ein gesaettigtes
        ''' Rot oder Gruen aus Display P3 laeuft dann in eine Flaeche gleicher Werte, und Verlaeufe
        ''' darin reissen ab. Hier rechnet Skia zuerst ohne Abschneiden in lineares sRGB
        ''' (Gleitkomma), danach wird je Kanal der Abstand vom Grau oberhalb der Schwelle
        ''' zusammengedrueckt, sodass der aeusserste Punkt des Quellraums genau auf dem Rand von sRGB
        ''' landet (Referenz-Gamutkompression aus ACES, der hellste Kanal bleibt stehen). Ist der
        ''' hellste Kanal danach ueber 1, wird die Farbe bei gleicher Helligkeit (Y) entsaettigt, bis
        ''' er 1 erreicht: Farbton und Helligkeit bleiben, statt dass ein Kanal allein abgeschnitten
        ''' wird. Zuletzt rechnet Skia nach sRGB zurueck.
        '''
        ''' Farben unterhalb der Schwelle kommen bis auf Rundung so an wie bei <see cref="ToSrgb"/>.
        ''' Was ausserhalb von sRGB lag, bleibt AUSSERHALB VERLOREN - es wird nur unterscheidbar
        ''' hereingeholt; einen weiteren Arbeitsfarbraum hat die Kette nicht.
        '''
        ''' Genutzt vom HEIF-Weg. Die uebrigen Leser (JPEG, TIFF, JPEG XL, PSD) wandeln weiter ueber
        ''' <see cref="ToSrgb"/>.</summary>
        Public Shared Function ToSrgbSoftGamut(source As SKBitmap, Optional sourceProfile As SKColorSpace = Nothing) As SKBitmap
            If source Is Nothing Then Return Nothing
            Dim profile = If(sourceProfile, source.ColorSpace)
            If Not NeedsConversion(profile) Then Return source
            Dim scales = GamutCompressionScales(profile)
            If scales Is Nothing Then Return ToSrgb(source, profile)

            Dim managed As SKBitmap = Nothing
            Try
                Dim sourceInfo = source.Info.WithColorSpace(profile)
                Dim width = source.Width
                Dim height = source.Height
                managed = New SKBitmap(New SKImageInfo(width, height, sourceInfo.ColorType, sourceInfo.AlphaType))
                Dim sourcePixels = source.GetPixels()
                Dim targetPixels = managed.GetPixels()
                If sourcePixels = IntPtr.Zero OrElse targetPixels = IntPtr.Zero Then
                    managed.Dispose()
                    Return ToSrgb(source, profile)
                End If
                Dim sourceStride = source.RowBytes
                Dim targetStride = managed.RowBytes
                Dim colorType = sourceInfo.ColorType
                Dim alphaType = sourceInfo.AlphaType
                Dim floatAlpha = If(alphaType = SKAlphaType.Opaque, SKAlphaType.Opaque, SKAlphaType.Unpremul)
                Dim failed = 0
                Dim bands = (height + GamutBandRows - 1) \ GamutBandRows
                Threading.Tasks.Parallel.For(0, bands,
                    Sub(band)
                        Dim y0 = band * GamutBandRows
                        Dim rows = Math.Min(GamutBandRows, height - y0)
                        Dim values(width * rows * 4 - 1) As Single
                        Dim pin = Runtime.InteropServices.GCHandle.Alloc(values, Runtime.InteropServices.GCHandleType.Pinned)
                        Try
                            Dim address = pin.AddrOfPinnedObject()
                            Dim floatInfo = New SKImageInfo(width, rows, SKColorType.RgbaF32, floatAlpha, _linearSrgb)
                            ' Versatz in Long: bei sehr grossen Bildern laege y0 * Zeilenlaenge
                            ' jenseits von Integer.
                            Dim sourceBand = New IntPtr(sourcePixels.ToInt64() + CLng(y0) * sourceStride)
                            Dim targetBand = New IntPtr(targetPixels.ToInt64() + CLng(y0) * targetStride)
                            Using pixels = New SKPixmap(New SKImageInfo(width, rows, colorType, alphaType, profile), sourceBand, sourceStride)
                                If Not pixels.ReadPixels(floatInfo, address, floatInfo.RowBytes) Then
                                    failed = 1
                                    Return
                                End If
                            End Using
                            CompressGamut(values, scales)
                            Using linear = New SKPixmap(floatInfo, address, floatInfo.RowBytes)
                                If Not linear.ReadPixels(New SKImageInfo(width, rows, colorType, alphaType, _srgb), targetBand, targetStride) Then
                                    failed = 1
                                End If
                            End Using
                        Finally
                            pin.Free()
                        End Try
                    End Sub)
                If failed <> 0 Then
                    DiagnosticLogService.LogAlways("Color.ToSrgbSoftGamut",
                        $"ReadPixels abgelehnt, es wird hart gewandelt: {DescribeProfile(profile)}")
                    managed.Dispose()
                    Return ToSrgb(source, profile)
                End If
                Return managed
            Catch ex As Exception
                DiagnosticLogService.LogException("Color.ToSrgbSoftGamut", ex)
                managed?.Dispose()
                Return ToSrgb(source, profile)
            End Try
        End Function

        ''' <summary>Je Kanal die Kennzahl der Kurve, oder 0, wenn der Quellraum in dieser Richtung
        ''' nicht ueber sRGB hinausreicht. Nothing, wenn Skia den Raum nicht wandeln kann.
        '''
        ''' Wie weit der Quellraum hinausreicht, wird nicht aus Tabellen genommen, sondern gemessen:
        ''' die Oberflaeche seines Wuerfels (je Seite 17 x 17 Punkte) durch dieselbe Wandlung wie das
        ''' Bild, und je Kanal der groesste Abstand vom Grau. Damit gilt dasselbe fuer nclx und fuer
        ''' jedes ICC-Profil, das Skia lesen kann.</summary>
        Private Shared Function GamutCompressionScales(profile As SKColorSpace) As Double()
            Const n As Integer = 17
            Dim count = 6 * n * n
            Dim samples(count * 4 - 1) As Single
            Dim index = 0
            For face = 0 To 5
                Dim axis = face Mod 3
                Dim fixedValue = If(face < 3, 0.0F, 1.0F)
                For i = 0 To n - 1
                    For j = 0 To n - 1
                        Dim u = CSng(i / (n - 1))
                        Dim v = CSng(j / (n - 1))
                        Dim rgb = If(axis = 0, {fixedValue, u, v}, If(axis = 1, {u, fixedValue, v}, {u, v, fixedValue}))
                        samples(index) = rgb(0)
                        samples(index + 1) = rgb(1)
                        samples(index + 2) = rgb(2)
                        samples(index + 3) = 1.0F
                        index += 4
                    Next
                Next
            Next

            Dim converted(samples.Length - 1) As Single
            Dim sourcePin = Runtime.InteropServices.GCHandle.Alloc(samples, Runtime.InteropServices.GCHandleType.Pinned)
            Dim targetPin = Runtime.InteropServices.GCHandle.Alloc(converted, Runtime.InteropServices.GCHandleType.Pinned)
            Try
                Dim sourceInfo = New SKImageInfo(count, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul, profile)
                Dim targetInfo = New SKImageInfo(count, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul, _linearSrgb)
                Using pixels = New SKPixmap(sourceInfo, sourcePin.AddrOfPinnedObject(), sourceInfo.RowBytes)
                    If Not pixels.ReadPixels(targetInfo, targetPin.AddrOfPinnedObject(), targetInfo.RowBytes) Then Return Nothing
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Color.GamutCompressionScales", ex)
                Return Nothing
            Finally
                sourcePin.Free()
                targetPin.Free()
            End Try

            Dim limits(2) As Double
            For i = 0 To converted.Length - 4 Step 4
                Dim achromatic = Math.Max(converted(i), Math.Max(converted(i + 1), converted(i + 2)))
                If achromatic <= 0.0001F Then Continue For
                For c = 0 To 2
                    limits(c) = Math.Max(limits(c), (achromatic - converted(i + c)) / achromatic)
                Next
            Next

            Dim scales(2) As Double
            For c = 0 To 2
                ' Ein Hauch Rand, damit der aeusserste Punkt sicher innen landet: das Raster der
                ' Messung trifft die Ecke des Quellraums, aber nicht jeden Punkt dazwischen.
                Dim limit = limits(c) + 0.005
                If limits(c) <= 1.0 Then
                    scales(c) = 0
                Else
                    scales(c) = (limit - GamutThreshold) /
                                Math.Pow(Math.Pow((1.0 - GamutThreshold) / (limit - GamutThreshold), -GamutPower) - 1.0, 1.0 / GamutPower)
                End If
            Next
            Return scales
        End Function

        ''' <summary>Die Abbildung selbst, auf RGBA in linearem sRGB (Gleitkomma, an Ort und
        ''' Stelle). Alpha bleibt unberuehrt.</summary>
        Private Shared Sub CompressGamut(values As Single(), scales As Double())
            For i = 0 To values.Length - 4 Step 4
                Dim r As Double = values(i)
                Dim g As Double = values(i + 1)
                Dim b As Double = values(i + 2)
                Dim achromatic = Math.Max(r, Math.Max(g, b))
                If achromatic <= 0 Then Continue For
                r = CompressComponent(r, achromatic, scales(0))
                g = CompressComponent(g, achromatic, scales(1))
                b = CompressComponent(b, achromatic, scales(2))
                ' Der hellste Kanal bleibt bei der Kompression stehen. Liegt er ueber 1, wird die
                ' Farbe bei gleicher Helligkeit (Y) entsaettigt, bis er 1 erreicht. Geteilt durch
                ' den hellsten Kanal liefe ein heller werdender Verlauf in eine Flaeche gleicher
                ' Werte (das Verhaeltnis der Kanaele bleibt ja gleich); so wird er heller und
                ' blasser, aber er laeuft weiter.
                If achromatic > 1.0 Then
                    Dim luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b
                    If luminance >= 1.0 Then
                        r = 1.0
                        g = 1.0
                        b = 1.0
                    Else
                        Dim factor = (1.0 - luminance) / (achromatic - luminance)
                        r = luminance + factor * (r - luminance)
                        g = luminance + factor * (g - luminance)
                        b = luminance + factor * (b - luminance)
                    End If
                End If
                values(i) = CSng(r)
                values(i + 1) = CSng(g)
                values(i + 2) = CSng(b)
            Next
        End Sub

        Private Shared Function CompressComponent(value As Double, achromatic As Double, scale As Double) As Double
            If scale <= 0 Then Return value
            Dim distance = (achromatic - value) / achromatic
            If distance <= GamutThreshold Then Return value
            Dim excess = distance - GamutThreshold
            Dim compressed = GamutThreshold + excess / Math.Pow(1.0 + Math.Pow(excess / scale, GamutPower), 1.0 / GamutPower)
            Return achromatic - compressed * achromatic
        End Function

        ''' <summary>Kurzbeschreibung eines Profils fuer das Protokoll.</summary>
        Public Shared Function DescribeProfile(profile As SKColorSpace) As String
            If profile Is Nothing Then Return "ohne Profil"
            If profile.IsSrgb Then Return "sRGB"
            Try
                ' Die Primaerfarben unterscheiden die gaengigen Faelle voneinander; einen Namen
                ' traegt ein ICC-Profil an dieser Stelle nicht mehr.
                Dim primaries As SKColorSpaceXyz = Nothing
                If profile.ToColorSpaceXyz(primaries) Then
                    Dim redX = primaries.Values(0)
                    ' Adobe RGB und Display P3 unterscheiden sich vor allem im roten Punkt.
                    If redX > 0.62F AndAlso redX < 0.66F Then Return "Adobe RGB oder aehnlich"
                    If redX >= 0.66F Then Return "Display P3 oder weiter"
                End If
            Catch
                ' Beschreibung ist Beiwerk; ein Fehler darf hier nichts kosten.
            End Try
            Return "abweichendes Profil"
        End Function
    End Class

End Namespace
