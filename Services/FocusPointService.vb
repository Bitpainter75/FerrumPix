Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif
Imports MetadataExtractor.Formats.Exif.Makernotes
Imports MetadataExtractor.Formats.Jpeg

Namespace Services

    ''' <summary>Warum eine Aufnahme Fokusfelder liefert oder keine.</summary>
    Public Enum FocusPointState
        ''' <summary>Die Aufnahmedaten tragen gar keine Angabe, die hier gelesen wird.</summary>
        NoData
        ''' <summary>Mindestens ein Feld mit Lage.</summary>
        Found
        ''' <summary>Das Feld ist da, aber die Kamera hat nichts gemeldet (Handfokus, kein Feld
        ''' scharf, Platzhalterwerte).</summary>
        Empty
        ''' <summary>Die Lage bezieht sich auf eine groessere Flaeche als das gespeicherte Bild
        ''' (Ausschnittmodus der Kamera); wohin der Ausschnitt faellt, steht nicht dabei.</summary>
        CropMode
        ''' <summary>Die Kamera nennt nur die Nummer eines Messfelds, keine Lage im Bild.</summary>
        IndexOnly
    End Enum

    ''' <summary>Ein Fokusfeld als Anteile (0 bis 1) im UNGEDREHTEN Bild, so wie die Kamera es
    ''' ablegt. X und Y sind die Mitte. Ohne Groesse (<see cref="HasSize"/> False) ist nur die
    ''' Stelle bekannt.</summary>
    Public NotInheritable Class FocusPoint
        Public Sub New(x As Double, y As Double, width As Double, height As Double)
            Me.X = x
            Me.Y = y
            Me.Width = width
            Me.Height = height
        End Sub

        Public ReadOnly Property X As Double
        Public ReadOnly Property Y As Double
        Public ReadOnly Property Width As Double
        Public ReadOnly Property Height As Double

        Public ReadOnly Property HasSize As Boolean
            Get
                Return Width > 0 AndAlso Height > 0
            End Get
        End Property
    End Class

    ''' <summary>Was aus den Aufnahmedaten einer Datei gelesen wurde.</summary>
    Public NotInheritable Class FocusPointReading
        Friend Sub New(state As FocusPointState, maker As String, field As String, detail As String,
                       points As IReadOnlyList(Of FocusPoint), referenceWidth As Integer, referenceHeight As Integer)
            Me.State = state
            Me.Maker = If(maker, "")
            Me.Field = If(field, "")
            Me.Detail = If(detail, "")
            Me.Points = If(points, Array.Empty(Of FocusPoint)())
            Me.ReferenceWidth = referenceWidth
            Me.ReferenceHeight = referenceHeight
        End Sub

        Public ReadOnly Property State As FocusPointState
        ''' <summary>Fuer die Diagnose: aus wessen Daten gelesen wurde.</summary>
        Public ReadOnly Property Maker As String
        ''' <summary>Fuer die Diagnose: der Name des Felds.</summary>
        Public ReadOnly Property Field As String
        ''' <summary>Fuer die Diagnose: Zahlen zum Befund, kein Text fuer die Oberflaeche.</summary>
        Public ReadOnly Property Detail As String
        Public ReadOnly Property Points As IReadOnlyList(Of FocusPoint)
        ''' <summary>Die Flaeche, auf die sich die Lage bezieht, in Bildpunkten des ungedrehten
        ''' Bildes; 0, wenn die Kamera gleich Anteile ablegt. Dient dem Vergleich des
        ''' Seitenverhaeltnisses mit dem gezeigten Bild.</summary>
        Public ReadOnly Property ReferenceWidth As Integer
        Public ReadOnly Property ReferenceHeight As Integer
        ''' <summary>Die Ausrichtung aus den Aufnahmedaten, 1 bis 8. Die Felder liegen im
        ''' ungedrehten Bild und muessen damit aufgerichtet werden
        ''' (<see cref="FocusPointLayout.ToUpright"/>).</summary>
        Public Property Orientation As Integer = 1
    End Class

    ''' <summary>Liest die Fokusfelder einer Aufnahme aus den Daten der Kamera: wo sie scharfgestellt
    ''' hat. Ohne Decode, nur aus den Verzeichnissen von <see cref="ExifService.ReadMetadataDirectories"/>.
    '''
    ''' Jeder Hersteller legt das anders ab, und nur was an Dateien des Bestands belegt ist, wird
    ''' gelesen (Abdeckung und Gegenprobe: packaging/Diagnostics/AfProbe):
    '''   Canon      AFInfo2 (0x0026) und das aeltere AFInfo (0x0012): alle scharf gemeldeten Felder.
    '''   Nikon Z    AFInfo2 (0x00B7) ab Fassung 03: ein Feld in Bildpunkten.
    '''   Sony       FocusLocation (0x2027), Groesse aus FocusFrameSize (0x2037), wenn vorhanden.
    '''   Fujifilm   FocusPixel (0x1023): nur die Stelle.
    '''   Panasonic  AFPointPosition (0x004D), Groesse aus 0x00DE bei jungen Gehaeusen.
    '''   Olympus    AFPointSelected (0x0305), Groesse aus AFAreas (0x0304), wenn sie dazu passt.
    '''   sonst      Exif SubjectArea (0x9214), das vor allem Telefone schreiben.
    ''' Nicht gelesen werden Kameras, die nur die NUMMER eines Messfelds ablegen (Nikon mit
    ''' Spiegel, Pentax, aeltere Sony): dafuer braucht es je Gehaeuse eine Tabelle der Feldlagen.
    '''
    ''' Die Daten stammen aus fremden Dateien und koennen beliebig kaputt sein. Jede Lesung prueft
    ''' deshalb Laenge und Bereich, Zaehler haben eine feste Obergrenze, und keine Schleife haengt
    ''' an einem Wert aus der Datei, der nicht vorher begrenzt wurde. <see cref="Read"/> wirft nie.</summary>
    Public NotInheritable Class FocusPointService

        Private Sub New()
        End Sub

        ' Obergrenzen gegen kaputte Zaehler. Canons juengste Gehaeuse melden 1053 Felder, mehr als
        ' das Vierfache davon ist kein Messraster mehr. Gezeigt werden hoechstens MaxShownPoints:
        ' darueber ist es kein Hinweis mehr, sondern eine Flaeche.
        Private Const MaxCanonPoints As Integer = 4096
        Private Const MaxShownPoints As Integer = 256
        Private Const MaxArrayLength As Integer = 65536
        Private Const MaxPixelSize As Integer = 65535

        Private Const TagSubjectArea As Integer = &H9214
        Private Const TagExifImageWidth As Integer = &HA002
        Private Const TagExifImageHeight As Integer = &HA003
        Private Const TagImageWidth As Integer = &H100
        Private Const TagImageHeight As Integer = &H101
        Private Const TagOrientation As Integer = &H112

        Private Shared ReadOnly NoPoints As IReadOnlyList(Of FocusPoint) = Array.Empty(Of FocusPoint)()

        ''' <summary>Die Fokusfelder einer Datei. Nie Nothing; kann die Datei nicht gelesen werden,
        ''' ist das Ergebnis <see cref="FocusPointState.NoData"/>.</summary>
        Public Shared Function ReadFile(path As String) As FocusPointReading
            Try
                If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) Then Return Nothing_("", "", "keine Datei")
                Return Read(ExifService.ReadMetadataDirectories(path))
            Catch ex As Exception
                DiagnosticLogService.LogException("FocusPointService.ReadFile", ex)
                Return Nothing_("", "", ex.GetType().Name)
            End Try
        End Function

        ''' <summary>Die Fokusfelder aus schon gelesenen Verzeichnissen. Nie Nothing, wirft nie.</summary>
        Public Shared Function Read(directories As IEnumerable(Of Directory)) As FocusPointReading
            If directories Is Nothing Then Return Nothing_("", "", "keine Verzeichnisse")
            Dim list As List(Of Directory)
            Try
                list = directories.Where(Function(d) d IsNot Nothing).ToList()
            Catch ex As Exception
                Return Nothing_("", "", ex.GetType().Name)
            End Try

            Dim result As FocusPointReading = Nothing
            ' Jeder Leser in eigenem Try: ein kaputter Herstellerteil darf die allgemeine Angabe
            ' dahinter nicht mitreissen. Der erste mit einer Lage gewinnt; sonst bleibt der erste
            ' Befund stehen, der einen Grund nennt.
            For Each reader In Readers
                Dim one As FocusPointReading = Nothing
                Try
                    one = reader(list)
                Catch ex As Exception
                    DiagnosticLogService.LogException("FocusPointService.Read", ex)
                End Try
                If one Is Nothing Then Continue For
                If one.State = FocusPointState.Found Then
                    result = one
                    Exit For
                End If
                If result Is Nothing Then result = one
            Next
            If result Is Nothing Then result = Nothing_("", "", "kein Feld")
            Try
                result.Orientation = ReadOrientation(list)
            Catch ex As Exception
                result.Orientation = 1
            End Try
            Return result
        End Function

        Private Shared ReadOnly Readers As Func(Of List(Of Directory), FocusPointReading)() = {
            AddressOf ReadCanon, AddressOf ReadNikon, AddressOf ReadSony, AddressOf ReadFujifilm,
            AddressOf ReadPanasonic, AddressOf ReadOlympus, AddressOf ReadSubjectArea}

        ' ── Canon ─────────────────────────────────────────────────────────────────

        Private Const CanonAfInfo2Tag As Integer = &H26
        Private Const CanonAfInfo3Tag As Integer = &H3C
        ' Das aeltere AFInfo (0x0012) zerlegt MetadataExtractor selbst in Einzelfelder ab 0xD200.
        Private Const CanonAfInfoBase As Integer = &HD200

        ''' <summary>Canon: alle Felder, die die Kamera als scharf meldet. Die nur GEWAEHLTEN ohne
        ''' Schaerfemeldung bleiben weg: sie sagen, was eingestellt war, nicht wo die Schaerfe liegt.
        '''
        ''' Die Lagen zaehlen von der Bildmitte, in Bildpunkten von AFImageWidth und AFImageHeight.
        ''' In welche Richtung Y laeuft, haengt am Feld und am Gehaeuse
        ''' (<see cref="CanonVerticalDirection"/>). Ist die Richtung fuer ein Gehaeuse nicht belegt
        ''' und liegt ein Feld nicht auf der waagerechten Mittelachse, wird nichts gezeigt statt
        ''' einer Stelle, die ebenso gut gespiegelt sein koennte.</summary>
        Private Shared Function ReadCanon(directories As List(Of Directory)) As FocusPointReading
            Dim canon = directories.OfType(Of CanonMakernoteDirectory)().FirstOrDefault()
            If canon Is Nothing Then Return Nothing
            Dim model = ReadModel(directories)

            ' Derselbe Aufbau steht je nach Gehaeuse unter 0x0026 oder unter 0x003C (die
            ' spiegellosen der Reihe M und die Kompakten derselben Jahre).
            Dim fieldName = "AFInfo2"
            Dim raw = ToIntegers(canon.GetObject(CanonAfInfo2Tag))
            If raw Is Nothing OrElse raw.Length < 8 Then
                raw = ToIntegers(canon.GetObject(CanonAfInfo3Tag))
                fieldName = "AFInfo3"
            End If
            If raw IsNot Nothing AndAlso raw.Length >= 8 Then
                Dim direction = CanonVerticalDirection(model, fieldName = "AFInfo3")
                Dim count = raw(2)
                Dim imageWidth = raw(6), imageHeight = raw(7)
                If count <= 0 OrElse count > MaxCanonPoints OrElse Not IsPixelSize(imageWidth) OrElse Not IsPixelSize(imageHeight) Then
                    Return Empty_("Canon", fieldName,$"felder={count} bezug={imageWidth}x{imageHeight}")
                End If
                Dim words = (count + 15) \ 16
                ' Breiten, Hoehen, X, Y je Feld, dann die Bitfelder "scharf" und "gewaehlt".
                If raw.Length < 8 + 4 * count + words Then
                    Return Empty_("Canon", fieldName,$"felder={count} laenge={raw.Length}")
                End If
                Dim widths = 8, heights = 8 + count, xs = 8 + 2 * count, ys = 8 + 3 * count, inFocus = 8 + 4 * count
                Dim points As New List(Of FocusPoint)()
                Dim reported = 0
                For i = 0 To count - 1
                    If (raw(inFocus + i \ 16) >> (i Mod 16) And 1) = 0 Then Continue For
                    reported += 1
                    If points.Count >= MaxShownPoints Then Continue For
                    Dim y = ToSigned16(raw(ys + i))
                    If direction = 0 AndAlso IsOffAxis(y, imageHeight) Then Return IndexOnly_("Canon", fieldName, $"felder={count} richtung unbelegt")
                    AddCentered(points, ToSigned16(raw(xs + i)), y, raw(widths + i), raw(heights + i),
                                imageWidth, imageHeight, direction >= 0)
                Next
                If points.Count = 0 Then Return Empty_("Canon", fieldName,$"felder={count} scharf={reported}")
                Return Found_("Canon", fieldName,$"felder={count} scharf={reported}", points, imageWidth, imageHeight)
            End If

            If canon.ContainsTag(CanonAfInfoBase) Then
                Dim count, imageWidth, imageHeight, areaWidth, areaHeight As Integer
                If Not canon.TryGetInt32(CanonAfInfoBase, count) OrElse
                   Not canon.TryGetInt32(CanonAfInfoBase + 4, imageWidth) OrElse
                   Not canon.TryGetInt32(CanonAfInfoBase + 5, imageHeight) Then Return Empty_("Canon", "AFInfo", "unvollstaendig")
                If Not canon.TryGetInt32(CanonAfInfoBase + 6, areaWidth) Then areaWidth = 0
                If Not canon.TryGetInt32(CanonAfInfoBase + 7, areaHeight) Then areaHeight = 0
                Dim xs = ToIntegers(canon.GetObject(CanonAfInfoBase + 8))
                Dim ys = ToIntegers(canon.GetObject(CanonAfInfoBase + 9))
                Dim bits = ToIntegers(canon.GetObject(CanonAfInfoBase + 10))
                If count <= 0 OrElse count > MaxCanonPoints OrElse Not IsPixelSize(imageWidth) OrElse Not IsPixelSize(imageHeight) OrElse
                   xs Is Nothing OrElse ys Is Nothing OrElse bits Is Nothing Then
                    Return Empty_("Canon", "AFInfo", $"felder={count} bezug={imageWidth}x{imageHeight}")
                End If
                Dim direction = CanonVerticalDirection(model, False)
                Dim points As New List(Of FocusPoint)()
                Dim reported = 0
                For i = 0 To Math.Min(count, Math.Min(xs.Length, ys.Length)) - 1
                    If i \ 16 >= bits.Length Then Exit For
                    If ((bits(i \ 16) And &HFFFF) >> (i Mod 16) And 1) = 0 Then Continue For
                    reported += 1
                    If points.Count >= MaxShownPoints Then Continue For
                    Dim y = ToSigned16(ys(i))
                    If direction = 0 AndAlso IsOffAxis(y, imageHeight) Then Return IndexOnly_("Canon", "AFInfo", $"felder={count} richtung unbelegt")
                    AddCentered(points, ToSigned16(xs(i)), y, areaWidth, areaHeight, imageWidth, imageHeight, direction >= 0)
                Next
                If points.Count = 0 Then Return Empty_("Canon", "AFInfo", $"felder={count} scharf={reported}")
                Return Found_("Canon", "AFInfo", $"felder={count} scharf={reported}", points, imageWidth, imageHeight)
            End If
            Return Nothing
        End Function

        ' Kompakte, deren Richtung an Dateien belegt ist (nach oben, wie die Reihe EOS). Es sind
        ' die mit dem juengeren Dateiformat; aeltere Kompakte legen das Feld genauso ab, aber
        ' fuer sie fand sich im Bestand keine Aufnahme, an der sich oben und unten trennen liessen.
        Private Shared ReadOnly CanonCompactsCountingUp As String() = {
            "PowerShot G5 X Mark II", "PowerShot G7 X Mark III", "PowerShot V1"}

        ''' <summary>In welche Richtung Canons Y zaehlt: 1 nach oben, -1 nach unten (wie das Bild),
        ''' 0 nicht belegt. Belegt ueber die Schaerfe am Feld gegen das senkrecht gespiegelte Feld
        ''' und an einzelnen Aufnahmen von Auge (packaging/Diagnostics/AfProbe):
        '''   unter 0x003C (AFInfo3) zaehlt Y nach unten, auch bei der Reihe EOS M;
        '''   unter 0x0026 und im aelteren AFInfo zaehlt die Reihe EOS nach oben, ebenso die
        '''   Kompakten aus <see cref="CanonCompactsCountingUp"/>.</summary>
        Friend Shared Function CanonVerticalDirection(model As String, isAfInfo3 As Boolean) As Integer
            If isAfInfo3 Then Return -1
            Dim name = If(model, "")
            If name.IndexOf("EOS", StringComparison.OrdinalIgnoreCase) >= 0 Then Return 1
            For Each compact In CanonCompactsCountingUp
                If name.EndsWith(compact, StringComparison.OrdinalIgnoreCase) Then Return 1
            Next
            Return 0
        End Function

        ''' <summary>Liegt ein Feld merklich neben der waagerechten Mittelachse? Bis zu einem
        ''' Hundertstel der Hoehe gilt es als auf der Achse: manche Kompakte melden die Bildmitte
        ''' mit einem Bildpunkt Versatz, und so weit daneben laege auch die falsche Richtung noch
        ''' im Feld.</summary>
        Private Shared Function IsOffAxis(y As Integer, imageHeight As Integer) As Boolean
            Return Math.Abs(CLng(y)) * 100 > imageHeight
        End Function

        ''' <summary>Ein Feld aus einer Lage, die von der Bildmitte zaehlt.</summary>
        Private Shared Sub AddCentered(points As List(Of FocusPoint), x As Integer, y As Integer, width As Integer, height As Integer,
                                       imageWidth As Integer, imageHeight As Integer, yUp As Boolean)
            Dim cx = 0.5 + x / CDbl(imageWidth)
            Dim cy = If(yUp, 0.5 - y / CDbl(imageHeight), 0.5 + y / CDbl(imageHeight))
            If Not IsFraction(cx) OrElse Not IsFraction(cy) Then Return
            Dim w = SizeFraction(width, imageWidth), h = SizeFraction(height, imageHeight)
            If w <= 0 OrElse h <= 0 Then
                w = 0
                h = 0
            End If
            points.Add(New FocusPoint(cx, cy, w, h))
        End Sub

        ' ── Nikon ─────────────────────────────────────────────────────────────────

        Private Const NikonAfInfo2Tag As Integer = &HB7
        Private Const NikonAfInfoTag As Integer = &H88

        ''' <summary>Nikon: die spiegellosen Gehaeuse (AFInfo2 ab Fassung 03) legen Bezugsgroesse,
        ''' Mitte und Groesse des Felds als sechs Zahlen zu 16 Bit ab, niederwertiges Byte voran,
        ''' bei Fassung 03 ab Byte 0x2A, bei Fassung 04 ab 0x3E. Aeltere Fassungen nennen nur die
        ''' Nummer eines Messfelds.</summary>
        Private Shared Function ReadNikon(directories As List(Of Directory)) As FocusPointReading
            Dim nikon = directories.OfType(Of NikonType2MakernoteDirectory)().FirstOrDefault()
            If nikon Is Nothing Then Return Nothing
            Dim data = TryCast(nikon.GetObject(NikonAfInfo2Tag), Byte())
            If data Is Nothing OrElse data.Length < 4 Then
                Return If(nikon.ContainsTag(NikonAfInfoTag), IndexOnly_("Nikon", "AFInfo", ""), Nothing)
            End If
            Dim version = "" & ChrW(data(0)) & ChrW(data(1)) & ChrW(data(2)) & ChrW(data(3))
            Dim offset As Integer
            If version.StartsWith("03", StringComparison.Ordinal) Then
                offset = &H2A
            ElseIf version.StartsWith("04", StringComparison.Ordinal) Then
                offset = &H3E
            Else
                Return IndexOnly_("Nikon", "AFInfo2", "fassung=" & SafeText(version))
            End If
            Dim imageWidth = ReadUInt16(data, offset, False), imageHeight = ReadUInt16(data, offset + 2, False)
            Dim x = ReadUInt16(data, offset + 4, False), y = ReadUInt16(data, offset + 6, False)
            Dim width = ReadUInt16(data, offset + 8, False), height = ReadUInt16(data, offset + 10, False)
            Dim detail = $"fassung={SafeText(version)} bezug={imageWidth}x{imageHeight} mitte={x},{y} feld={width}x{height}"
            ' Ohne Bezugsgroesse hat die Kamera keine Lage abgelegt: bei der selbsttaetigen
            ' Feldwahl steht stattdessen nur das Raster der benutzten Messfelder da.
            If imageWidth = 0 AndAlso imageHeight = 0 Then Return IndexOnly_("Nikon", "AFInfo2", detail)
            If Not IsPixelSize(imageWidth) OrElse Not IsPixelSize(imageHeight) OrElse x <= 0 OrElse y <= 0 OrElse
               x > imageWidth OrElse y > imageHeight Then Return Empty_("Nikon", "AFInfo2", detail)
            Dim w = SizeFraction(width, imageWidth), h = SizeFraction(height, imageHeight)
            If w <= 0 OrElse h <= 0 Then
                w = 0
                h = 0
            End If
            Return Found_("Nikon", "AFInfo2", detail, {New FocusPoint(x / CDbl(imageWidth), y / CDbl(imageHeight), w, h)},
                          imageWidth, imageHeight)
        End Function

        ' ── Sony ──────────────────────────────────────────────────────────────────

        Private Const SonyFocusLocationTag As Integer = &H2027
        Private Const SonyFocusFrameSizeTag As Integer = &H2037
        Private Const SonyAfPointSelectedTag As Integer = &H201E

        ''' <summary>Sony: Bezugsbreite, Bezugshoehe, X und Y der Mitte. Im Ausschnittmodus (etwa
        ''' APS-C an einem Kleinbildgehaeuse) bleibt der Bezug die VOLLE Sensorflaeche, das Bild
        ''' ist aber nur der Ausschnitt; dann wird nichts gezeigt statt einer Stelle daneben.
        ''' Ein Bezug KLEINER als das Bild ist nur ein anderer Massstab und gilt.</summary>
        Private Shared Function ReadSony(directories As List(Of Directory)) As FocusPointReading
            Dim sony = directories.OfType(Of SonyType1MakernoteDirectory)().FirstOrDefault()
            If sony Is Nothing Then Return Nothing
            Dim location = ToIntegers(sony.GetObject(SonyFocusLocationTag))
            If location Is Nothing OrElse location.Length <> 4 Then
                Return If(sony.ContainsTag(SonyAfPointSelectedTag), IndexOnly_("Sony", "AFPointSelected", ""), Nothing)
            End If
            Dim referenceWidth = location(0), referenceHeight = location(1), x = location(2), y = location(3)
            Dim image = ReadImageSize(directories)
            Dim detail = $"bezug={referenceWidth}x{referenceHeight} mitte={x},{y} bild={image.Width}x{image.Height}"
            If Not IsPixelSize(referenceWidth) OrElse Not IsPixelSize(referenceHeight) OrElse (x <= 0 AndAlso y <= 0) OrElse
               x > referenceWidth OrElse y > referenceHeight Then Return Empty_("Sony", "FocusLocation", detail)
            If IsLargerThanImage(referenceWidth, referenceHeight, image.Width, image.Height) Then
                Return New FocusPointReading(FocusPointState.CropMode, "Sony", "FocusLocation", detail, NoPoints, referenceWidth, referenceHeight)
            End If
            Dim w = 0.0, h = 0.0
            Dim frame = TryCast(sony.GetObject(SonyFocusFrameSizeTag), Byte())
            ' Breite und Hoehe zu 16 Bit, dann ein Byte "gueltig".
            If frame IsNot Nothing AndAlso frame.Length >= 5 AndAlso frame(4) <> 0 Then
                w = SizeFraction(ReadUInt16(frame, 0, False), referenceWidth)
                h = SizeFraction(ReadUInt16(frame, 2, False), referenceHeight)
                If w <= 0 OrElse h <= 0 Then
                    w = 0
                    h = 0
                End If
            End If
            Return Found_("Sony", "FocusLocation", detail, {New FocusPoint(x / CDbl(referenceWidth), y / CDbl(referenceHeight), w, h)},
                          referenceWidth, referenceHeight)
        End Function

        ''' <summary>Die Ausschnittregel: der Bezug ist in einer Richtung merklich groesser als das
        ''' Bild. Ohne bekannte Bildgroesse laesst sich nichts sagen, und es gilt nicht.</summary>
        Friend Shared Function IsLargerThanImage(referenceWidth As Integer, referenceHeight As Integer,
                                                 imageWidth As Integer, imageHeight As Integer) As Boolean
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return False
            Return referenceWidth > imageWidth * 1.02 OrElse referenceHeight > imageHeight * 1.02
        End Function

        ' ── Fujifilm ──────────────────────────────────────────────────────────────

        Private Const FujifilmFocusPixelTag As Integer = &H1023

        ''' <summary>Fujifilm: X und Y in Bildpunkten des Bildes, das die Kamera selbst gerechnet
        ''' hat (in einer RAF das eingebettete), vom linken oberen Rand. Eine Groesse gibt es nicht.</summary>
        Private Shared Function ReadFujifilm(directories As List(Of Directory)) As FocusPointReading
            Dim fuji = directories.OfType(Of FujifilmMakernoteDirectory)().FirstOrDefault()
            If fuji Is Nothing Then Return Nothing
            Dim pixel = ToIntegers(fuji.GetObject(FujifilmFocusPixelTag))
            If pixel Is Nothing OrElse pixel.Length <> 2 Then Return Nothing
            Dim image = ReadImageSize(directories)
            Dim detail = $"pixel={pixel(0)},{pixel(1)} bild={image.Width}x{image.Height}"
            If image.Width <= 0 OrElse image.Height <= 0 OrElse (pixel(0) <= 0 AndAlso pixel(1) <= 0) OrElse
               pixel(0) > image.Width OrElse pixel(1) > image.Height Then Return Empty_("Fujifilm", "FocusPixel", detail)
            Return Found_("Fujifilm", "FocusPixel", detail,
                          {New FocusPoint(pixel(0) / CDbl(image.Width), pixel(1) / CDbl(image.Height), 0, 0)},
                          image.Width, image.Height)
        End Function

        ' ── Panasonic ─────────────────────────────────────────────────────────────

        Private Const PanasonicAfPointPositionTag As Integer = &H4D
        Private Const PanasonicAfAreaSizeTag As Integer = &HDE

        ''' <summary>Panasonic: zwei Brueche von 0 bis 1 fuer die Mitte. Ohne Feld stehen dort
        ''' Platzhalter weit ausserhalb. Die Groesse (ebenfalls als Anteile) schreiben nur junge
        ''' Gehaeuse.</summary>
        Private Shared Function ReadPanasonic(directories As List(Of Directory)) As FocusPointReading
            Dim panasonic = directories.OfType(Of PanasonicMakernoteDirectory)().FirstOrDefault()
            If panasonic Is Nothing OrElse Not panasonic.ContainsTag(PanasonicAfPointPositionTag) Then Return Nothing
            Dim position = ToDoubles(panasonic.GetObject(PanasonicAfPointPositionTag))
            If position Is Nothing OrElse position.Length <> 2 Then Return Empty_("Panasonic", "AFPointPosition", "kein Bruchpaar")
            Dim detail = FormattableString.Invariant($"mitte={position(0):0.####},{position(1):0.####}")
            If Not IsFraction(position(0)) OrElse Not IsFraction(position(1)) OrElse (position(0) = 0 AndAlso position(1) = 0) Then
                Return Empty_("Panasonic", "AFPointPosition", detail)
            End If
            Dim w = 0.0, h = 0.0
            Dim size = ToDoubles(panasonic.GetObject(PanasonicAfAreaSizeTag))
            If size IsNot Nothing AndAlso size.Length = 2 AndAlso size(0) > 0 AndAlso size(0) <= 1 AndAlso size(1) > 0 AndAlso size(1) <= 1 Then
                w = size(0)
                h = size(1)
            End If
            Return Found_("Panasonic", "AFPointPosition", detail, {New FocusPoint(position(0), position(1), w, h)}, 0, 0)
        End Function

        ' ── Olympus ───────────────────────────────────────────────────────────────

        Private Const OlympusAfAreasTag As Integer = &H304
        Private Const OlympusAfPointSelectedTag As Integer = &H305

        ''' <summary>Olympus und OM System: AFPointSelected traegt fuenf Brueche, der zweite und
        ''' dritte sind X und Y als Anteile. AFAreas packt je Feld vier Bytes (links, oben, rechts,
        ''' unten auf 0 bis 255); die Groesse wird daraus nur genommen, wenn genau ein Feld
        ''' eingetragen ist und seine Mitte auf der gemeldeten Stelle liegt. Sonst bleibt es bei
        ''' der Stelle allein. Junge Gehaeuse lassen beide Felder leer.</summary>
        Private Shared Function ReadOlympus(directories As List(Of Directory)) As FocusPointReading
            Dim settings = directories.OfType(Of OlympusCameraSettingsMakernoteDirectory)().FirstOrDefault()
            If settings Is Nothing Then Return Nothing
            Dim hasSelected = settings.ContainsTag(OlympusAfPointSelectedTag)
            Dim hasAreas = settings.ContainsTag(OlympusAfAreasTag)
            If Not hasSelected AndAlso Not hasAreas Then Return Nothing

            Dim areas As New List(Of (Left As Double, Top As Double, Right As Double, Bottom As Double))()
            Dim packed = ToLongs(settings.GetObject(OlympusAfAreasTag))
            If packed IsNot Nothing Then
                For i = 0 To Math.Min(packed.Length, 64) - 1
                    Dim value = packed(i) And &HFFFFFFFFL
                    If value = 0 Then Continue For
                    Dim left = ((value >> 24) And 255) / 255.0, top = ((value >> 16) And 255) / 255.0
                    Dim right = ((value >> 8) And 255) / 255.0, bottom = (value And 255) / 255.0
                    If right > left AndAlso bottom > top Then areas.Add((left, top, right, bottom))
                Next
            End If

            Dim selected = ToDoubles(settings.GetObject(OlympusAfPointSelectedTag))
            If selected IsNot Nothing AndAlso selected.Length >= 3 AndAlso IsFraction(selected(1)) AndAlso IsFraction(selected(2)) AndAlso
               (selected(1) > 0 OrElse selected(2) > 0) Then
                Dim x = selected(1), y = selected(2)
                Dim w = 0.0, h = 0.0
                If areas.Count = 1 Then
                    Dim area = areas(0)
                    If Math.Abs((area.Left + area.Right) / 2 - x) < 0.03 AndAlso Math.Abs((area.Top + area.Bottom) / 2 - y) < 0.03 Then
                        w = area.Right - area.Left
                        h = area.Bottom - area.Top
                    End If
                End If
                Return Found_("Olympus", "AFPointSelected", FormattableString.Invariant($"mitte={x:0.####},{y:0.####} felder={areas.Count}"),
                              {New FocusPoint(x, y, w, h)}, 0, 0)
            End If
            If areas.Count > 0 Then
                Dim points = areas.Take(MaxShownPoints).
                    Select(Function(a) New FocusPoint((a.Left + a.Right) / 2, (a.Top + a.Bottom) / 2, a.Right - a.Left, a.Bottom - a.Top)).ToList()
                Return Found_("Olympus", "AFAreas", $"felder={areas.Count}", points, 0, 0)
            End If
            Return Empty_("Olympus", "AFPointSelected", "leer")
        End Function

        ' ── Exif SubjectArea ──────────────────────────────────────────────────────

        ''' <summary>Die allgemeine Angabe aus dem Exif-Teil: zwei Zahlen sind eine Stelle, drei
        ''' eine Stelle mit Durchmesser, vier eine Mitte mit Breite und Hoehe, alles in Bildpunkten
        ''' des ungedrehten Bildes.</summary>
        Private Shared Function ReadSubjectArea(directories As List(Of Directory)) As FocusPointReading
            For Each directory In directories.OfType(Of ExifDirectoryBase)()
                If Not directory.ContainsTag(TagSubjectArea) Then Continue For
                Dim area = ToIntegers(directory.GetObject(TagSubjectArea))
                If area Is Nothing OrElse area.Length < 2 OrElse area.Length > 4 Then Continue For
                Dim image = ReadImageSize(directories)
                Dim detail = $"werte={String.Join(",", area)} bild={image.Width}x{image.Height}"
                If image.Width <= 0 OrElse image.Height <= 0 OrElse area(0) < 0 OrElse area(1) < 0 OrElse
                   area(0) > image.Width OrElse area(1) > image.Height OrElse (area(0) = 0 AndAlso area(1) = 0) Then
                    Return Empty_("Exif", "SubjectArea", detail)
                End If
                Dim w = 0.0, h = 0.0
                If area.Length = 3 Then
                    w = SizeFraction(area(2), image.Width)
                    h = SizeFraction(area(2), image.Height)
                ElseIf area.Length = 4 Then
                    w = SizeFraction(area(2), image.Width)
                    h = SizeFraction(area(3), image.Height)
                End If
                If w <= 0 OrElse h <= 0 Then
                    w = 0
                    h = 0
                End If
                Return Found_("Exif", "SubjectArea", detail,
                              {New FocusPoint(area(0) / CDbl(image.Width), area(1) / CDbl(image.Height), w, h)}, image.Width, image.Height)
            Next
            Return Nothing
        End Function

        ' ── Gemeinsames ───────────────────────────────────────────────────────────

        ''' <summary>Die Groesse des gespeicherten Bildes in Bildpunkten, ungedreht: zuerst die
        ''' Angabe im Exif-Teil, sonst die groesste Bildgroesse eines Verzeichnisses (eine DNG
        ''' traegt die Exif-Angabe oft nicht), sonst der JPEG-Kopf.</summary>
        Friend Shared Function ReadImageSize(directories As List(Of Directory)) As (Width As Integer, Height As Integer)
            Dim bestWidth = 0, bestHeight = 0
            For Each directory In directories.OfType(Of ExifDirectoryBase)()
                Dim w, h As Integer
                If directory.TryGetInt32(TagExifImageWidth, w) AndAlso directory.TryGetInt32(TagExifImageHeight, h) AndAlso
                   IsPixelSize(w) AndAlso IsPixelSize(h) AndAlso w > bestWidth Then
                    bestWidth = w
                    bestHeight = h
                End If
            Next
            If bestWidth > 0 Then Return (bestWidth, bestHeight)
            For Each directory In directories.OfType(Of ExifDirectoryBase)()
                Dim w, h As Integer
                If directory.TryGetInt32(TagImageWidth, w) AndAlso directory.TryGetInt32(TagImageHeight, h) AndAlso
                   IsPixelSize(w) AndAlso IsPixelSize(h) AndAlso w > bestWidth Then
                    bestWidth = w
                    bestHeight = h
                End If
            Next
            If bestWidth > 0 Then Return (bestWidth, bestHeight)
            For Each jpeg In directories.OfType(Of JpegDirectory)()
                Dim w, h As Integer
                If jpeg.TryGetInt32(JpegDirectory.TagImageWidth, w) AndAlso jpeg.TryGetInt32(JpegDirectory.TagImageHeight, h) AndAlso
                   IsPixelSize(w) AndAlso IsPixelSize(h) Then Return (w, h)
            Next
            Return (0, 0)
        End Function

        Private Shared Function ReadOrientation(directories As List(Of Directory)) As Integer
            For Each ifd0 In directories.OfType(Of ExifIfd0Directory)()
                Dim value As Integer
                If ifd0.TryGetInt32(TagOrientation, value) AndAlso value >= 1 AndAlso value <= 8 Then Return value
            Next
            Return 1
        End Function

        Private Shared Function ReadModel(directories As List(Of Directory)) As String
            For Each ifd0 In directories.OfType(Of ExifIfd0Directory)()
                Dim model = ifd0.GetString(ExifDirectoryBase.TagModel)
                If Not String.IsNullOrWhiteSpace(model) Then Return model
            Next
            Return ""
        End Function

        Private Shared Function IsPixelSize(value As Integer) As Boolean
            Return value > 0 AndAlso value <= MaxPixelSize
        End Function

        Private Shared Function IsFraction(value As Double) As Boolean
            Return Not Double.IsNaN(value) AndAlso value >= 0.0 AndAlso value <= 1.0
        End Function

        ''' <summary>Eine Kantenlaenge als Anteil, 0 wenn sie fehlt oder ueber das Bild hinausgeht.</summary>
        Private Shared Function SizeFraction(size As Integer, reference As Integer) As Double
            If size <= 0 OrElse reference <= 0 OrElse size > reference Then Return 0
            Return size / CDbl(reference)
        End Function

        Private Shared Function ToSigned16(value As Integer) As Integer
            Dim low = value And &HFFFF
            Return If(low >= 32768, low - 65536, low)
        End Function

        ''' <summary>Zwei Bytes als Zahl ohne Vorzeichen, -1 jenseits des Endes.</summary>
        Private Shared Function ReadUInt16(data As Byte(), offset As Integer, bigEndian As Boolean) As Integer
            If data Is Nothing OrElse offset < 0 OrElse offset > data.Length - 2 Then Return -1
            Return If(bigEndian,
                      (CInt(data(offset)) << 8) Or data(offset + 1),
                      (CInt(data(offset + 1)) << 8) Or data(offset))
        End Function

        ''' <summary>Nur druckbare Zeichen: die Fassung kommt aus der Datei und geht ins Protokoll.</summary>
        Private Shared Function SafeText(text As String) As String
            Return New String(If(text, "").Select(Function(c) If(c >= " "c AndAlso c < ChrW(127), c, "?"c)).ToArray())
        End Function

        ''' <summary>Ein Feld als ganze Zahlen, gleich in welchem Zahlentyp es abgelegt ist.
        ''' Nothing bei anderem Inhalt oder unsinniger Laenge. Werte jenseits von Integer werden
        ''' auf dessen Grenzen geklemmt statt ueberzulaufen.</summary>
        Private Shared Function ToIntegers(value As Object) As Integer()
            Dim longs = ToLongs(value)
            If longs Is Nothing Then Return Nothing
            Dim result(longs.Length - 1) As Integer
            For i = 0 To longs.Length - 1
                result(i) = CInt(Math.Max(Integer.MinValue, Math.Min(Integer.MaxValue, longs(i))))
            Next
            Return result
        End Function

        Private Shared Function ToLongs(value As Object) As Long()
            Dim source = TryCast(value, Array)
            If source Is Nothing Then
                If TypeOf value Is Rational OrElse TypeOf value Is String OrElse value Is Nothing Then Return Nothing
                If Not IsWholeNumber(value) Then Return Nothing
                Return {Convert.ToInt64(value, Globalization.CultureInfo.InvariantCulture)}
            End If
            If source.Rank <> 1 OrElse source.Length > MaxArrayLength Then Return Nothing
            Dim result(source.Length - 1) As Long
            For i = 0 To source.Length - 1
                Dim item = source.GetValue(i)
                If Not IsWholeNumber(item) Then Return Nothing
                result(i) = If(TypeOf item Is ULong, CLng(Math.Min(DirectCast(item, ULong), CULng(Long.MaxValue))),
                               Convert.ToInt64(item, Globalization.CultureInfo.InvariantCulture))
            Next
            Return result
        End Function

        Private Shared Function IsWholeNumber(value As Object) As Boolean
            Return TypeOf value Is Byte OrElse TypeOf value Is SByte OrElse TypeOf value Is Short OrElse TypeOf value Is UShort OrElse
                   TypeOf value Is Integer OrElse TypeOf value Is UInteger OrElse TypeOf value Is Long OrElse TypeOf value Is ULong
        End Function

        ''' <summary>Ein Feld als Kommazahlen; ein Bruch mit Nenner null wird NaN.</summary>
        Private Shared Function ToDoubles(value As Object) As Double()
            Dim source = TryCast(value, Array)
            If source Is Nothing OrElse source.Rank <> 1 OrElse source.Length > MaxArrayLength Then Return Nothing
            Dim result(source.Length - 1) As Double
            For i = 0 To source.Length - 1
                Dim item = source.GetValue(i)
                If TypeOf item Is Rational Then
                    Dim rational = DirectCast(item, Rational)
                    result(i) = If(rational.Denominator = 0, Double.NaN, rational.Numerator / CDbl(rational.Denominator))
                ElseIf IsWholeNumber(item) OrElse TypeOf item Is Single OrElse TypeOf item Is Double Then
                    result(i) = Convert.ToDouble(item, Globalization.CultureInfo.InvariantCulture)
                Else
                    Return Nothing
                End If
            Next
            Return result
        End Function

        Private Shared Function Nothing_(maker As String, field As String, detail As String) As FocusPointReading
            Return New FocusPointReading(FocusPointState.NoData, maker, field, detail, NoPoints, 0, 0)
        End Function

        Private Shared Function Empty_(maker As String, field As String, detail As String) As FocusPointReading
            Return New FocusPointReading(FocusPointState.Empty, maker, field, detail, NoPoints, 0, 0)
        End Function

        Private Shared Function IndexOnly_(maker As String, field As String, detail As String) As FocusPointReading
            Return New FocusPointReading(FocusPointState.IndexOnly, maker, field, detail, NoPoints, 0, 0)
        End Function

        Private Shared Function Found_(maker As String, field As String, detail As String, points As IReadOnlyList(Of FocusPoint),
                                       referenceWidth As Integer, referenceHeight As Integer) As FocusPointReading
            Return New FocusPointReading(FocusPointState.Found, maker, field, detail, points, referenceWidth, referenceHeight)
        End Function

    End Class

End Namespace
