Imports System
Imports System.Globalization
Imports System.IO
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Liest Radiance HDR (.hdr, .rgbe), ein eigener Leser ohne Bibliothek. Das Format traegt je
    ''' Bildpunkt drei Mantissen und einen gemeinsamen Exponenten (RGBE, oder XYZE fuer CIE XYZ), also
    ''' lineares Licht mit grossem Umfang. Es geht wie EXR als <see cref="LinearLightImage"/> in die
    ''' RAW-Grundstufe.
    '''
    ''' Die Regeln:
    ''' - Kopf: Zeilen bis zur ersten leeren, beginnend mit "#?" (RADIANCE oder RGBE). FORMAT muss
    '''   32-bit_rle_rgbe oder 32-bit_rle_xyze sein; fehlt es, gilt RGBE.
    ''' - PRIMARIES wird angewandt (acht Zahlen: Rot, Gruen, Blau, Weiss als x y), mit Bradford auf
    '''   D65 nach sRGB. Ohne die Zeile gelten die Primaerfarben von sRGB; Radiance selbst nimmt dann
    '''   leicht andere mit Weiss E an, die meisten Schreiber legen aber sRGB-Werte ab.
    ''' - EXPOSURE wird NICHT angewandt: die Zeile sagt, mit welchem Faktor ein Programm die Werte
    '''   bereits multipliziert hat, und die Werte selbst sind das Licht. Ebenso darktable.
    ''' - Alle acht Achsenlagen der Aufloesungszeile (-Y N +X M ist die uebliche).
    ''' - Zeilen in allen drei Formen: ungepackt, die alte Lauflaenge (1 1 1 n wiederholt den
    '''   vorigen Bildpunkt) und die neue (2 2 Laenge, dann je Kanal Laeufe).
    ''' - Jede Laenge wird gegen das Dateiende und die Zeilenbreite geprueft, bevor sie wirkt; die
    '''   Masse gegen <see cref="LinearLightImage.MaxPixels"/>, bevor ein Feld entsteht.
    ''' - Die Datei kommt erst in den Speicher, wenn ihre Laenge passt: zuerst gegen die Grenze aus
    '''   MaxPixels (<see cref="MaxFileBytes"/>), dann, nach dem Kopf aus dem Dateistrom, gegen das,
    '''   was dieser Kopf hoechstens braucht. Ein Kopf mit zu grossen Massen oder eine zu lange Datei
    '''   kostet also nur das Lesen des Kopfes.
    ''' </summary>
    Friend NotInheritable Class RadianceHdrDecodeService

        Private Sub New()
        End Sub

        Private Const MaxHeaderBytes As Integer = 64 * 1024

        Public Shared Function IsSupportedRadiance(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".hdr", ".rgbe"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Was der Kopf sagt.</summary>
        Friend NotInheritable Class RadianceHeader
            Public Width As Integer
            Public Height As Integer
            Public IsXyz As Boolean
            ''' <summary>Acht Zahlen aus PRIMARIES, oder Nothing.</summary>
            Public Primaries As Double()
            ''' <summary>Die Achsen: erste die der Zeilen (Y oder X), dazu die Richtungen.</summary>
            Public RowsAlongY As Boolean
            Public FirstAxisPositive As Boolean
            Public SecondAxisPositive As Boolean
            ''' <summary>Zeilen und Punkte je Zeile, wie sie in der Datei liegen.</summary>
            Public ScanLines As Integer
            Public ScanLength As Integer
            Public DataOffset As Long
        End Class

        ''' <summary>Masse aus dem Kopf (Breite, Hoehe des aufrechten Bildes), oder (0, 0).</summary>
        Public Shared Function TryGetSize(path As String) As (Width As Integer, Height As Integer)
            Try
                Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 4096)
                    Dim header = ReadHeader(stream)
                    If header Is Nothing Then Return (0, 0)
                    Return (header.Width, header.Height)
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Hoechstens so viele Bytes belegt ein Bildpunkt in den Daten. Ungepackt und in
        ''' der alten Lauflaenge sind es vier (RGBE); die neue Lauflaenge kann im schlechtesten Fall
        ''' jeden Wert einzeln ablegen, mit einem Zaehlbyte davor, also zwei Bytes je Kanal und Punkt.</summary>
        Private Const MaxBytesPerPixel As Long = 8
        ''' <summary>Der Zeilenkopf der neuen Lauflaenge (2 2 hoch tief).</summary>
        Private Const ScanLineHeaderBytes As Long = 4
        ''' <summary>Laengste Aufloesungszeile, die <see cref="ReadLine"/> annimmt, samt Zeilenende.</summary>
        Private Const MaxResolutionLineBytes As Long = 4098

        ''' <summary>Die groesste Datei, die ein Bild mit <see cref="LinearLightImage.MaxPixels"/>
        ''' Bildpunkten ergeben kann: Kopf, Aufloesungszeile, Daten im schlechtesten Fall, dazu je
        ''' Zeile der Kopf der neuen Lauflaenge (hoechstens 65535 Zeilen). Gefragt nach der Laenge,
        ''' bevor ein Byte gelesen wird.</summary>
        Friend Const MaxFileBytes As Long = MaxHeaderBytes + MaxResolutionLineBytes +
                                            LinearLightImage.MaxPixels * MaxBytesPerPixel + 65535L * ScanLineHeaderBytes

        ''' <summary>Die groesste Datei zu DIESEM Kopf: Kopf und Daten im schlechtesten Fall.</summary>
        Friend Shared Function MaxFileBytesFor(header As RadianceHeader) As Long
            Return header.DataOffset + CLng(header.Width) * header.Height * MaxBytesPerPixel +
                   CLng(header.ScanLines) * ScanLineHeaderBytes
        End Function

        ''' <summary>Das Bild als lineares Licht, oder Nothing. Zuerst die Laenge der Datei gegen
        ''' <see cref="MaxFileBytes"/>, dann der Kopf aus dem Dateistrom (die Masse gegen
        ''' <see cref="LinearLightImage.MaxPixels"/>, dann die Laenge gegen das, was dieser Kopf
        ''' hoechstens braucht); erst danach wird die Datei in den Speicher gelesen.</summary>
        Public Shared Function TryReadLinear(path As String) As LinearLightImage
            If Not IsSupportedRadiance(path) Then Return Nothing
            Try
                Dim length = New FileInfo(path).Length
                If length > MaxFileBytes Then
                    DiagnosticLogService.LogAlways("RadianceHdr.Read", $"zu gross ({length} Bytes): {IO.Path.GetFileName(path)}")
                    Return Nothing
                End If
                Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 4096)
                    Dim header = ReadHeader(stream)
                    If header Is Nothing Then Return Nothing
                    If stream.Length > MaxFileBytesFor(header) Then
                        DiagnosticLogService.LogAlways("RadianceHdr.Read",
                            $"laenger als ein Bild von {header.Width}x{header.Height} sein kann ({stream.Length} Bytes): {IO.Path.GetFileName(path)}")
                        Return Nothing
                    End If
                    Dim bytes(CInt(stream.Length) - 1) As Byte
                    stream.Position = 0
                    stream.ReadExactly(bytes, 0, bytes.Length)
                    Return Decode(bytes, header)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("RadianceHdr.Read", ex)
                Return Nothing
            End Try
        End Function

        Friend Shared Function ReadHeader(stream As Stream) As RadianceHeader
            Dim first = ReadLine(stream)
            If first Is Nothing OrElse Not first.StartsWith("#?", StringComparison.Ordinal) Then Return Nothing
            Dim header As New RadianceHeader()
            Dim format = "32-bit_rle_rgbe"
            Do
                Dim line = ReadLine(stream)
                If line Is Nothing Then Return Nothing
                If line.Length = 0 Then Exit Do
                If line.StartsWith("FORMAT=", StringComparison.Ordinal) Then
                    format = line.Substring(7).Trim()
                ElseIf line.StartsWith("PRIMARIES=", StringComparison.Ordinal) Then
                    Dim parts = line.Substring(10).Split({" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
                    If parts.Length = 8 Then
                        Dim values(7) As Double
                        Dim ok = True
                        For i = 0 To 7
                            If Not Double.TryParse(parts(i), NumberStyles.Float, CultureInfo.InvariantCulture, values(i)) Then ok = False
                        Next
                        If ok Then header.Primaries = values
                    End If
                End If
                If stream.Position > MaxHeaderBytes Then Return Nothing
            Loop
            Select Case format
                Case "32-bit_rle_rgbe" : header.IsXyz = False
                Case "32-bit_rle_xyze" : header.IsXyz = True
                Case Else : Return Nothing
            End Select

            ' Die Aufloesungszeile, etwa "-Y 512 +X 768".
            Dim resolution = ReadLine(stream)
            If resolution Is Nothing Then Return Nothing
            Dim tokens = resolution.Split({" "c, ChrW(9)}, StringSplitOptions.RemoveEmptyEntries)
            If tokens.Length <> 4 OrElse tokens(0).Length <> 2 OrElse tokens(2).Length <> 2 Then Return Nothing
            Dim firstAxis = tokens(0)(1)
            Dim secondAxis = tokens(2)(1)
            If Not (firstAxis = "Y"c AndAlso secondAxis = "X"c) AndAlso Not (firstAxis = "X"c AndAlso secondAxis = "Y"c) Then Return Nothing
            If (tokens(0)(0) <> "+"c AndAlso tokens(0)(0) <> "-"c) OrElse (tokens(2)(0) <> "+"c AndAlso tokens(2)(0) <> "-"c) Then Return Nothing
            Dim firstCount, secondCount As Integer
            If Not Integer.TryParse(tokens(1), NumberStyles.None, CultureInfo.InvariantCulture, firstCount) OrElse
               Not Integer.TryParse(tokens(3), NumberStyles.None, CultureInfo.InvariantCulture, secondCount) Then Return Nothing
            header.RowsAlongY = firstAxis = "Y"c
            header.FirstAxisPositive = tokens(0)(0) = "+"c
            header.SecondAxisPositive = tokens(2)(0) = "+"c
            header.ScanLines = firstCount
            header.ScanLength = secondCount
            ' Zeilen entlang Y: so viele Zeilen wie die Hoehe, jede so lang wie die Breite.
            header.Width = If(header.RowsAlongY, secondCount, firstCount)
            header.Height = If(header.RowsAlongY, firstCount, secondCount)
            If Not LinearLightImage.SizeAllowed(header.Width, header.Height) Then Return Nothing
            header.DataOffset = stream.Position
            Return header
        End Function

        ''' <summary>Eine Zeile bis zum Zeilenende (ohne es), oder Nothing am Dateiende.</summary>
        Private Shared Function ReadLine(stream As Stream) As String
            Dim builder As New StringBuilder()
            Do
                Dim b = stream.ReadByte()
                If b < 0 Then Return If(builder.Length > 0, builder.ToString(), Nothing)
                If b = 10 Then Return builder.ToString()
                If builder.Length > 4096 Then Return Nothing
                builder.Append(ChrW(b))
            Loop
        End Function

        Private Shared Function Decode(bytes As Byte(), header As RadianceHeader) As LinearLightImage
            Dim width = header.Width
            Dim height = header.Height
            Dim rgb(CInt(CLng(width) * height * 3) - 1) As Single
            Dim scan(header.ScanLength * 4 - 1) As Byte
            Dim position = header.DataOffset
            For line = 0 To header.ScanLines - 1
                If Not ReadScanLine(bytes, position, scan, header.ScanLength) Then Return Nothing
                For i = 0 To header.ScanLength - 1
                    ' Wohin dieser Punkt im aufrechten Bild gehoert. Radiance zaehlt Y nach oben:
                    ' -Y heisst, die erste Zeile ist die oberste.
                    Dim x, y As Integer
                    If header.RowsAlongY Then
                        y = If(header.FirstAxisPositive, height - 1 - line, line)
                        x = If(header.SecondAxisPositive, i, width - 1 - i)
                    Else
                        x = If(header.FirstAxisPositive, line, width - 1 - line)
                        y = If(header.SecondAxisPositive, height - 1 - i, i)
                    End If
                    Dim o = (CLng(y) * width + x) * 3
                    Dim e = scan(i * 4 + 3)
                    If e = 0 Then
                        rgb(CInt(o)) = 0.0F : rgb(CInt(o) + 1) = 0.0F : rgb(CInt(o) + 2) = 0.0F
                    Else
                        ' Wie Radiance selbst (colr_color): Mantisse plus eine halbe Stufe.
                        Dim f = Math.Pow(2.0, e - 136)
                        rgb(CInt(o)) = CSng((scan(i * 4) + 0.5) * f)
                        rgb(CInt(o) + 1) = CSng((scan(i * 4 + 1) + 0.5) * f)
                        rgb(CInt(o) + 2) = CSng((scan(i * 4 + 2) + 0.5) * f)
                    End If
                Next
            Next

            Dim result As New LinearLightImage With {.Width = width, .Height = height, .Rgb = rgb}
            If header.IsXyz Then
                ' CIE XYZ nach linearem sRGB (D65); Radiance legt XYZ mit Weiss E ab, deshalb erst
                ' Bradford von E nach D65.
                result.ToSrgb = LinearLightImage.XyzToSrgb(1.0 / 3.0, 1.0 / 3.0)
                result.Description = "XYZE"
            ElseIf header.Primaries IsNot Nothing Then
                Dim p = header.Primaries
                result.ToSrgb = LinearLightImage.MatrixToSrgb(p(0), p(1), p(2), p(3), p(4), p(5), p(6), p(7))
                result.Description = If(result.ToSrgb Is Nothing, "RGBE, PRIMARIES wie sRGB", "RGBE mit PRIMARIES")
            Else
                result.Description = "RGBE"
            End If
            Return result
        End Function

        ''' <summary>Eine Zeile nach <paramref name="scan"/> (je Punkt vier Bytes), in einer der drei
        ''' Formen. False, wenn die Daten nicht reichen oder nicht passen.</summary>
        Private Shared Function ReadScanLine(bytes As Byte(), ByRef position As Long, scan As Byte(), length As Integer) As Boolean
            If position + 4 > bytes.Length Then Return False
            Dim b0 = bytes(CInt(position)), b1 = bytes(CInt(position) + 1), b2 = bytes(CInt(position) + 2), b3 = bytes(CInt(position) + 3)
            If length >= 8 AndAlso length <= &H7FFF AndAlso b0 = 2 AndAlso b1 = 2 AndAlso (b2 And &H80) = 0 Then
                ' Neue Lauflaenge: Kopf 2 2 hoch tief, dann je Kanal die Laeufe.
                If ((CInt(b2) << 8) Or b3) <> length Then Return False
                position += 4
                For channel = 0 To 3
                    Dim i = 0
                    While i < length
                        If position >= bytes.Length Then Return False
                        Dim count = CInt(bytes(CInt(position)))
                        position += 1
                        If count > 128 Then
                            count -= 128
                            If count = 0 OrElse i + count > length OrElse position >= bytes.Length Then Return False
                            Dim value = bytes(CInt(position))
                            position += 1
                            For k = 0 To count - 1
                                scan((i + k) * 4 + channel) = value
                            Next
                        Else
                            If count = 0 OrElse i + count > length OrElse position + count > bytes.Length Then Return False
                            For k = 0 To count - 1
                                scan((i + k) * 4 + channel) = bytes(CInt(position) + k)
                            Next
                            position += count
                        End If
                        i += count
                    End While
                Next
                Return True
            End If

            ' Ungepackt oder die alte Lauflaenge: Punkt fuer Punkt, 1 1 1 n wiederholt den vorigen.
            Dim index = 0
            Dim shift = 0
            While index < length
                If position + 4 > bytes.Length Then Return False
                Dim p = CInt(position)
                position += 4
                If bytes(p) = 1 AndAlso bytes(p + 1) = 1 AndAlso bytes(p + 2) = 1 Then
                    If index = 0 OrElse shift > 24 Then Return False
                    Dim repeat = CLng(bytes(p + 3)) << shift
                    If index + repeat > length Then Return False
                    For k = 0 To CInt(repeat) - 1
                        For c = 0 To 3
                            scan(index * 4 + c) = scan((index - 1) * 4 + c)
                        Next
                        index += 1
                    Next
                    shift += 8
                Else
                    scan(index * 4) = bytes(p)
                    scan(index * 4 + 1) = bytes(p + 1)
                    scan(index * 4 + 2) = bytes(p + 2)
                    scan(index * 4 + 3) = bytes(p + 3)
                    index += 1
                    shift = 0
                End If
            End While
            Return True
        End Function

    End Class

End Namespace
