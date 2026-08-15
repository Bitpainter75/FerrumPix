Imports System.Runtime.InteropServices
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
    ''' <para>Grenze: erkannt wird nur ein EINGEBETTETES ICC-Profil. Kameras, die ein Bild ohne
    ''' Profil ablegen und Adobe RGB allein ueber EXIF (ColorSpace=Uncalibrated plus
    ''' InteropIndex=R03) beschriften, sind damit nicht abgedeckt. Der Fall ist bekannt und bewusst
    ''' offen: er braucht den Metadatenleser statt des Decoders.</para>
    Public NotInheritable Class ColorManagementService
        Private Sub New()
        End Sub

        ''' <summary>Das Ziel aller Wandlungen. Skia legt dahinter einen festen Wert an, deshalb ist
        ''' das Halten billiger als das wiederholte Erzeugen.</summary>
        Private Shared ReadOnly _srgb As SKColorSpace = SKColorSpace.CreateSrgb()

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
                Dim managed As SKBitmap = Nothing
                Try
                    managed = New SKBitmap(managedInfo)
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

                    ' Den Farbraum wieder abstreifen: ab hier ist alles sRGB, und die Kette erwartet
                    ' farbraumlose Bitmaps. Eine einzelne Bitmap MIT Profil mitten in einer Kette
                    ' ohne Profile waere ein Sonderfall, den jede spaetere Stufe kennen muesste.
                    Dim plain = StripColorSpace(managed)
                    If plain Is Nothing Then Return managed
                    managed.Dispose()
                    Return plain
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

        ''' <summary>Eine farbraumlose Kopie mit demselben Bildaufbau - eine reine Speicherkopie
        ''' Zeile fuer Zeile.
        '''
        ''' Zeilenweise und nicht als ein Block, weil die Zeilenlaenge einer Bitmap nicht
        ''' zwingend Breite mal vier ist; Skia darf Zeilen auffuellen. Ein Block-Kopieren waere fuer
        ''' die hier erzeugten Bitmaps zwar richtig, aber es waere eine unerzwungene Annahme.</summary>
        Private Shared Function StripColorSpace(source As SKBitmap) As SKBitmap
            Dim plainInfo = New SKImageInfo(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim plain As SKBitmap = Nothing
            Try
                plain = New SKBitmap(plainInfo)
                Dim sourcePixels = source.GetPixels()
                Dim targetPixels = plain.GetPixels()
                If sourcePixels = IntPtr.Zero OrElse targetPixels = IntPtr.Zero Then
                    plain.Dispose()
                    Return Nothing
                End If

                Dim rowLength = Math.Min(source.RowBytes, plain.RowBytes)
                Dim row(rowLength - 1) As Byte
                For y As Integer = 0 To source.Height - 1
                    Marshal.Copy(IntPtr.Add(sourcePixels, y * source.RowBytes), row, 0, rowLength)
                    Marshal.Copy(row, 0, IntPtr.Add(targetPixels, y * plain.RowBytes), rowLength)
                Next
                Return plain
            Catch ex As Exception
                DiagnosticLogService.LogException("Color.StripColorSpace", ex)
                plain?.Dispose()
                Return Nothing
            End Try
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
