Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Nimmt einem JPEG beim Ausliefern alles, was ueber Ort, Personen und Kamera Auskunft gibt,
    ''' ohne das Bild neu zu kodieren (Freigaben per Link, BrowserBridgeShares.vb).
    '''
    ''' <para>WEG fallen: APP1 (EXIF mit GPS und Kamera, XMP mit Gesichtsbereichen und Namen),
    ''' APP13 (IPTC mit Ortsnamen), APP3 bis APP13 und APP15 (Herstellerdaten), das MPF in APP2 samt
    ''' der Bilder hinter dem Ende (Telefone haengen dort Tiefenkarten und Vorschauen an, jede mit
    ''' eigenem EXIF) und Kommentare. BLEIBEN: APP0 (JFIF), das ICC-Profil in APP2 und APP14 (Adobe,
    ''' bestimmt die Farbumrechnung) und alle Segmente, die das Bild selbst beschreiben.</para>
    '''
    ''' <para>DIE AUSRICHTUNG BLEIBT: sie steht im EXIF, und ohne sie laege ein Hochformat quer. An
    ''' die Stelle des EXIF tritt deshalb ein kleines EXIF mit nur dieser einen Angabe, gelesen
    ''' ueber den Decoder (SKCodec.EncodedOrigin).</para>
    ''' </summary>
    Public NotInheritable Class JpegMetadataStrip

        Private Sub New()
        End Sub

        ''' <summary>Das JPEG ohne Metadaten, Nothing, wenn es sich nicht als JPEG lesen laesst. Die
        ''' Bilddaten bleiben Byte fuer Byte, wie sie waren.</summary>
        Public Shared Function Strip(source As Byte()) As Byte()
            If source Is Nothing OrElse source.Length < 4 OrElse source(0) <> &HFF OrElse source(1) <> &HD8 Then Return Nothing
            Dim orientation = OrientationOf(source)
            Using output As New MemoryStream(source.Length)
                output.WriteByte(&HFF)
                output.WriteByte(&HD8)
                If orientation > 1 Then
                    Dim exif = OrientationSegment(orientation)
                    output.Write(exif, 0, exif.Length)
                End If
                Dim position = 2
                Do
                    ' Zum naechsten Marker; Fuellbytes (FF FF ...) vor ihm fallen weg.
                    If position >= source.Length - 1 OrElse source(position) <> &HFF Then Return Nothing
                    While position < source.Length - 1 AndAlso source(position + 1) = &HFF
                        position += 1
                    End While
                    If position >= source.Length - 1 Then Return Nothing
                    Dim marker = CInt(source(position + 1))
                    If marker = &HD9 Then
                        ' Ende des Bildes. Was danach kommt (weitere Bilder, Anhaenge), bleibt aussen vor.
                        output.WriteByte(&HFF)
                        output.WriteByte(&HD9)
                        Return output.ToArray()
                    End If
                    If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                        output.Write(source, position, 2)
                        position += 2
                        Continue Do
                    End If
                    If position + 3 >= source.Length Then Return Nothing
                    Dim length = (CInt(source(position + 2)) << 8) Or CInt(source(position + 3))
                    If length < 2 OrElse position + 2 + length > source.Length Then Return Nothing
                    Dim segmentEnd = position + 2 + length
                    If marker = &HDA Then
                        ' Kopf des Scans, dann die Bilddaten bis zum naechsten echten Marker: FF 00
                        ' ist ein maskiertes FF, FF D0 bis D7 sind Neustartmarken in den Daten.
                        Dim scan = segmentEnd
                        While scan < source.Length - 1
                            If source(scan) = &HFF Then
                                Dim following = CInt(source(scan + 1))
                                If following <> 0 AndAlso Not (following >= &HD0 AndAlso following <= &HD7) AndAlso following <> &HFF Then Exit While
                            End If
                            scan += 1
                        End While
                        If scan >= source.Length - 1 Then Return Nothing
                        output.Write(source, position, scan - position)
                        position = scan
                        Continue Do
                    End If
                    If Keeps(marker, source, position + 4, length - 2) Then output.Write(source, position, segmentEnd - position)
                    position = segmentEnd
                Loop
            End Using
        End Function

        ''' <summary>Bleibt das Segment? APP0, das ICC-Profil in APP2, APP14 und alles, was kein
        ''' APP-Segment und kein Kommentar ist.</summary>
        Private Shared Function Keeps(marker As Integer, source As Byte(), payload As Integer, payloadLength As Integer) As Boolean
            If marker = &HFE Then Return False
            If marker < &HE0 OrElse marker > &HEF Then Return True
            If marker = &HE0 OrElse marker = &HEE Then Return True
            If marker = &HE2 Then Return StartsWith(source, payload, payloadLength, "ICC_PROFILE")
            Return False
        End Function

        Private Shared Function StartsWith(source As Byte(), offset As Integer, available As Integer, text As String) As Boolean
            If available < text.Length Then Return False
            For i = 0 To text.Length - 1
                If source(offset + i) <> AscW(text(i)) Then Return False
            Next
            Return True
        End Function

        ''' <summary>Die Ausrichtung nach EXIF (1 bis 8), 1 wenn keine bekannt ist.</summary>
        Private Shared Function OrientationOf(source As Byte()) As Integer
            Try
                Using data = SKData.CreateCopy(source), codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return 1
                    Dim origin = CInt(codec.EncodedOrigin)
                    Return If(origin >= 1 AndAlso origin <= 8, origin, 1)
                End Using
            Catch
                Return 1
            End Try
        End Function

        ''' <summary>Ein APP1 mit einem EXIF, das nur die Ausrichtung traegt: TIFF-Kopf in
        ''' Motorola-Reihenfolge, ein Verzeichnis mit einem Eintrag (0x0112, SHORT, 1), kein weiteres.</summary>
        Friend Shared Function OrientationSegment(orientation As Integer) As Byte()
            Return New Byte() {
                &HFF, &HE1, 0, 34,
                &H45, &H78, &H69, &H66, 0, 0,
                &H4D, &H4D, 0, &H2A, 0, 0, 0, 8,
                0, 1,
                &H1, &H12, 0, 3, 0, 0, 0, 1, 0, CByte(orientation And &HFF), 0, 0,
                0, 0, 0, 0}
        End Function

    End Class

End Namespace
